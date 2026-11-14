using System.Text.Json;
using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Runs;

/// <summary>
/// AUT-08: the trigger loop applies each cron trigger's misfire policy after downtime (M6 task 2.2). The triggers
/// fire yearly on 1 January and were last due in 2020, so seven fire times (2020–2026) are missed and none is on time.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class MisfireTests(PostgresFixture pg)
{
    private const string Yearly = "0 0 1 1 *";
    private const string Since2020 = "'2020-01-01T00:00:00Z'";
    private static readonly int ThisYear = DateTime.UtcNow.Year;

    [Fact]
    public async Task FireOnce_after_an_outage_creates_one_run_for_the_latest_missed_time()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        await DrainDueTriggersAsync();
        var trigger = await TriggerLoopTests.AddCronTriggerAsync(pg, s.AutomationId, Yearly, "UTC", Since2020, "fire_once");

        var pass = await new CronTriggers(pg.DataSource).FireDueAsync(CancellationToken.None);

        Assert.Equal(1, pass.Created);
        Assert.Equal([ThisYear], await RunYearsAsync(s.AutomationId));
        var missed = await MisfiredAsync(s.AutomationId);
        Assert.Equal(ThisYear - 2020, missed.GetProperty("count").GetInt32());
        Assert.Equal(2020, missed.GetProperty("first").GetDateTimeOffset().Year);
        Assert.Equal(ThisYear - 1, missed.GetProperty("last").GetDateTimeOffset().Year);
        Assert.Equal(new DateTimeOffset(ThisYear + 1, 1, 1, 0, 0, 0, TimeSpan.Zero),
            await HostData.ScalarAsync<DateTimeOffset>(pg, "SELECT next_fire_at FROM automation_triggers WHERE id = @id", ("id", trigger)));
    }

    [Fact]
    public async Task Skip_after_an_outage_creates_no_run_and_records_the_miss()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        await DrainDueTriggersAsync();
        var trigger = await TriggerLoopTests.AddCronTriggerAsync(pg, s.AutomationId, Yearly, "UTC", Since2020, "skip");

        var pass = await new CronTriggers(pg.DataSource).FireDueAsync(CancellationToken.None);

        Assert.Equal(0, pass.Created);
        Assert.Empty(await RunYearsAsync(s.AutomationId));
        Assert.Equal(ThisYear - 2020 + 1, (await MisfiredAsync(s.AutomationId)).GetProperty("count").GetInt32());
        Assert.True(await HostData.ScalarAsync<bool>(pg,
            "SELECT next_fire_at > now() AND last_fired_at IS NULL FROM automation_triggers WHERE id = @id", ("id", trigger)));
    }

    [Fact]
    public async Task CatchUp_creates_the_3_most_recent_missed_runs()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        await DrainDueTriggersAsync();
        await TriggerLoopTests.AddCronTriggerAsync(pg, s.AutomationId, Yearly, "UTC", Since2020, "catch_up", catchUpLimit: 3);

        var pass = await new CronTriggers(pg.DataSource).FireDueAsync(CancellationToken.None);

        Assert.Equal(3, pass.Created);
        Assert.Equal([ThisYear - 2, ThisYear - 1, ThisYear], await RunYearsAsync(s.AutomationId));
        var missed = await MisfiredAsync(s.AutomationId);
        Assert.Equal(ThisYear - 2020 - 2, missed.GetProperty("count").GetInt32());
        Assert.Equal(ThisYear - 3, missed.GetProperty("last").GetDateTimeOffset().Year);
    }

    [Fact]
    public async Task Slightly_late_fire_is_not_a_misfire()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        await DrainDueTriggersAsync();
        await TriggerLoopTests.AddCronTriggerAsync(pg, s.AutomationId, Yearly, "UTC", dueSecondsAgo: 20, misfirePolicy: "skip");

        var pass = await new CronTriggers(pg.DataSource).FireDueAsync(CancellationToken.None);

        Assert.Equal(1, pass.Created);
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg,
            "SELECT count(*) FROM run_events WHERE workflow_run_id = @id AND type = 'trigger.misfired'", ("id", s.AutomationId)));
    }

    [Fact]
    public async Task Catch_up_runs_follow_the_concurrency_policy()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "skip", maxRuns: 1);
        await DrainDueTriggersAsync();
        await TriggerLoopTests.AddCronTriggerAsync(pg, s.AutomationId, Yearly, "UTC", Since2020, "catch_up", catchUpLimit: 3);

        var pass = await new CronTriggers(pg.DataSource).FireDueAsync(CancellationToken.None);

        Assert.Equal((1, 2), (pass.Created, pass.Skipped));
        Assert.Equal([ThisYear - 2], await RunYearsAsync(s.AutomationId));
        Assert.Equal(2L, await HostData.ScalarAsync<long>(pg,
            "SELECT count(*) FROM run_events WHERE workflow_run_id = @id AND type = 'run.skipped'", ("id", s.AutomationId)));
    }

    /// <summary>
    /// Fires the due triggers other tests in the collection left behind, so the pass totals below count this test's
    /// trigger only (the trigger pass fires every due trigger in the shared database).
    /// </summary>
    private async Task DrainDueTriggersAsync() => await new CronTriggers(pg.DataSource).FireDueAsync(CancellationToken.None);

    /// <summary>The fire-time year of each run of the automation, oldest first.</summary>
    private async Task<List<int>> RunYearsAsync(Guid automationId)
    {
        var years = new List<int>();
        await using var cmd = pg.DataSource.CreateCommand("""
            SELECT (e.data->>'fireTime')::timestamptz
            FROM workflow_runs r JOIN run_events e ON e.workflow_run_id = r.id AND e.type = 'run.created'
            WHERE r.automation_id = @id ORDER BY 1
            """);
        cmd.Parameters.AddWithValue("id", automationId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            years.Add(reader.GetFieldValue<DateTimeOffset>(0).UtcDateTime.Year);
        return years;
    }

    private async Task<JsonElement> MisfiredAsync(Guid automationId) =>
        JsonSerializer.Deserialize<JsonElement>(await HostData.ScalarAsync<string>(pg,
            "SELECT data::text FROM run_events WHERE workflow_run_id = @id AND type = 'trigger.misfired'", ("id", automationId)));
}
