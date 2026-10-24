namespace Baton.Domain.Runs;

/// <summary>Pending → Running → (Cancelling) → Succeeded | Failed | Cancelled | TimedOut. Design spec §6.</summary>
public enum WorkflowRunStatus
{
    Pending,
    Running,
    Cancelling,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
}

public static class WorkflowRunStatusExtensions
{
    public static bool IsFinal(this WorkflowRunStatus status) => status
        is WorkflowRunStatus.Succeeded
        or WorkflowRunStatus.Failed
        or WorkflowRunStatus.Cancelled
        or WorkflowRunStatus.TimedOut;
}
