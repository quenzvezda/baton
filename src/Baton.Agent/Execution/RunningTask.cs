using Baton.Agent.Protocol.V1;

namespace Baton.Agent.Execution;

/// <summary>
/// One leased attempt on this host. The heartbeat loop stops it in one of two ways (ORC-07): the lease was
/// lost (stop and discard the result) or cancellation was requested (stop and report Cancelled).
/// </summary>
public sealed class RunningTask : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private int _lost;
    private int _cancelRequested;

    public RunningTask(TaskLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        Lease = lease;
        TaskRunId = lease.TaskRunId;
    }

    public TaskLease Lease { get; }

    public string TaskRunId { get; }

    public LeaseRef Ref => new() { TaskRunId = Lease.TaskRunId, LeaseToken = Lease.LeaseToken };

    /// <summary>Cancelled when the task must stop: lease lost or cancellation requested.</summary>
    public CancellationToken Stopping => _stop.Token;

    /// <summary>Another host may own the task now: its result must not be reported.</summary>
    public bool Lost => Volatile.Read(ref _lost) == 1;

    public bool CancelRequested => Volatile.Read(ref _cancelRequested) == 1;

    public void MarkLost()
    {
        Interlocked.Exchange(ref _lost, 1);
        Cancel();
    }

    public void RequestCancel()
    {
        Interlocked.Exchange(ref _cancelRequested, 1);
        Cancel();
    }

    public void Dispose() => _stop.Dispose();

    private void Cancel()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished in the meantime; nothing to stop.
        }
    }
}
