using System.Collections.Concurrent;

namespace Baton.Orchestrator.Dispatch;

/// <summary>
/// In-process wake-up for waiting claims, one signal per host group, fed by <see cref="TasksReadyListener"/>.
/// A waiter takes the group's current task before it claims, so a notification that arrives in between is
/// not missed.
/// </summary>
public sealed class TasksReadySignal
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _groups = new();

    /// <summary>Completes on the group's next notification.</summary>
    public Task WhenReady(Guid hostGroupId) =>
        _groups.GetOrAdd(hostGroupId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Notify(Guid hostGroupId)
    {
        if (_groups.TryRemove(hostGroupId, out var waiters))
            waiters.TrySetResult();
    }

    /// <summary>Wakes every waiter, e.g. after the listener reconnected and may have missed notifications.</summary>
    public void NotifyAll()
    {
        foreach (var group in _groups.Keys)
            Notify(group);
    }
}
