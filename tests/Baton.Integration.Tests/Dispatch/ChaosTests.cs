using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>
/// M3 exit criterion and task 5.1 (ORC-09, NFR-ExecutionGuarantee): an agent killed mid-task loses its
/// lease, the task is retried on another agent and the run completes — real agents, real orchestrator loops.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ChaosTests(PostgresFixture pg)
{
    [Fact]
    public async Task Agent_killed_mid_task_is_retried_elsewhere_and_the_run_completes()
    {
        await using var orchestrator = await OrchestratorHost.StartAsync(pg, d =>
        {
            d.ReaperInterval = TimeSpan.FromMilliseconds(500);
            d.RetryPromoterInterval = TimeSpan.FromMilliseconds(200);
            d.ClaimRecheck = TimeSpan.FromMilliseconds(500);
        }, loops: true);
        var (groupId, group) = await HostData.CreateGroupAsync(pg);
        await HostData.ExecuteAsync(pg, "UPDATE host_groups SET lease_seconds = 3 WHERE id = @id", ("id", groupId));
        var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);
        var (definitionId, _) = await DispatchScenario.RegisterAsync(pg, new ChaosPipeline());
        await using var first = await AgentProcess.StartAsync(orchestrator.Address, group, token);
        await using var second = await AgentProcess.StartAsync(orchestrator.Address, group, token);
        var agents = new[] { first, second };
        await EventuallyAsync(() => HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM hosts WHERE host_group_id = @g", ("g", groupId)),
            count => count == 2, "both agents registered");

        var runs = (RunMaterializer)orchestrator.Services.GetService(typeof(RunMaterializer))!;
        var runId = await runs.StartAsync(
            new NewRun(definitionId, groupId, 50, """{"batch":"2026-10-26"}""", [], "manual", Guid.NewGuid().ToString()),
            CancellationToken.None);

        // Kill whichever agent holds the hanging first attempt of `transform`.
        var holder = await EventuallyAsync(async () => await HostData.ScalarAsync<string>(pg, """
            SELECT COALESCE((SELECT h.name FROM task_runs t JOIN hosts h ON h.id = t.lease_host_id
                             WHERE t.workflow_run_id = @run AND t.task_key = 'transform' AND t.status = 'running'), '')
            """, ("run", runId)), name => name.Length > 0, "transform running");
        var victim = agents.Single(a => a.Name == holder);
        victim.Kill();

        await EventuallyAsync(async () => await HostData.ScalarAsync<string>(pg, "SELECT status FROM workflow_runs WHERE id = @id", ("id", runId)),
            status => status is "succeeded" or "failed", "the run to finish", TimeSpan.FromSeconds(60));

        Assert.Equal("succeeded", await HostData.ScalarAsync<string>(pg, "SELECT status FROM workflow_runs WHERE id = @id", ("id", runId)));
        Assert.Equal("\"published 20\"", await HostData.ScalarAsync<string>(pg,
            "SELECT output::text FROM task_runs WHERE workflow_run_id = @run AND task_key = 'publish'", ("run", runId)));
        var attempts = await HostData.ScalarAsync<string>(pg, """
            SELECT string_agg(a.attempt || ':' || a.outcome || ':' || h.name, ',' ORDER BY a.attempt)
            FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id JOIN hosts h ON h.id = a.host_id
            WHERE t.workflow_run_id = @run AND t.task_key = 'transform'
            """, ("run", runId));
        var survivor = agents.Single(a => a != victim).Name;
        Assert.Equal($"1:lost:{victim.Name},2:succeeded:{survivor}", attempts);
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var value = await read();
            if (done(value))
                return value;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {what}; last value: {value}.");
            await Task.Delay(100);
        }
    }
}
