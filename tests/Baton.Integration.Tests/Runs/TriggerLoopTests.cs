using Baton.Domain.Automations;
using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Runs;

/// <summary>
/// ORC-01, AUT-04, NFR-TriggerAccuracy: the trigger loop's SQL (M4 tasks 2.2, 2.6; exit criterion "two replicas
/// never create two runs for the same fire time").
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class TriggerLoopTests(PostgresFixture pg)
{
    [Fact]
    public async Task Two_replicas_firing_the_same_due_trigger_create_one_run()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        var trigger = await AddCronTriggerAsync(s.AutomationId, "0 2 * * *", "Asia/Singapore", dueSecondsAgo: 1);
        var replicas = Enumerable.Range(0, 4).Select(_ => new CronTriggers(pg.DataSource)).ToList();

        await Task.WhenAll(replicas.Select(r => Task.Run(() => r.FireDueAsync(CancellationToken.None))));

        Assert.Equal(1L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM workflow_runs WHERE automation_id = @id", ("id", s.AutomationId)));
        Assert.True(await HostData.ScalarAsync<bool>(pg,
            "SELECT next_fire_at > now() AND last_fired_at < now() FROM automation_triggers WHERE id = @id", ("id", trigger)));
    }

    [Fact]
    public async Task A_fire_time_another_replica_already_turned_into_a_run_is_not_fired_again()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        var trigger = await AddCronTriggerAsync(s.AutomationId, "*/5 * * * *", "UTC", dueSecondsAgo: 1);
        var fireTime = await HostData.ScalarAsync<DateTimeOffset>(pg, "SELECT next_fire_at FROM automation_triggers WHERE id = @id", ("id", trigger));
        // A replica created the run, then crashed before moving next_fire_at.
        var key = $"automation:{s.AutomationId}:trigger:{trigger}:{fireTime.UtcDateTime:O}";
        Assert.Equal(FireOutcome.Created, (await s.FireAsync(key: key)).Outcome);

        var pass = await new CronTriggers(pg.DataSource).FireDueAsync(CancellationToken.None);

        Assert.Equal(0, pass.Created);
        Assert.Equal(1L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM workflow_runs WHERE automation_id = @id", ("id", s.AutomationId)));
        Assert.True(await HostData.ScalarAsync<bool>(pg, "SELECT next_fire_at > now() FROM automation_triggers WHERE id = @id", ("id", trigger)));
    }

    [Fact]
    public async Task After_downtime_a_trigger_fires_once_and_moves_to_the_next_future_time()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        var trigger = await AddCronTriggerAsync(s.AutomationId, "* * * * *", "UTC", dueSecondsAgo: 600);
        var triggers = new CronTriggers(pg.DataSource);

        await triggers.FireDueAsync(CancellationToken.None);
        await triggers.FireDueAsync(CancellationToken.None);

        Assert.Equal(1L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM workflow_runs WHERE automation_id = @id", ("id", s.AutomationId)));
        Assert.True(await HostData.ScalarAsync<bool>(pg, "SELECT next_fire_at > now() FROM automation_triggers WHERE id = @id", ("id", trigger)));
    }

    [Fact]
    public async Task A_trigger_whose_firing_fails_does_not_hold_up_the_others()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        var broken = await AddCronTriggerAsync(s.AutomationId, "* * * * *", "UTC", dueSecondsAgo: 5);
        await HostData.ExecuteAsync(pg, "UPDATE automation_triggers SET cron_expr = 'not cron' WHERE id = @id", ("id", broken));
        var other = await s.AddAutomationAsync(policy: "allow", maxRuns: 10);
        await AddCronTriggerAsync(other, "* * * * *", "UTC", dueSecondsAgo: 1);

        var pass = await new CronTriggers(pg.DataSource).FireDueAsync(CancellationToken.None);

        Assert.Contains(pass.Failed, f => f.TriggerId == broken);
        Assert.Equal(1L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM workflow_runs WHERE automation_id = @id", ("id", other)));
        await HostData.ExecuteAsync(pg, "UPDATE automation_triggers SET enabled = false WHERE id = @id", ("id", broken));
    }

    [Fact]
    public async Task Due_trigger_has_its_run_within_five_seconds_with_the_loop_running()
    {
        await using var orchestrator = await OrchestratorHost.StartAsync(pg, loops: true);
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        var trigger = await AddCronTriggerAsync(s.AutomationId, "0 2 * * *", "UTC", dueSecondsAgo: -2); // due in 2 s

        try
        {
            var lag = await Eventually.UntilAsync(() => HostData.ScalarAsync<double>(pg, """
                    SELECT COALESCE((SELECT extract(epoch FROM r.created_at - t.last_fired_at)
                                     FROM workflow_runs r JOIN automation_triggers t ON t.automation_id = r.automation_id
                                     WHERE t.id = @id), -1)::float8
                    """, ("id", trigger)),
                lag => lag >= 0, "the run of the due trigger", TimeSpan.FromSeconds(15));

            Assert.InRange(lag, 0, 5);
        }
        finally
        {
            await HostData.ExecuteAsync(pg, "UPDATE automations SET enabled = false WHERE id = @id", ("id", s.AutomationId));
        }
    }

    /// <param name="dueSecondsAgo">Sets <c>next_fire_at</c> that far in the past (negative: in the future).</param>
    private async Task<Guid> AddCronTriggerAsync(Guid automationId, string expression, string zone, int dueSecondsAgo)
    {
        var id = Guid.CreateVersion7();
        await HostData.ExecuteAsync(pg, """
            INSERT INTO automation_triggers (id, automation_id, kind, cron_expr, time_zone, next_fire_at)
            VALUES (@id, @automation, 'cron', @expr, @zone, now() - make_interval(secs => @ago))
            """, ("id", id), ("automation", automationId), ("expr", expression), ("zone", zone), ("ago", (double)dueSecondsAgo));
        Assert.NotNull(CronSchedule.TryCreate(expression, zone, out _));
        return id;
    }
}
