using Baton.Domain.Runs;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Orchestrator.Agents;
using Baton.Orchestrator.Dispatch;
using Baton.Orchestrator.Loops;
using Baton.Persistence.Queue;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>ORC-06: the aging loop raises the effective priority of waiting tasks (M6 tasks 3.1–3.3).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AgingTests(PostgresFixture pg)
{
    private AgingLoop Loop() =>
        new(pg.DataSource, Options.Create(new DispatchOptions()), TestMetrics.Create(), NullLogger<AgingLoop>.Instance);

    private Task QueuedSinceAsync(Guid taskRunId, TimeSpan ago) =>
        HostData.ExecuteAsync(pg, "UPDATE task_runs SET queued_at = now() - @ago WHERE id = @id", ("ago", ago), ("id", taskRunId));

    private Task<short> EffectiveAsync(Guid taskRunId) =>
        HostData.ScalarAsync<short>(pg, "SELECT effective_priority FROM task_runs WHERE id = @id", ("id", taskRunId));

    [Fact]
    public async Task Queued_task_ages_one_point_per_step()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var runId = await s.StartRunAsync(priority: 40);
        var task = await s.TaskAsync(runId, "a");

        await QueuedSinceAsync(task.Id, TimeSpan.FromMinutes(12));
        Assert.True(await Loop().RunOnceAsync(CancellationToken.None));
        Assert.Equal(42, await EffectiveAsync(task.Id)); // spec "Waiting task ages"

        await QueuedSinceAsync(task.Id, TimeSpan.FromHours(3));
        Assert.True(await Loop().RunOnceAsync(CancellationToken.None));
        Assert.Equal(60, await EffectiveAsync(task.Id)); // spec "Cap"
    }

    [Fact]
    public async Task Ceiling_is_100()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var task = await s.TaskAsync(await s.StartRunAsync(priority: 95), "a");

        await QueuedSinceAsync(task.Id, TimeSpan.FromHours(1));
        await Loop().RunOnceAsync(CancellationToken.None);

        Assert.Equal(100, await EffectiveAsync(task.Id));
    }

    [Fact]
    public async Task Unchanged_rows_are_not_written()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var aged = await s.TaskAsync(await s.StartRunAsync(priority: 40), "a");
        var fresh = await s.TaskAsync(await s.StartRunAsync(priority: 40), "a");
        await QueuedSinceAsync(aged.Id, TimeSpan.FromMinutes(12));
        await Loop().RunOnceAsync(CancellationToken.None);
        var versionsBefore = await RowVersionsAsync(aged.Id, fresh.Id);

        await Loop().RunOnceAsync(CancellationToken.None);

        // xmin changes on every write; neither the aged task (already at 42) nor the fresh one (still 40) is rewritten.
        Assert.Equal(versionsBefore, await RowVersionsAsync(aged.Id, fresh.Id));
        Assert.Equal((42, 40), (await EffectiveAsync(aged.Id), await EffectiveAsync(fresh.Id)));
    }

    [Fact]
    public async Task Aged_task_overtakes_a_newer_higher_base_task()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var old = await s.TaskAsync(await s.StartRunAsync(priority: 40), "a");
        await s.StartRunAsync(priority: 55);
        await QueuedSinceAsync(old.Id, TimeSpan.FromHours(2));
        var host = await s.AddHostAsync(slots: 1);

        await Loop().RunOnceAsync(CancellationToken.None);
        var claimed = await s.ClaimOneAsync(host);

        Assert.Equal(old.Id, claimed.TaskRunId); // spec "Aged task overtakes": 60 beats 55
    }

    [Fact]
    public async Task Retry_starts_again_from_base_priority()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a").Retry(3, TimeSpan.Zero));
        var runId = await s.StartRunAsync(priority: 40);
        var task = await s.TaskAsync(runId, "a");
        await QueuedSinceAsync(task.Id, TimeSpan.FromHours(2));
        await Loop().RunOnceAsync(CancellationToken.None);
        Assert.Equal(60, await EffectiveAsync(task.Id));

        var host = await s.AddHostAsync();
        Assert.True(await s.CompleteAsync(await s.ClaimOneAsync(host), Baton.Domain.Runs.AttemptOutcome.Failed));
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET not_before = now() - interval '1 second' WHERE id = @id", ("id", task.Id));
        await s.Promoter.PromoteAsync(CancellationToken.None);

        Assert.Equal("queued", (await s.TaskAsync(runId, "a")).Status);
        Assert.Equal(40, await EffectiveAsync(task.Id)); // spec "Retry starts again from base"
    }

    [Fact]
    public void Invalid_aging_settings_fail_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Dispatch:AgingStep"] = "00:00:10" })
            .Build();
        using var services = new ServiceCollection().AddAgentProtocol(configuration).BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<DispatchOptions>>().Value);

        Assert.Contains("AgingStep", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aging_ticks_during_concurrent_claims_skip_locked_rows()
    {
        const int tasks = 400, claimers = 6;
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        for (var i = 0; i < tasks; i++)
            await s.StartRunAsync(priority: 10 + (i % 50));
        await HostData.ExecuteAsync(pg,
            "UPDATE task_runs SET queued_at = now() - make_interval(mins => (random() * 120)::int) WHERE host_group_id = @g",
            ("g", s.GroupId));
        var hosts = new List<Guid>();
        for (var i = 0; i < claimers; i++)
            hosts.Add(await s.AddHostAsync(slots: tasks));

        using var stop = new CancellationTokenSource();
        var aging = Task.Run(async () =>
        {
            var ticks = 0;
            while (!stop.IsCancellationRequested)
            {
                await Loop().RunOnceAsync(CancellationToken.None);
                ticks++;
            }

            return ticks;
        });
        var claims = hosts.Select(host => Task.Run(async () =>
        {
            var leased = 0;
            while (true)
            {
                var claimed = (await s.ClaimAsync(host, freeSlots: 5)).Tasks;
                if (claimed.Count == 0)
                    return leased;
                leased += claimed.Count;
            }
        })).ToList();

        var total = (await Task.WhenAll(claims)).Sum();
        await stop.CancelAsync();
        Assert.True(await aging > 0);

        Assert.Equal(tasks, total);
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg, """
            SELECT count(*) FROM (SELECT task_run_id FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id
                                  WHERE t.host_group_id = @g GROUP BY task_run_id HAVING count(*) <> 1) twice
            """, ("g", s.GroupId)));
    }

    private async Task<(string, string)> RowVersionsAsync(Guid a, Guid b) =>
        (await HostData.ScalarAsync<string>(pg, "SELECT xmin::text FROM task_runs WHERE id = @id", ("id", a)),
         await HostData.ScalarAsync<string>(pg, "SELECT xmin::text FROM task_runs WHERE id = @id", ("id", b)));
}
