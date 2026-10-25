using Baton.Agent.Protocol.V1;
using Baton.Domain.Hosts;
using Baton.Persistence.Queue;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Baton.Orchestrator.Agents;

/// <summary>
/// gRPC <c>AgentService</c> (ADR-0005). M2 serves Register and Heartbeat (HST-02, HST-03, HST-04); lease
/// renewal in Heartbeat and the other calls arrive with dispatch in M3. Stateless: any replica can serve
/// any agent, because everything lives in Postgres.
/// </summary>
internal sealed partial class AgentGrpcService(AgentRegistry registry, ILogger<AgentGrpcService> logger)
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
        return new RegisterReply
        {
            HostId = result.HostId.ToString(),
            LeaseDuration = Duration.FromTimeSpan(TimeSpan.FromSeconds(result.LeaseSeconds)),
            HeartbeatInterval = Duration.FromTimeSpan(HostLiveness.HeartbeatInterval),
            Draining = result.Draining,
        };
    }

    public override async Task<HeartbeatReply> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.HostId, out var hostId))
            throw InvalidArgument("host_id is not a valid id.");

        var credential = AgentAuthInterceptor.Credential(context);
        var drained = await registry.HeartbeatAsync(credential, hostId, request.Draining, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Host is not registered for this credential; register again."));

        // M3: renew request.Leases and report lost and cancelled tasks.
        return new HeartbeatReply { Draining = drained };
    }

    private static RpcException InvalidArgument(string detail) => new(new Status(StatusCode.InvalidArgument, detail));

    [LoggerMessage(Level = LogLevel.Information, Message = "Host {Host} registered in group {Group} with {Slots} slots, agent {Version}")]
    private static partial void LogRegistered(ILogger logger, string host, string group, int slots, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected registration of host {Host} into group {Group} with credential {CredentialId}")]
    private static partial void LogRejected(ILogger logger, string host, string group, Guid credentialId);
}
