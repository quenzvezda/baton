using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>ORC-07 / NFR-ExecutionGuarantee: concurrent claimers never lease one task twice (M3 exit criterion).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ClaimRaceTests(PostgresFixture pg)
{
    private const int Claimers = 8;
    private const int Tasks = 1_000;

    [Fact]
    public async Task Eight_parallel_claimers_lease_each_of_1000_tasks_exactly_once()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        await HostData.ExecuteAsync(pg, """
            INSERT INTO workflow_runs (id, definition_id, status, priority, host_group_id, parameters, trigger_kind, idempotency_key, started_at)
            SELECT gen_random_uuid(), @definitionId, 'running', 50, @groupId, '{}', 'manual', gen_random_uuid()::text, now()
            FROM generate_series(1, @tasks)
            """, ("definitionId", s.DefinitionId), ("groupId", s.GroupId), ("tasks", Tasks));
        await HostData.ExecuteAsync(pg, """
            INSERT INTO task_runs (id, workflow_run_id, task_key, status, trigger_rule, upstream_remaining, max_attempts, timeout,
                                   priority, effective_priority, host_group_id, definition_hash, queued_at)
            SELECT gen_random_uuid(), r.id, 'a', 'queued', 'all_success', 0, 3, interval '1 hour', 50, 50, @groupId, @hash, now()
            FROM workflow_runs r WHERE r.host_group_id = @groupId
            """, ("groupId", s.GroupId), ("hash", s.Hash));
        var hosts = new List<Guid>();
        for (var i = 0; i < Claimers; i++)
            hosts.Add(await s.AddHostAsync(slots: Tasks));

        using var start = new ManualResetEventSlim();
        var claimers = hosts.Select(host => Task.Run(async () =>
        {
            start.Wait();
            var leased = new List<(Guid Task, long Token)>();
            while (true)
            {
                var claimed = (await s.ClaimAsync(host, freeSlots: 7)).Tasks;
                if (claimed.Count == 0)
                    return leased;
                leased.AddRange(claimed.Select(c => (c.TaskRunId, c.LeaseToken)));
            }
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(claimers);

        var all = results.SelectMany(r => r).ToList();
        Assert.Equal(Tasks, all.Count);
        Assert.Equal(Tasks, all.Select(l => l.Task).Distinct().Count());
        Assert.Equal(Tasks, all.Select(l => l.Token).Distinct().Count());
        Assert.All(results, r => Assert.NotEmpty(r));
        Assert.Equal(Tasks, await HostData.ScalarAsync<long>(pg, """
            SELECT count(*) FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id WHERE t.host_group_id = @groupId
            """, ("groupId", s.GroupId)));
    }
}
