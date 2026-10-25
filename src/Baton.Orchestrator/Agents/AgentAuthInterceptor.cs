using Baton.Persistence.Queue;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Baton.Orchestrator.Agents;

/// <summary>
/// Authenticates every agent call (ADR-0013): <c>authorization: Bearer &lt;token&gt;</c> must name an
/// unrevoked credential, looked up in the database on each call so revocation is immediate. A missing,
/// unknown or revoked token is <c>UNAUTHENTICATED</c>; whether the credential's group fits the request is
/// checked by <see cref="AgentGrpcService"/>.
/// </summary>
internal sealed class AgentAuthInterceptor(AgentRegistry registry) : Interceptor
{
    private const string CredentialKey = "baton.agent-credential";
    private const string BearerPrefix = "Bearer ";

    public static AgentCredential Credential(ServerCallContext context) =>
        (AgentCredential)context.UserState[CredentialKey];

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context);
        return await continuation(request, context);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthenticateAsync(context);
        return await continuation(requestStream, context);
    }

    private async Task AuthenticateAsync(ServerCallContext context)
    {
        var header = context.RequestHeaders.GetValue("authorization");
        if (header is null || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "An agent credential is required."));

        var credential = await registry.FindCredentialAsync(header[BearerPrefix.Length..].Trim(), context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "The agent credential is unknown or revoked."));

        context.UserState[CredentialKey] = credential;
    }
}
