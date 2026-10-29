using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Baton.Web.Api;

namespace Baton.Web.Tests.Infrastructure;

/// <summary>An in-memory Control Plane API: the data the pages read and a record of what they asked to change.</summary>
internal sealed class FakeBatonApi : IBatonApi
{
    public CurrentUser? User { get; set; } = new("Test Viewer", ["Viewer"]);
    public List<WorkflowSummary> Workflows { get; } = [];
    public List<WorkflowVersionDetail> WorkflowVersions { get; } = [];
    public List<HostGroup> HostGroups { get; } = [];
    public List<HostInfo> Hosts { get; } = [];
    public List<Automation> Automations { get; } = [];
    public List<RunSummary> Runs { get; } = [];
    public Dictionary<Guid, RunDetail> RunDetails { get; } = [];
    public Dictionary<(Guid RunId, string TaskKey, int Attempt), LogPage> Logs { get; } = [];

    /// <summary>Live lines for a followed attempt; completing the writer ends the stream.</summary>
    public Channel<LogLine> LiveLines { get; } = Channel.CreateUnbounded<LogLine>();

    public List<RunQuery> RunQueries { get; } = [];
    public List<AutomationRequest> SavedRequests { get; } = [];
    public List<Guid> Cancelled { get; } = [];
    public List<Guid> Drained { get; } = [];

    /// <summary>Thrown by the next create or update, as the API would reject the body.</summary>
    public ApiException? RejectNextSave { get; set; }

    public Task<CurrentUser> GetMeAsync(CancellationToken ct = default) =>
        User is null ? throw new ApiException(403, "Your role does not allow this.") : Task.FromResult(User);

    public Task<IReadOnlyList<WorkflowSummary>> ListWorkflowsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkflowSummary>>(Workflows);

    public Task<WorkflowVersionDetail?> GetWorkflowVersionAsync(string key, int version, CancellationToken ct = default) =>
        Task.FromResult(WorkflowVersions.SingleOrDefault(w => w.Key == key && w.Version == version));

    public Task<IReadOnlyList<HostGroup>> ListHostGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HostGroup>>(HostGroups);

    public Task<IReadOnlyList<HostInfo>> ListHostsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HostInfo>>([.. Hosts]);

    public Task DrainHostAsync(Guid hostId, CancellationToken ct = default)
    {
        Drained.Add(hostId);
        var index = Hosts.FindIndex(h => h.Id == hostId);
        Hosts[index] = Hosts[index] with { DrainRequested = true, Status = "draining" };
        return Task.CompletedTask;
    }

    public Task UndrainHostAsync(Guid hostId, CancellationToken ct = default)
    {
        var index = Hosts.FindIndex(h => h.Id == hostId);
        Hosts[index] = Hosts[index] with { DrainRequested = false, Status = "online" };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Automation>>(Automations);

    public Task<Automation?> GetAutomationAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Automations.SingleOrDefault(a => a.Id == id));

    public Task<Automation> CreateAutomationAsync(AutomationRequest request, CancellationToken ct = default) => SaveAsync(null, request);

    public Task<Automation> UpdateAutomationAsync(Guid id, int version, AutomationRequest request, CancellationToken ct = default) =>
        SaveAsync(id, request);

    public Task<Automation> SetAutomationEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        var index = Automations.FindIndex(a => a.Id == id);
        Automations[index] = Automations[index] with { Enabled = enabled, Version = Automations[index].Version + 1 };
        return Task.FromResult(Automations[index]);
    }

    public Task DeleteAutomationAsync(Guid id, int version, CancellationToken ct = default)
    {
        Automations.RemoveAll(a => a.Id == id);
        return Task.CompletedTask;
    }

    public Task<RunAccepted> RunNowAsync(Guid automationId, CancellationToken ct = default) => Task.FromResult(new RunAccepted(Guid.CreateVersion7()));

    public Task<RunPage> ListRunsAsync(RunQuery query, CancellationToken ct = default)
    {
        RunQueries.Add(query);
        var runs = Runs.Where(r => query.Statuses is not { Count: > 0 } statuses || statuses.Contains(r.Status))
            .Where(r => query.AutomationId is null || r.AutomationId == query.AutomationId)
            .ToList();
        return Task.FromResult(new RunPage(runs, null));
    }

    public Task<RunDetail?> GetRunAsync(Guid id, CancellationToken ct = default) => Task.FromResult(RunDetails.GetValueOrDefault(id));

    public Task<CancelAccepted> CancelRunAsync(Guid id, CancellationToken ct = default)
    {
        Cancelled.Add(id);
        RunDetails[id] = RunDetails[id] with { Status = "cancelling" };
        return Task.FromResult(new CancelAccepted(id, "cancelling"));
    }

    public Task<LogPage?> GetLogsAsync(Guid runId, string taskKey, int? attempt, int afterSeq = 0, CancellationToken ct = default) =>
        Task.FromResult(Logs.TryGetValue((runId, taskKey, attempt ?? 1), out var page)
            ? page with { Lines = [.. page.Lines.Where(l => l.Seq > afterSeq)] }
            : null);

    public async IAsyncEnumerable<LogLine> FollowLogsAsync(
        Guid runId, string taskKey, int attempt, int afterSeq, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var line in LiveLines.Reader.ReadAllAsync(ct))
            yield return line;
    }

    private Task<Automation> SaveAsync(Guid? id, AutomationRequest request)
    {
        SavedRequests.Add(request);
        if (RejectNextSave is { } rejection)
        {
            RejectNextSave = null;
            throw rejection;
        }

        var automation = new Automation(
            id ?? Guid.CreateVersion7(), request.Name!, request.Description, request.Enabled ?? true, "tester", request.WorkflowKey!,
            request.WorkflowVersion, request.Parameters ?? default, request.HostGroupId!.Value, request.RequiredLabels ?? [],
            request.Priority ?? 50, request.MaxConcurrentRuns ?? 1, request.ConcurrencyPolicy ?? "skip", request.RunTimeout,
            DateTimeOffset.UtcNow, 1, []);
        Automations.RemoveAll(a => a.Id == automation.Id);
        Automations.Add(automation);
        return Task.FromResult(automation);
    }
}
