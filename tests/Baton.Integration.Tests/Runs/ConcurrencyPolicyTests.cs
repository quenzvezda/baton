using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Runs;

/// <summary>AUT-07: one integration test per concurrency policy (M4 task 2.3, exit criterion).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ConcurrencyPolicyTests(PostgresFixture pg)
{
    [Fact]
    public async Task Skip_at_the_limit_creates_no_run_and_records_the_skipped_fire()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "skip", maxRuns: 1);
        await s.StartRunAsync();

        var result = await s.FireAsync();

        Assert.Equal(FireOutcome.Skipped, result.Outcome);
        Assert.Equal(1L, await RunCountAsync(s));
        Assert.Equal(["run.skipped"], await s.Dispatch.EventTypesAsync(s.AutomationId));
    }

    [Fact]
    public async Task Queue_at_the_limit_creates_a_pending_run_that_starts_after_the_active_one_finishes()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "queue", maxRuns: 1);
        var host = await s.Dispatch.AddHostAsync();
        var first = await s.StartRunAsync();

        var second = await s.CreateRunAsync();
        await s.AdmitAsync();
        Assert.Equal("pending", await s.RunStatusAsync(second));

        await s.CompleteRunAsync(first, host);
        await s.AdmitAsync();

        Assert.Equal("succeeded", await s.RunStatusAsync(first));
        Assert.Equal("running", await s.RunStatusAsync(second));
        Assert.Equal(1, await s.InUseAsync(s.AutomationLimit));
    }

    [Fact]
    public async Task Replace_at_the_limit_cancels_the_active_run_and_creates_a_new_one()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "replace", maxRuns: 1);
        await s.Dispatch.AddHostAsync();
        var first = await s.StartRunAsync();

        var result = await s.FireAsync();

        Assert.Equal(FireOutcome.Created, result.Outcome);
        Assert.Equal(first, result.ReplacedRunId);
        // The replaced run had no leased task, so it ended at once and released the automation limit.
        Assert.Equal("cancelled", await s.RunStatusAsync(first));
        await s.AdmitAsync();
        Assert.Equal("running", await s.RunStatusAsync(result.RunId!.Value));
        Assert.Contains("run.cancelling", await s.Dispatch.EventTypesAsync(first));
    }

    [Fact]
    public async Task Replace_waits_for_a_replaced_run_whose_task_is_still_running()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "replace", maxRuns: 1);
        var host = await s.Dispatch.AddHostAsync();
        var first = await s.StartRunAsync();
        var lease = await s.Dispatch.ClaimOneAsync(host);

        var result = await s.FireAsync();
        await s.AdmitAsync();

        Assert.Equal("cancelling", await s.RunStatusAsync(first));
        Assert.Equal("pending", await s.RunStatusAsync(result.RunId!.Value));

        await s.Dispatch.CompleteAsync(lease, Baton.Domain.Runs.AttemptOutcome.Cancelled);
        await s.AdmitAsync();

        Assert.Equal("cancelled", await s.RunStatusAsync(first));
        Assert.Equal("running", await s.RunStatusAsync(result.RunId!.Value));
    }

    [Fact]
    public async Task Allow_at_the_limit_creates_a_second_run_that_starts_when_workflow_and_group_limits_have_room()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 1, groupMaxRuns: 2);
        await s.Dispatch.AddHostAsync();
        var first = await s.StartRunAsync();

        var second = await s.CreateRunAsync();
        var third = await s.CreateRunAsync();
        await s.AdmitAsync();

        Assert.Equal("running", await s.RunStatusAsync(first));
        Assert.Equal("running", await s.RunStatusAsync(second));
        Assert.Equal("pending", await s.RunStatusAsync(third)); // group-runs is full
        Assert.Equal(0, await s.InUseAsync(s.AutomationLimit));
        Assert.Equal(2, await s.InUseAsync(s.GroupRunsLimit));
    }

    private static Task<long> RunCountAsync(RunScenario s) =>
        HostData.ScalarAsync<long>(s.Pg, "SELECT count(*) FROM workflow_runs WHERE automation_id = @id", ("id", s.AutomationId));
}
