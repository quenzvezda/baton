namespace Baton.Domain.Runs;

/// <summary>Nine task-run states, four final. Mirrors the CHECK constraint on task_runs.status.</summary>
public enum TaskRunStatus
{
    Waiting,
    Queued,
    Leased,
    Running,
    RetryWait,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
}

public static class TaskRunStatusExtensions
{
    public static bool IsFinal(this TaskRunStatus status) => status
        is TaskRunStatus.Succeeded
        or TaskRunStatus.Failed
        or TaskRunStatus.Skipped
        or TaskRunStatus.Cancelled;

    /// <summary>Failed, Skipped and Cancelled all count as "not succeeded" for AllSuccess children.</summary>
    public static bool IsUnsuccessful(this TaskRunStatus status) => status
        is TaskRunStatus.Failed
        or TaskRunStatus.Skipped
        or TaskRunStatus.Cancelled;
}
