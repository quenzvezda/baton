namespace Baton.Web.Api;

/// <summary>
/// The Control Plane API as the console uses it (ADR-0012: the console talks to the API only). Failures other
/// than "not found" on reads throw <see cref="ApiException"/>.
/// </summary>
public interface IBatonApi
{
    Task<CurrentUser> GetMeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowSummary>> ListWorkflowsAsync(CancellationToken ct = default);

    Task<WorkflowVersionDetail?> GetWorkflowVersionAsync(string key, int version, CancellationToken ct = default);

    Task<IReadOnlyList<HostGroup>> ListHostGroupsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<HostInfo>> ListHostsAsync(CancellationToken ct = default);

    Task DrainHostAsync(Guid hostId, CancellationToken ct = default);

    Task UndrainHostAsync(Guid hostId, CancellationToken ct = default);

    /// <summary>ORC-13: pauses or resumes dispatch for the group; returns the group as it is now.</summary>
    Task<HostGroup> SetHostGroupPausedAsync(Guid groupId, bool paused, CancellationToken ct = default);

    /// <summary>AUT-10: the next fire times; an invalid expression or zone fails with field errors.</summary>
    Task<IReadOnlyList<FireTimePreview>> PreviewFireTimesAsync(string expression, string timeZone, CancellationToken ct = default);

    Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken ct = default);

    Task<Automation?> GetAutomationAsync(Guid id, CancellationToken ct = default);

    Task<Automation> CreateAutomationAsync(AutomationRequest request, CancellationToken ct = default);

    /// <param name="version">The <see cref="Automation.Version"/> last read; a newer one fails with 412.</param>
    Task<Automation> UpdateAutomationAsync(Guid id, int version, AutomationRequest request, CancellationToken ct = default);

    Task<Automation> SetAutomationEnabledAsync(Guid id, bool enabled, CancellationToken ct = default);

    Task DeleteAutomationAsync(Guid id, int version, CancellationToken ct = default);

    /// <summary>OBS-05: issues a new signing secret for a webhook notification channel; the secret is returned once.</summary>
    Task<SigningSecretRotated> RotateNotificationSecretAsync(Guid automationId, Guid channelId, CancellationToken ct = default);

    Task<RunAccepted> RunNowAsync(Guid automationId, CancellationToken ct = default);

    Task<RunPage> ListRunsAsync(RunQuery query, CancellationToken ct = default);

    Task<RunDetail?> GetRunAsync(Guid id, CancellationToken ct = default);

    Task<CancelAccepted> CancelRunAsync(Guid id, CancellationToken ct = default);

    /// <summary>WF-10: re-runs a Failed or TimedOut run in place; returns the run, now Pending. Other states fail with 409.</summary>
    Task<RunDetail> RetryFailedRunAsync(Guid id, CancellationToken ct = default);

    /// <summary>OBS-04: every host group's queue, ordered by name.</summary>
    Task<IReadOnlyList<QueueGroupStats>> GetQueueStatsAsync(CancellationToken ct = default);

    /// <summary>AUT-11: the audit entries of one entity, newest first.</summary>
    /// <param name="entityType"><c>automation</c>, <c>host_group</c>, <c>agent_credential</c> or <c>workflow_limit</c>.</param>
    Task<AuditPage> GetAuditAsync(string entityType, string entityId, string? cursor = null, int? limit = null, CancellationToken ct = default);

    /// <param name="attempt"><c>null</c> for the task's latest attempt.</param>
    Task<LogPage?> GetLogsAsync(Guid runId, string taskKey, int? attempt, int afterSeq = 0, CancellationToken ct = default);

    /// <summary>
    /// Lines after <paramref name="afterSeq"/> as they are written; the sequence ends once the attempt has finished
    /// and every line was returned.
    /// </summary>
    IAsyncEnumerable<LogLine> FollowLogsAsync(Guid runId, string taskKey, int attempt, int afterSeq, CancellationToken ct = default);
}

/// <summary>A non-success API response, with the field errors of a validation problem.</summary>
public sealed class ApiException(int statusCode, string message, IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    /// <summary>Field errors keyed as the API names them, e.g. <c>name</c> or <c>parameters.region</c>.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}
