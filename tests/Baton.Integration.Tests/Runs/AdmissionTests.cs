using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Runs;

/// <summary>ORC-02: admission under limits, pass-over and run start (M4 tasks 3.1, 3.2; exit criterion).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AdmissionTests(PostgresFixture pg)
{
    [Fact]
    public async Task Group_full_keeps_a_new_run_pending_until_one_finishes()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10, groupMaxRuns: 5);
        var host = await s.Dispatch.AddHostAsync(slots: 10);
        var running = new List<Guid>();
        for (var i = 0; i < 5; i++)
            running.Add(await s.CreateRunAsync());
        await s.AdmitAsync();

        var waiting = await s.CreateRunAsync();
        var pass = await s.AdmitAsync();

        Assert.Equal((0, 1, false), pass);
        Assert.Equal("pending", await s.RunStatusAsync(waiting));
        Assert.Equal(5, await s.InUseAsync(s.GroupRunsLimit));

        var lease = await s.Dispatch.ClaimOneAsync(host);
        await s.Dispatch.CompleteAsync(lease);
        await s.AdmitAsync();

        Assert.Equal("running", await s.RunStatusAsync(waiting));
        Assert.Equal(5, await s.InUseAsync(s.GroupRunsLimit));
    }

    [Fact]
    public async Task A_run_blocked_by_its_workflow_limit_is_passed_over_for_a_lower_priority_run_that_fits()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10, workflowMaxRuns: 1, priority: 90);
        // A second workflow in the same group, with no workflow limit, run by a low-priority automation.
        var (_, otherHash) = await DispatchScenario.RegisterAsync(pg, new GraphWorkflow(wf => wf.Task<Noop>("b")));
        var otherKey = await HostData.ScalarAsync<string>(pg, "SELECT key FROM workflow_definitions WHERE graph_hash = @h", ("h", otherHash));
        var lowPriority = await RunScenario.InsertAutomationAsync(pg, s.GroupId, otherKey, policy: "allow", maxRuns: 10, priority: 10);
        await s.StartRunAsync();

        var blocked = await s.CreateRunAsync();
        var fits = await s.CreateRunAsync(lowPriority);
        await s.AdmitAsync();

        Assert.Equal("pending", await s.RunStatusAsync(blocked));
        Assert.Equal("running", await s.RunStatusAsync(fits));
        Assert.Equal(s.WorkflowLimit, await HostData.ScalarAsync<string>(pg, "SELECT blocked_on FROM workflow_runs WHERE id = @id", ("id", blocked)));
    }

    [Fact]
    public async Task Workflow_limit_applies_across_automations()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10, workflowMaxRuns: 1);
        var other = await s.AddAutomationAsync(policy: "allow", maxRuns: 10);
        var first = await s.StartRunAsync();

        var second = await s.CreateRunAsync(other);
        await s.AdmitAsync();

        Assert.Equal("running", await s.RunStatusAsync(first));
        Assert.Equal("pending", await s.RunStatusAsync(second));
    }

    [Fact]
    public async Task Admitted_run_starts_with_roots_queued_others_waiting_and_a_deadline()
    {
        var s = await RunScenario.CreateAsync(pg, wf =>
        {
            var r1 = wf.Task<Noop>("r1");
            var r2 = wf.Task<Noop>("r2");
            var a = wf.Task<Noop>("a").DependsOn(r1);
            var b = wf.Task<Noop>("b").DependsOn(r2);
            wf.Task<Noop>("c").DependsOn(a, b);
        }, runTimeout: TimeSpan.FromHours(2));

        var runId = await s.StartRunAsync();

        var statuses = await HostData.ScalarAsync<string>(pg,
            "SELECT string_agg(task_key || '=' || status, ',' ORDER BY task_key) FROM task_runs WHERE workflow_run_id = @id", ("id", runId));
        Assert.Equal("a=waiting,b=waiting,c=waiting,r1=queued,r2=queued", statuses);
        Assert.True(await HostData.ScalarAsync<bool>(pg,
            "SELECT started_at IS NOT NULL AND deadline_at = started_at + interval '2 hours' FROM workflow_runs WHERE id = @id", ("id", runId)));
        // Only the automation limit has a row here; a key without a row is unlimited and not held.
        Assert.Equal([s.AutomationLimit],
            await HostData.ScalarAsync<string[]>(pg, "SELECT held_limits FROM workflow_runs WHERE id = @id", ("id", runId)));
    }

    [Fact]
    public async Task Higher_priority_runs_are_admitted_first()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10, groupMaxRuns: 1, priority: 10);
        var high = await s.AddAutomationAsync(policy: "allow", maxRuns: 10, priority: 80);
        var lowRun = await s.CreateRunAsync();
        var highRun = await s.CreateRunAsync(high);

        await s.AdmitAsync();

        Assert.Equal("running", await s.RunStatusAsync(highRun));
        Assert.Equal("pending", await s.RunStatusAsync(lowRun));
    }

    [Fact]
    public async Task Another_replica_admitting_the_group_makes_this_pass_skip_it()
    {
        var s = await RunScenario.CreateAsync(pg);
        await s.CreateRunAsync();

        await using var connection = await pg.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var cmd = new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(@id)", connection, transaction))
        {
            cmd.Parameters.AddWithValue("id", Baton.Persistence.AdvisoryLocks.Admission(s.GroupId));
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.Equal((0, 0, true), await s.AdmitAsync());
    }

    /// <summary>
    /// Exit criterion "no deadlock across replicas": two admission replicas and a completing agent work on three
    /// groups whose runs share automation and workflow limits. Every run must end, no transaction may fail, and
    /// every counter must return to zero.
    /// </summary>
    [Fact]
    public async Task Two_admission_replicas_and_completions_never_deadlock()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 3, workflowMaxRuns: 4, groupMaxRuns: 3);
        var groups = new List<(Guid Group, Guid Host)> { (s.GroupId, await s.Dispatch.AddHostAsync(slots: 50)) };
        for (var g = 0; g < 2; g++)
        {
            var (groupId, _) = await HostData.CreateGroupAsync(pg);
            await RunScenario.SetLimitAsync(pg, Baton.Persistence.Queue.DbNames.GroupRunsLimit(groupId), 3);
            groups.Add((groupId, await HostData.InsertHostAsync(pg, groupId, slots: 50, hashes: [s.Dispatch.Hash])));
        }

        // Shared across groups: one Queue automation per group, plus one Allow automation whose limit is shared.
        var automations = new List<Guid>();
        foreach (var (groupId, _) in groups)
        {
            automations.Add(await RunScenario.InsertAutomationAsync(pg, groupId, s.WorkflowKey, policy: "queue", maxRuns: 2));
            automations.Add(await RunScenario.InsertAutomationAsync(pg, groupId, s.WorkflowKey, policy: "allow", maxRuns: 2));
        }

        const int RunsPerAutomation = 5;
        var runIds = new List<Guid>();
        foreach (var automation in automations)
        {
            for (var i = 0; i < RunsPerAutomation; i++)
                runIds.Add(await s.CreateRunAsync(automation));
        }

        var replicas = new[] { new RunAdmission(pg.DataSource, s.Dispatch.Runs), new RunAdmission(pg.DataSource, s.Dispatch.Runs) };
        var claims = new TaskClaims(pg.DataSource, s.Dispatch.Graphs);
        var leases = new TaskLeases(pg.DataSource, s.Dispatch.Graphs);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        async Task AdmitLoop(RunAdmission admission)
        {
            while (!stop.IsCancellationRequested && await OpenRunsAsync(runIds) > 0)
            {
                await admission.AdmitAsync(CancellationToken.None);
                await Task.Delay(5);
            }
        }

        async Task WorkLoop()
        {
            while (!stop.IsCancellationRequested && await OpenRunsAsync(runIds) > 0)
            {
                foreach (var (groupId, host) in groups)
                {
                    var claimed = await claims.ClaimAsync(groupId, host, 50, CancellationToken.None);
                    await Task.WhenAll(claimed!.Tasks.Select(t =>
                        leases.CompleteAsync(groupId, DispatchScenario.Report(t.TaskRunId, t.LeaseToken, Baton.Domain.Runs.AttemptOutcome.Succeeded), CancellationToken.None)));
                }

                await Task.Delay(5);
            }
        }

        await Task.WhenAll(AdmitLoop(replicas[0]), AdmitLoop(replicas[1]), WorkLoop(), WorkLoop());

        Assert.Equal(0, await OpenRunsAsync(runIds));
        Assert.Equal(runIds.Count, await HostData.ScalarAsync<long>(pg,
            "SELECT count(*) FROM workflow_runs WHERE id = ANY (@ids) AND status = 'succeeded'", ("ids", runIds.ToArray())));
        var keys = automations.Select(Baton.Domain.Automations.AutomationRules.AutomationLimitKey)
            .Concat(groups.Select(g => Baton.Persistence.Queue.DbNames.GroupRunsLimit(g.Group)))
            .Append(s.WorkflowLimit).ToArray();
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg,
            "SELECT COALESCE(sum(in_use), 0) FROM concurrency_limits WHERE key = ANY (@keys)", ("keys", keys)));
    }

    private Task<long> OpenRunsAsync(List<Guid> runIds) => HostData.ScalarAsync<long>(pg,
        "SELECT count(*) FROM workflow_runs WHERE id = ANY (@ids) AND status IN ('pending','running','cancelling')", ("ids", runIds.ToArray()));
}
