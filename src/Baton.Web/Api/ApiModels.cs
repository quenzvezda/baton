using System.Text.Json;

namespace Baton.Web.Api;

// The console's view of the Control Plane API contract (JSON, web defaults). Kept apart from the API's own
// records so the console depends on HTTP only (ADR-0012).

public sealed record CurrentUser(string Name, IReadOnlyList<string> Roles);

public sealed record WorkflowSummary(string Key, int LatestVersion, string DisplayName, bool IsRetired, DateTimeOffset RegisteredAt);

public sealed record WorkflowVersionDetail(
    string Key,
    int Version,
    string DisplayName,
    string GraphHash,
    JsonElement Graph,
    JsonElement ParamsSchema,
    bool IsRetired,
    DateTimeOffset RegisteredAt,
    IReadOnlyList<int> Versions);

public sealed record HostGroup(Guid Id, string Name, int? MaxConcurrentRuns, int? MaxConcurrentTasks, int LeaseSeconds, bool DispatchPaused);

public sealed record HostTask(Guid TaskRunId, Guid WorkflowRunId, string TaskKey, int Attempt, string Status);

public sealed record HostInfo(
    Guid Id,
    string Name,
    Guid HostGroupId,
    string HostGroupName,
    string Status,
    bool DrainRequested,
    IReadOnlyList<string> Labels,
    int SlotsTotal,
    int SlotsUsed,
    IReadOnlyList<HostTask> CurrentTasks,
    string AgentVersion,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset RegisteredAt);

public sealed record TriggerRequest(Guid? Id, string? Kind, string? CronExpression = null, string? TimeZone = null, bool? Enabled = null);

public sealed record AutomationRequest(
    string? Name,
    string? WorkflowKey,
    Guid? HostGroupId,
    IReadOnlyList<TriggerRequest>? Triggers,
    string? Description = null,
    bool? Enabled = null,
    int? WorkflowVersion = null,
    JsonElement? Parameters = null,
    IReadOnlyList<string>? RequiredLabels = null,
    int? Priority = null,
    int? MaxConcurrentRuns = null,
    string? ConcurrencyPolicy = null,
    TimeSpan? RunTimeout = null);

public sealed record TriggerInfo(
    Guid Id,
    string Kind,
    string? CronExpression,
    string? TimeZone,
    bool Enabled,
    DateTimeOffset? NextFireAt,
    DateTimeOffset? LastFiredAt,
    string? WebhookUrl,
    string? WebhookSecret);

public sealed record Automation(
    Guid Id,
    string Name,
    string? Description,
    bool Enabled,
    string Owner,
    string WorkflowKey,
    int? WorkflowVersion,
    JsonElement Parameters,
    Guid HostGroupId,
    IReadOnlyList<string> RequiredLabels,
    int Priority,
    int MaxConcurrentRuns,
    string ConcurrencyPolicy,
    TimeSpan? RunTimeout,
    DateTimeOffset UpdatedAt,
    int Version,
    IReadOnlyList<TriggerInfo> Triggers);

public sealed record RunAccepted(Guid RunId);

public sealed record CancelAccepted(Guid RunId, string Status);

public sealed record RunSummary(
    Guid Id,
    Guid? AutomationId,
    string? AutomationName,
    string WorkflowKey,
    int WorkflowVersion,
    string Status,
    int Priority,
    Guid HostGroupId,
    string HostGroupName,
    string TriggerKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record RunPage(IReadOnlyList<RunSummary> Items, string? NextCursor);

/// <summary>Runs list filters; <see cref="To"/> is exclusive.</summary>
public sealed record RunQuery(
    Guid? AutomationId = null,
    string? WorkflowKey = null,
    IReadOnlyList<string>? Statuses = null,
    Guid? HostGroupId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Cursor = null,
    int? Limit = null);

public sealed record TaskAttempt(
    int Attempt,
    Guid HostId,
    string? HostName,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Outcome,
    JsonElement? Error);

public sealed record TaskRun(
    Guid Id,
    string Key,
    string Status,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    JsonElement? Output,
    JsonElement? Error,
    IReadOnlyList<TaskAttempt> Attempts);

public sealed record RunDetail(
    Guid Id,
    Guid? AutomationId,
    string? AutomationName,
    string WorkflowKey,
    int WorkflowVersion,
    string GraphHash,
    JsonElement Graph,
    string Status,
    int Priority,
    Guid HostGroupId,
    string HostGroupName,
    string TriggerKind,
    JsonElement Parameters,
    string? CancelReason,
    string? BlockedOn,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? DeadlineAt,
    IReadOnlyList<TaskRun> Tasks);

public sealed record LogLine(int Seq, DateTimeOffset LoggedAt, string Level, string Message);

public sealed record LogPage(int Attempt, bool IsLive, IReadOnlyList<LogLine> Lines, int? NextAfterSeq);
