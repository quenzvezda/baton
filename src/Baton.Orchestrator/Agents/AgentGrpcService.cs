using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Baton.Agent.Protocol.V1;
using Baton.Domain.Hosts;
using Baton.Domain.Runs;
using Baton.Orchestrator.Dispatch;
using Baton.Persistence.Queue;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Baton.Orchestrator.Agents;

/// <summary>
/// gRPC <c>AgentService</c> (ADR-0005): Register and Heartbeat (M2: HST-02..04), Claim, Start, Complete
/// and AppendLogs (M3: ORC-04..10). Stateless: any replica can serve any agent, because everything lives
/// in Postgres. Every write after a claim is fenced by the lease token (ADR-0006) and answered with
/// <c>FAILED_PRECONDITION</c> when it is stale.
/// </summary>
internal sealed partial class AgentGrpcService(
    AgentRegistry registry,
    TaskClaims claims,
    TaskLeases leases,
    TasksReadySignal tasksReady,
    IOptions<DispatchOptions> options,
    ILogger<AgentGrpcService> logger)
    : AgentService.AgentServiceBase
{
    private const int MaxHostNameLength = 253;

    public override async Task<RegisterReply> Register(RegisterRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.HostName) || request.HostName.Length > MaxHostNameLength)
            throw InvalidArgument($"host_name is required, at most {MaxHostNameLength} characters.");
        if (string.IsNullOrWhiteSpace(request.HostGroup))
            throw InvalidArgument("host_group is required.");
        if (request.CapacitySlots < 1)
            throw InvalidArgument("capacity_slots must be at least 1.");
        if (string.IsNullOrWhiteSpace(request.AgentVersion))
            throw InvalidArgument("agent_version is required.");

        var credential = AgentAuthInterceptor.Credential(context);
        var registration = new HostRegistration(
            request.HostName, request.HostGroup, [.. request.Labels], request.CapacitySlots, request.AgentVersion,
            [.. request.DefinitionHashes]);

        var result = await registry.RegisterAsync(credential, registration, context.CancellationToken);
        switch (result.Outcome)
        {
            case RegistrationOutcome.UnknownGroup:
                throw new RpcException(new Status(StatusCode.NotFound, $"Host group '{request.HostGroup}' does not exist."));
            case RegistrationOutcome.WrongGroup:
                LogRejected(logger, request.HostName, request.HostGroup, credential.Id);
                throw new RpcException(new Status(StatusCode.PermissionDenied, $"The credential is not valid for host group '{request.HostGroup}'."));
            case RegistrationOutcome.HostNotPermitted:
                LogRejected(logger, request.HostName, request.HostGroup, credential.Id);
                throw new RpcException(new Status(StatusCode.PermissionDenied, $"Host '{request.HostName}' belongs to another host group."));
        }

        LogRegistered(logger, request.HostName, request.HostGroup, request.CapacitySlots, request.AgentVersion);
        var lease = TimeSpan.FromSeconds(result.LeaseSeconds);
        return new RegisterReply
        {
            HostId = result.HostId.ToString(),
            LeaseDuration = Duration.FromTimeSpan(lease),
            HeartbeatInterval = Duration.FromTimeSpan(HostLiveness.HeartbeatIntervalFor(lease)),
            Draining = result.Draining,
        };
    }

    public override async Task<HeartbeatReply> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        var hostId = ParseId(request.HostId, "host_id");
        var credential = AgentAuthInterceptor.Credential(context);
        var drained = await registry.HeartbeatAsync(credential, hostId, request.Draining, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Host is not registered for this credential; register again."));

        // A lease the server cannot even parse is not the host's.
        var held = new List<HeldLease>();
        var unknown = new List<string>();
        foreach (var lease in request.Leases)
        {
            if (Guid.TryParse(lease.TaskRunId, out var id))
                held.Add(new HeldLease(id, lease.LeaseToken));
            else
                unknown.Add(lease.TaskRunId);
        }

        var renewal = await leases.RenewAsync(hostId, held, context.CancellationToken);
        if (renewal.Lost.Count > 0)
            LogLeasesLost(logger, hostId, renewal.Lost.Count);

        return new HeartbeatReply
        {
            Draining = drained,
            LostTaskIds = { unknown, renewal.Lost.Select(id => id.ToString()) },
            CancelTaskIds = { renewal.CancelRequested.Select(id => id.ToString()) },
        };
    }

    /// <summary>
    /// Long-poll (NFR-DispatchLatency): claims at once, then waits for the group's <c>tasks_ready</c>
    /// notification or the re-check interval, until a lease is won or <c>max_wait</c> has passed.
    /// </summary>
    public override async Task<ClaimReply> Claim(ClaimRequest request, ServerCallContext context)
    {
        var hostId = ParseId(request.HostId, "host_id");
        if (request.FreeSlots < 0)
            throw InvalidArgument("free_slots must not be negative.");

        var settings = options.Value;
        var maxWait = request.MaxWait?.ToTimeSpan() ?? settings.DefaultClaimWait;
        maxWait = maxWait < TimeSpan.Zero ? TimeSpan.Zero : maxWait > settings.MaxClaimWait ? settings.MaxClaimWait : maxWait;

        var credential = AgentAuthInterceptor.Credential(context);
        var ct = context.CancellationToken;
        var waited = Stopwatch.StartNew();
        Task? wake = null;
        while (true)
        {
            var result = await claims.ClaimAsync(credential.HostGroupId, hostId, request.FreeSlots, ct)
                ?? throw new RpcException(new Status(StatusCode.NotFound, "Host is not registered for this credential; register again."));

            if (result.Tasks.Count > 0)
            {
                LogClaimed(logger, result.Tasks.Count, hostId);
                return Reply(result);
            }

            var left = maxWait - waited.Elapsed;
            if (left <= TimeSpan.Zero || request.FreeSlots == 0)
                return new ClaimReply();

            wake ??= tasksReady.WhenReady(result.HostGroupId);
            await Task.WhenAny(wake, Task.Delay(left < settings.ClaimRecheck ? left : settings.ClaimRecheck, ct));
            ct.ThrowIfCancellationRequested();

            // Taken before the next claim, so a notification in between still wakes the next wait.
            wake = tasksReady.WhenReady(result.HostGroupId);
        }
    }

    public override async Task<StartReply> Start(StartRequest request, ServerCallContext context)
    {
        var taskRunId = ParseId(request.TaskRunId, "task_run_id");
        var credential = AgentAuthInterceptor.Credential(context);
        if (!await leases.StartAsync(credential.HostGroupId, taskRunId, request.LeaseToken, context.CancellationToken))
            throw Stale(taskRunId, request.LeaseToken, "Start");
        return new StartReply();
    }

    public override async Task<CompleteReply> Complete(CompleteRequest request, ServerCallContext context)
    {
        var taskRunId = ParseId(request.TaskRunId, "task_run_id");
        var outcome = request.Outcome switch
        {
            TaskOutcome.Succeeded => AttemptOutcome.Succeeded,
            TaskOutcome.Failed => AttemptOutcome.Failed,
            TaskOutcome.TimedOut => AttemptOutcome.TimedOut,
            TaskOutcome.Cancelled => AttemptOutcome.Cancelled,
            _ => throw InvalidArgument("outcome is required."),
        };

        string? output = null;
        if (outcome == AttemptOutcome.Succeeded)
        {
            output = request.OutputJson.IsEmpty ? "null" : request.OutputJson.ToStringUtf8();
            if (!IsJson(output))
                throw InvalidArgument("output_json is not valid JSON.");
        }

        var error = request.Error is { } e && outcome != AttemptOutcome.Succeeded
            ? TaskErrors.Json(e.Type, e.Message, string.IsNullOrEmpty(e.StackTrace) ? null : e.StackTrace)
            : null;
        var report = new AttemptReport(taskRunId, request.LeaseToken, outcome, output, request.OutputJson.Length, error,
            Retryable: request.Error?.Retryable ?? true);

        var credential = AgentAuthInterceptor.Credential(context);
        if (!await leases.CompleteAsync(credential.HostGroupId, report, context.CancellationToken))
            throw Stale(taskRunId, request.LeaseToken, "Complete");
        return new CompleteReply();
    }

    public override async Task<AppendLogsReply> AppendLogs(IAsyncStreamReader<LogChunk> requestStream, ServerCallContext context)
    {
        var credential = AgentAuthInterceptor.Credential(context);
        var accepted = 0;
        await foreach (var chunk in requestStream.ReadAllAsync(context.CancellationToken))
        {
            var taskRunId = ParseId(chunk.TaskRunId, "task_run_id");
            if (chunk.Lines.Count == 0)
                continue;

            var lines = chunk.Lines.Select(l => new TaskLogLine(
                l.Seq, l.LoggedAt?.ToDateTime() ?? DateTime.UtcNow, l.Level, l.Message)).ToList();
            accepted += await leases.AppendLogsAsync(credential.HostGroupId, taskRunId, chunk.Attempt, chunk.LeaseToken, lines,
                context.CancellationToken) ?? throw Stale(taskRunId, chunk.LeaseToken, "AppendLogs");
        }

        return new AppendLogsReply { Accepted = accepted };
    }

    private static ClaimReply Reply(ClaimResult result) => new()
    {
        Leases =
        {
            result.Tasks.Select(t => new TaskLease
            {
                TaskRunId = t.TaskRunId.ToString(),
                WorkflowRunId = t.RunId.ToString(),
                TaskKey = t.TaskKey,
                DefinitionHash = t.DefinitionHash,
                Attempt = t.Attempt,
                LeaseToken = t.LeaseToken,
                Timeout = Duration.FromTimeSpan(t.Timeout),
                ParamsJson = ByteString.CopyFromUtf8(t.ParametersJson),
                UpstreamOutputsJson = { t.UpstreamOutputs.ToDictionary(o => o.Key, o => ByteString.CopyFromUtf8(o.Value)) },
            }),
        },
    };

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(Encoding.UTF8.GetBytes(text));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private RpcException Stale(Guid taskRunId, long leaseToken, string call)
    {
        LogStale(logger, call, taskRunId, leaseToken);
        return new RpcException(new Status(StatusCode.FailedPrecondition,
            "The lease expired or was replaced; discard this result (ADR-0006)."));
    }

    private static Guid ParseId(string value, string field) =>
        Guid.TryParse(value, out var id) ? id : throw InvalidArgument($"{field} is not a valid id.");

    private static RpcException InvalidArgument(string detail) => new(new Status(StatusCode.InvalidArgument, detail));

    [LoggerMessage(Level = LogLevel.Information, Message = "Host {Host} registered in group {Group} with {Slots} slots, agent {Version}")]
    private static partial void LogRegistered(ILogger logger, string host, string group, int slots, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected registration of host {Host} into group {Group} with credential {CredentialId}")]
    private static partial void LogRejected(ILogger logger, string host, string group, Guid credentialId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Leased {Count} tasks to host {HostId}")]
    private static partial void LogClaimed(ILogger logger, int count, Guid hostId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Host {HostId} reported {Count} leases it no longer owns")]
    private static partial void LogLeasesLost(ILogger logger, Guid hostId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected stale {Call} for task run {TaskRunId} with lease token {LeaseToken}")]
    private static partial void LogStale(ILogger logger, string call, Guid taskRunId, long leaseToken);
}
