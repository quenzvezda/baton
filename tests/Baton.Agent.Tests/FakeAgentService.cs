using System.Collections.Concurrent;
using Baton.Agent.Protocol.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Baton.Agent.Tests;

/// <summary>
/// An in-memory <c>AgentService</c> client: records every call and answers from what the test queued.
/// Claims hand out queued leases, otherwise wait briefly and return none.
/// </summary>
public sealed class FakeAgentService : AgentService.AgentServiceClient
{
    private readonly ConcurrentQueue<TaskLease> _leases = new();

    public string HostId { get; } = Guid.NewGuid().ToString();

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    public ConcurrentQueue<ClaimRequest> Claims { get; } = new();
    public ConcurrentQueue<HeartbeatRequest> Heartbeats { get; } = new();
    public ConcurrentQueue<StartRequest> Starts { get; } = new();
    public ConcurrentQueue<CompleteRequest> Completes { get; } = new();
    public ConcurrentQueue<LogChunk> LogChunks { get; } = new();

    /// <summary>Task run ids the next heartbeat replies report as lost, or to cancel.</summary>
    public ConcurrentQueue<string> LoseNext { get; } = new();
    public ConcurrentQueue<string> CancelNext { get; } = new();

    /// <summary>When set, Complete and AppendLogs answer FAILED_PRECONDITION.</summary>
    public bool Stale { get; set; }

    public void Offer(params TaskLease[] leases)
    {
        foreach (var lease in leases)
            _leases.Enqueue(lease);
    }

    public override AsyncUnaryCall<RegisterReply> RegisterAsync(RegisterRequest request, CallOptions options) =>
        Reply(new RegisterReply { HostId = HostId, HeartbeatInterval = Duration.FromTimeSpan(HeartbeatInterval) });

    public override AsyncUnaryCall<HeartbeatReply> HeartbeatAsync(HeartbeatRequest request, CallOptions options)
    {
        Heartbeats.Enqueue(request);
        var reply = new HeartbeatReply();
        while (LoseNext.TryDequeue(out var lost))
            reply.LostTaskIds.Add(lost);
        while (CancelNext.TryDequeue(out var cancel))
            reply.CancelTaskIds.Add(cancel);
        return Reply(reply);
    }

    public override AsyncUnaryCall<ClaimReply> ClaimAsync(ClaimRequest request, CallOptions options)
    {
        Claims.Enqueue(request);
        return new AsyncUnaryCall<ClaimReply>(ClaimAsync(request.FreeSlots, options.CancellationToken),
            Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }

    public override AsyncUnaryCall<StartReply> StartAsync(StartRequest request, CallOptions options)
    {
        Starts.Enqueue(request);
        return Reply(new StartReply());
    }

    public override AsyncUnaryCall<CompleteReply> CompleteAsync(CompleteRequest request, CallOptions options)
    {
        if (Stale)
            return Fail<CompleteReply>(StatusCode.FailedPrecondition);
        Completes.Enqueue(request);
        return Reply(new CompleteReply());
    }

    public override AsyncClientStreamingCall<LogChunk, AppendLogsReply> AppendLogs(CallOptions options)
    {
        var writer = new Writer(this);
        return new AsyncClientStreamingCall<LogChunk, AppendLogsReply>(
            writer, writer.Response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }

    private async Task<ClaimReply> ClaimAsync(int freeSlots, CancellationToken ct)
    {
        var reply = new ClaimReply();
        while (reply.Leases.Count < freeSlots && _leases.TryDequeue(out var lease))
            reply.Leases.Add(lease);
        if (reply.Leases.Count == 0)
            await Task.Delay(20, ct);
        return reply;
    }

    private static AsyncUnaryCall<T> Reply<T>(T reply) =>
        new(Task.FromResult(reply), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

    private static AsyncUnaryCall<T> Fail<T>(StatusCode code) =>
        new(Task.FromException<T>(new RpcException(new Status(code, "fake"))), Task.FromResult(new Metadata()),
            () => new Status(code, "fake"), () => [], () => { });

    private sealed class Writer(FakeAgentService owner) : IClientStreamWriter<LogChunk>
    {
        private readonly TaskCompletionSource<AppendLogsReply> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _accepted;

        public Task<AppendLogsReply> Response => _response.Task;

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(LogChunk message)
        {
            if (!owner.Stale)
            {
                owner.LogChunks.Enqueue(message);
                _accepted += message.Lines.Count;
            }

            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            if (owner.Stale)
                _response.SetException(new RpcException(new Status(StatusCode.FailedPrecondition, "fake")));
            else
                _response.SetResult(new AppendLogsReply { Accepted = _accepted });
            return Task.CompletedTask;
        }
    }
}
