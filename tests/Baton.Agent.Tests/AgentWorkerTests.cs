using Baton.Agent.Protocol.V1;

namespace Baton.Agent.Tests;

/// <summary>Task 4.1: the claim loop claims only free slots; heartbeats carry leases and act on lost and cancel lists.</summary>
public sealed class AgentWorkerTests : IAsyncLifetime
{
    private AgentHarness _agent = null!;

    public Task InitializeAsync()
    {
        _agent = AgentHarness.Create(slots: 2);
        return _agent.Worker.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _agent.Worker.StopAsync(timeout.Token);
        await _agent.DisposeAsync();
    }

    [Fact]
    public async Task Succeeded_task_is_started_then_reported_with_its_output()
    {
        var lease = _agent.Lease("echo", "eu");
        _agent.Fake.Offer(lease);

        await AgentHarness.EventuallyAsync(() => !_agent.Fake.Completes.IsEmpty, "the result");

        Assert.Equal(lease.TaskRunId, Assert.Single(_agent.Fake.Starts).TaskRunId);
        var complete = Assert.Single(_agent.Fake.Completes);
        Assert.Equal(lease.LeaseToken, complete.LeaseToken);
        Assert.Equal(TaskOutcome.Succeeded, complete.Outcome);
        Assert.Equal("\"eu#1\"", complete.OutputJson.ToStringUtf8());
    }

    [Fact]
    public async Task Claims_ask_only_for_free_slots_and_none_while_full()
    {
        var first = _agent.Lease("wait");
        var second = _agent.Lease("wait");
        _agent.Fake.Offer(first, second);
        await AgentHarness.EventuallyAsync(() => _agent.Worker.RunningCount == 2, "both tasks running");
        var claimsWhenFull = _agent.Fake.Claims.Count;

        await Task.Delay(200);
        Assert.Equal(claimsWhenFull, _agent.Fake.Claims.Count);
        Assert.Equal(2, _agent.Fake.Claims.First().FreeSlots);

        _agent.Fake.CancelNext.Enqueue(first.TaskRunId);
        await AgentHarness.EventuallyAsync(() => _agent.Fake.Claims.Count > claimsWhenFull, "a claim after a slot freed");
        Assert.Equal(1, _agent.Fake.Claims.Last().FreeSlots);
    }

    [Fact]
    public async Task Heartbeats_carry_every_held_lease()
    {
        var lease = _agent.Lease("wait");
        _agent.Fake.Offer(lease);
        await AgentHarness.EventuallyAsync(() => _agent.Worker.RunningCount == 1, "the task running");

        await AgentHarness.EventuallyAsync(
            () => _agent.Fake.Heartbeats.Any(h => h.Leases.Any(l => l.TaskRunId == lease.TaskRunId && l.LeaseToken == lease.LeaseToken)),
            "a heartbeat with the lease");
    }

    [Fact]
    public async Task Lost_lease_stops_the_handler_and_reports_nothing()
    {
        var lease = _agent.Lease("wait");
        _agent.Fake.Offer(lease);
        await AgentHarness.EventuallyAsync(() => _agent.Worker.RunningCount == 1, "the task running");

        _agent.Fake.LoseNext.Enqueue(lease.TaskRunId);

        await AgentHarness.EventuallyAsync(() => _agent.Worker.RunningCount == 0, "the handler to stop");
        await Task.Delay(100);
        Assert.Empty(_agent.Fake.Completes);
    }

    [Fact]
    public async Task Cancel_request_stops_the_handler_and_reports_cancelled()
    {
        var lease = _agent.Lease("wait");
        _agent.Fake.Offer(lease);
        await AgentHarness.EventuallyAsync(() => _agent.Worker.RunningCount == 1, "the task running");

        _agent.Fake.CancelNext.Enqueue(lease.TaskRunId);

        await AgentHarness.EventuallyAsync(() => !_agent.Fake.Completes.IsEmpty, "the cancelled report");
        Assert.Equal(TaskOutcome.Cancelled, Assert.Single(_agent.Fake.Completes).Outcome);
    }

    [Fact]
    public async Task Stale_result_is_discarded_and_the_slot_is_freed()
    {
        _agent.Fake.Stale = true;
        _agent.Fake.Offer(_agent.Lease("echo"));

        await AgentHarness.EventuallyAsync(() => !_agent.Fake.Starts.IsEmpty && _agent.Worker.RunningCount == 0, "the task to finish");
        Assert.Empty(_agent.Fake.Completes);
    }
}
