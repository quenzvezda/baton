using Grpc.Core;
using Grpc.Core.Interceptors;
using Npgsql;

namespace Baton.Orchestrator.Agents;

/// <summary>
/// Outermost agent interceptor: a call that fails because Postgres cannot be reached answers
/// <c>UNAVAILABLE</c> instead of <c>UNKNOWN</c>, so agents retry it like any transient error (their pipeline
/// retries <c>UNAVAILABLE</c>) rather than dropping a completion and waiting for the lease to expire. Found by
/// the G1 chaos test (Postgres restart under load). Retrying is safe: every write is fenced by its lease token,
/// so a completion that did commit before the connection broke is refused on retry, never applied twice.
/// </summary>
internal sealed partial class DatabaseUnavailableInterceptor(ILogger<DatabaseUnavailableInterceptor> logger) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (Exception ex) when (IsDatabaseUnavailable(ex))
        {
            throw Unavailable(context, ex);
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(requestStream, context);
        }
        catch (Exception ex) when (IsDatabaseUnavailable(ex))
        {
            throw Unavailable(context, ex);
        }
    }

    /// <summary>Connection-level failures only; SQL errors (constraint, syntax) stay <c>UNKNOWN</c> because a retry cannot fix them.</summary>
    internal static bool IsDatabaseUnavailable(Exception ex) => ex switch
    {
        PostgresException pg => pg.IsTransient,
        NpgsqlException npgsql => npgsql.IsTransient || npgsql.InnerException is System.Net.Sockets.SocketException or IOException or TimeoutException,
        TimeoutException => true,
        _ => false,
    };

    private RpcException Unavailable(ServerCallContext context, Exception ex)
    {
        LogUnavailable(logger, context.Method, ex.Message);
        return new RpcException(new Status(StatusCode.Unavailable, "The orchestrator's database is unavailable; retry."));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Method} failed: database unavailable ({Reason}); answered UNAVAILABLE")]
    private static partial void LogUnavailable(ILogger logger, string method, string reason);
}
