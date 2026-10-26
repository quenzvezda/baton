using Baton.Abstractions;
using Baton.Domain.Runs;

namespace Baton.Persistence.Queue;

/// <summary>The text values stored in run and task columns, mirroring their CHECK constraints.</summary>
public static class DbNames
{
    public static string Status(TaskRunStatus status) => status switch
    {
        TaskRunStatus.Waiting => "waiting",
        TaskRunStatus.Queued => "queued",
        TaskRunStatus.Leased => "leased",
        TaskRunStatus.Running => "running",
        TaskRunStatus.RetryWait => "retry_wait",
        TaskRunStatus.Succeeded => "succeeded",
        TaskRunStatus.Failed => "failed",
        TaskRunStatus.Skipped => "skipped",
        TaskRunStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static TaskRunStatus TaskStatus(string value) => value switch
    {
        "waiting" => TaskRunStatus.Waiting,
        "queued" => TaskRunStatus.Queued,
        "leased" => TaskRunStatus.Leased,
        "running" => TaskRunStatus.Running,
        "retry_wait" => TaskRunStatus.RetryWait,
        "succeeded" => TaskRunStatus.Succeeded,
        "failed" => TaskRunStatus.Failed,
        "skipped" => TaskRunStatus.Skipped,
        "cancelled" => TaskRunStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string Status(WorkflowRunStatus status) => status switch
    {
        WorkflowRunStatus.Pending => "pending",
        WorkflowRunStatus.Running => "running",
        WorkflowRunStatus.Cancelling => "cancelling",
        WorkflowRunStatus.Succeeded => "succeeded",
        WorkflowRunStatus.Failed => "failed",
        WorkflowRunStatus.Cancelled => "cancelled",
        WorkflowRunStatus.TimedOut => "timed_out",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static string Rule(TriggerRule rule) => rule switch
    {
        TriggerRule.AllSuccess => "all_success",
        TriggerRule.AllDone => "all_done",
        TriggerRule.OneFailed => "one_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
    };

    public static TriggerRule Rule(string value) => value switch
    {
        "all_success" => TriggerRule.AllSuccess,
        "all_done" => TriggerRule.AllDone,
        "one_failed" => TriggerRule.OneFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    /// <summary><c>task_attempts.outcome</c>.</summary>
    public static string Outcome(AttemptOutcome outcome) => outcome switch
    {
        AttemptOutcome.Succeeded => "succeeded",
        AttemptOutcome.Failed => "failed",
        AttemptOutcome.TimedOut => "timed_out",
        AttemptOutcome.Lost => "lost",
        AttemptOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    /// <summary>The NOTIFY channel; the payload is the host group id (design spec §7).</summary>
    public const string TasksReadyChannel = "tasks_ready";

    public static string GroupTasksLimit(Guid groupId) => $"group-tasks:{groupId}";
}

/// <summary><c>run_events.type</c> values written by dispatch (design.md "Decisions agreed with the owner").</summary>
public static class RunEventTypes
{
    public const string TaskQueued = "task.queued";
    public const string TaskSkipped = "task.skipped";
    public const string TaskLeased = "task.leased";
    public const string TaskStarted = "task.started";
    public const string TaskRetryScheduled = "task.retry_scheduled";
    public const string RunSucceeded = "run.succeeded";
    public const string RunFailed = "run.failed";
    public const string RunStarted = "run.started";

    /// <summary><c>task.succeeded</c>, <c>task.failed</c>, <c>task.timed_out</c>, <c>task.lost</c>, <c>task.cancelled</c>.</summary>
    public static string Attempt(AttemptOutcome outcome) => $"task.{DbNames.Outcome(outcome)}";
}
