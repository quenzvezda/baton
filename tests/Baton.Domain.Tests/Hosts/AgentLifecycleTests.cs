using Baton.Domain.Hosts;

namespace Baton.Domain.Tests.Hosts;

/// <summary>HST-02, HST-04: the agent worker's state machine, drain handling and graceful shutdown.</summary>
public sealed class AgentLifecycleTests
{
    [Fact]
    public void A_new_agent_registers_before_it_may_claim()
    {
        var agent = new AgentLifecycle();

        Assert.Equal(AgentState.Registering, agent.State);
        Assert.False(agent.CanClaim);
        Assert.False(agent.TryStartTask());
    }

    [Fact]
    public void Registered_agent_is_online_and_claims()
    {
        var agent = new AgentLifecycle();

        agent.Registered(draining: false);

        Assert.Equal(AgentState.Online, agent.State);
        Assert.True(agent.CanClaim);
        Assert.True(agent.TryStartTask());
        Assert.Equal(1, agent.RunningTasks);
    }

    [Fact]
    public void Agent_registering_into_a_drained_host_starts_draining()
    {
        var agent = new AgentLifecycle();

        agent.Registered(draining: true);

        Assert.Equal(AgentState.Draining, agent.State);
        Assert.False(agent.CanClaim);
    }

    [Fact]
    public void Drain_stops_claiming_but_keeps_running_tasks_and_undrain_resumes()
    {
        var agent = Online();
        Assert.True(agent.TryStartTask());
        Assert.True(agent.TryStartTask());

        agent.HeartbeatReplied(draining: true);

        Assert.Equal(AgentState.Draining, agent.State);
        Assert.False(agent.TryStartTask());
        Assert.Equal(2, agent.RunningTasks);

        agent.TaskFinished();
        agent.TaskFinished();
        agent.HeartbeatReplied(draining: false);

        Assert.Equal(AgentState.Online, agent.State);
        Assert.True(agent.CanClaim);
    }

    [Fact]
    public void Operator_drain_is_not_reported_back_as_self_drain()
    {
        // Echoing the drain would keep the host Draining for one more heartbeat after an undrain.
        var agent = Online();

        agent.HeartbeatReplied(draining: true);

        Assert.False(agent.ReportsDraining);
    }

    [Fact]
    public void Shutdown_without_running_tasks_stops_at_once()
    {
        var agent = Online();

        agent.ShutdownRequested();

        Assert.Equal(AgentState.Stopped, agent.State);
        Assert.True(agent.Stopped.IsCompletedSuccessfully);
        Assert.True(agent.ReportsDraining);
    }

    [Fact]
    public void Shutdown_waits_for_running_tasks_and_claims_nothing_meanwhile()
    {
        var agent = Online();
        Assert.True(agent.TryStartTask());
        Assert.True(agent.TryStartTask());

        agent.ShutdownRequested();

        Assert.Equal(AgentState.Stopping, agent.State);
        Assert.True(agent.ReportsDraining);
        Assert.False(agent.TryStartTask());
        Assert.False(agent.Stopped.IsCompleted);

        agent.TaskFinished();
        Assert.False(agent.Stopped.IsCompleted);

        agent.TaskFinished();
        Assert.Equal(AgentState.Stopped, agent.State);
        Assert.True(agent.Stopped.IsCompletedSuccessfully);
    }

    [Fact]
    public void Undrain_reply_during_shutdown_does_not_resume_claiming()
    {
        var agent = Online();
        Assert.True(agent.TryStartTask());
        agent.ShutdownRequested();

        agent.HeartbeatReplied(draining: false);
        agent.Registered(draining: false);

        Assert.Equal(AgentState.Stopping, agent.State);
        Assert.False(agent.CanClaim);
    }

    [Fact]
    public void Lost_host_registers_again()
    {
        var agent = Online();

        agent.HostLost();

        Assert.Equal(AgentState.Registering, agent.State);
        Assert.False(agent.CanClaim);
        agent.Registered(draining: false);
        Assert.Equal(AgentState.Online, agent.State);
    }

    [Fact]
    public void Heartbeat_reply_before_registration_is_ignored()
    {
        var agent = new AgentLifecycle();

        agent.HeartbeatReplied(draining: false);

        Assert.Equal(AgentState.Registering, agent.State);
    }

    [Fact]
    public void Finishing_a_task_that_never_started_is_a_bug()
    {
        var agent = Online();

        Assert.Throws<InvalidOperationException>(agent.TaskFinished);
    }

    [Fact]
    public void Shutdown_before_registration_stops()
    {
        var agent = new AgentLifecycle();

        agent.ShutdownRequested();

        Assert.Equal(AgentState.Stopped, agent.State);
        Assert.True(agent.Stopped.IsCompletedSuccessfully);
    }

    private static AgentLifecycle Online()
    {
        var agent = new AgentLifecycle();
        agent.Registered(draining: false);
        return agent;
    }
}
