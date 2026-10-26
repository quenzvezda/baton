namespace Baton.Domain.Hosts;

public enum AgentState
{
    /// <summary>Not registered yet, or the orchestrator no longer knows the host.</summary>
    Registering,

    /// <summary>Registered and eligible: may claim work.</summary>
    Online,

    /// <summary>Drained by an operator (HST-04): keeps heartbeating and running its tasks, claims nothing.</summary>
    Draining,

    /// <summary>Shutdown requested: claims nothing, waits for running tasks to finish.</summary>
    Stopping,

    /// <summary>Shut down; no running tasks.</summary>
    Stopped,
}

/// <summary>
/// The agent's lifecycle as a pure, thread-safe state machine (HST-02, HST-04). The worker feeds it the
/// orchestrator's replies and its own task starts and finishes, and asks it whether it may claim.
/// </summary>
public sealed class AgentLifecycle
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AgentState _state = AgentState.Registering;
    private int _runningTasks;

    public AgentState State
    {
        get { lock (_gate) return _state; }
    }

    public int RunningTasks
    {
        get { lock (_gate) return _runningTasks; }
    }

    /// <summary>Only an Online agent claims new work.</summary>
    public bool CanClaim => State == AgentState.Online;

    /// <summary>Heartbeats tell the orchestrator the agent is draining itself only while it shuts down.</summary>
    public bool ReportsDraining => State is AgentState.Stopping or AgentState.Stopped;

    /// <summary>Completes once the agent is <see cref="AgentState.Stopped"/>.</summary>
    public Task Stopped => _stopped.Task;

    public void Registered(bool draining) => OnReply(draining, fromRegistration: true);

    public void HeartbeatReplied(bool draining) => OnReply(draining, fromRegistration: false);

    /// <summary>The orchestrator does not know the host (e.g. it was removed): register again.</summary>
    public void HostLost()
    {
        lock (_gate)
        {
            if (_state is AgentState.Online or AgentState.Draining)
                _state = AgentState.Registering;
        }
    }

    public void ShutdownRequested()
    {
        lock (_gate)
        {
            if (_state == AgentState.Stopped)
                return;
            _state = AgentState.Stopping;
            StopIfIdle();
        }
    }

    /// <summary>Records a claimed task; <c>false</c> (and nothing recorded) when the agent may not claim.</summary>
    public bool TryStartTask()
    {
        lock (_gate)
        {
            if (_state != AgentState.Online)
                return false;
            _runningTasks++;
            return true;
        }
    }

    /// <summary>
    /// Records a task the orchestrator already leased (M3). It is counted even when a drain arrived while
    /// the claim was in flight, because the lease is held either way; <c>false</c> only once Stopped.
    /// </summary>
    public bool TaskLeased()
    {
        lock (_gate)
        {
            if (_state == AgentState.Stopped)
                return false;
            _runningTasks++;
            return true;
        }
    }

    public void TaskFinished()
    {
        lock (_gate)
        {
            if (_runningTasks == 0)
                throw new InvalidOperationException("No task is running.");
            _runningTasks--;
            if (_state == AgentState.Stopping)
                StopIfIdle();
        }
    }

    private void OnReply(bool draining, bool fromRegistration)
    {
        lock (_gate)
        {
            var applies = fromRegistration
                ? _state == AgentState.Registering
                : _state is AgentState.Online or AgentState.Draining;
            if (applies)
                _state = draining ? AgentState.Draining : AgentState.Online;
        }
    }

    private void StopIfIdle()
    {
        if (_runningTasks > 0)
            return;
        _state = AgentState.Stopped;
        _stopped.TrySetResult();
    }
}
