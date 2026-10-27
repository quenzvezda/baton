using Baton.Agent.Protocol.V1;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace Baton.Agent.Worker;

/// <summary>
/// The agent's side of <c>AgentService</c>. Every call carries the group credential (ADR-0013) and runs
/// through the <see cref="TransientPipeline"/> resilience pipeline, which retries transient gRPC failures
/// with exponential backoff and jitter.
/// </summary>
public sealed class AgentClient(
    AgentService.AgentServiceClient client,
    IOptions<AgentOptions> options,
    ResiliencePipelineProvider<string> pipelines)
{
    public const string TransientPipeline = "agent-rpc";

    private readonly Metadata _headers = new() { { "authorization", $"Bearer {options.Value.Token}" } };

    public Task<RegisterReply> RegisterAsync(RegisterRequest request, CancellationToken ct) =>
        Pipeline.ExecuteAsync(async token => await client.RegisterAsync(request, _headers, cancellationToken: token), ct).AsTask();

    public Task<HeartbeatReply> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) =>
        Pipeline.ExecuteAsync(async token => await client.HeartbeatAsync(request, _headers, cancellationToken: token), ct).AsTask();

    /// <summary>Long-poll; not retried by the pipeline, because the worker simply claims again.</summary>
    public async Task<ClaimReply> ClaimAsync(ClaimRequest request, CancellationToken ct) =>
        await client.ClaimAsync(request, _headers, cancellationToken: ct);

    public Task<StartReply> StartAsync(StartRequest request, CancellationToken ct) =>
        Pipeline.ExecuteAsync(async token => await client.StartAsync(request, _headers, cancellationToken: token), ct).AsTask();

    /// <summary>Retried on transient errors; a retried Complete is fenced, so it never applies twice.</summary>
    public Task<CompleteReply> CompleteAsync(CompleteRequest request, CancellationToken ct) =>
        Pipeline.ExecuteAsync(async token => await client.CompleteAsync(request, _headers, cancellationToken: token), ct).AsTask();

    /// <summary>One chunk per call; a retried chunk is deduplicated by the server (same sequence numbers).</summary>
    public Task<AppendLogsReply> AppendLogsAsync(LogChunk chunk, CancellationToken ct) =>
        Pipeline.ExecuteAsync(async token =>
        {
            using var call = client.AppendLogs(_headers, cancellationToken: token);
            await call.RequestStream.WriteAsync(chunk, token);
            await call.RequestStream.CompleteAsync();
            return await call;
        }, ct).AsTask();

    /// <summary>Failures worth repeating as they are; anything else needs a change elsewhere first.</summary>
    public static bool IsTransient(RpcException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted
            or StatusCode.Aborted;
    }

    private ResiliencePipeline Pipeline => pipelines.GetPipeline(TransientPipeline);
}
