using Baton.Agent.Protocol.V1;
using Baton.Agent.Worker;
using Grpc.Core;

namespace Baton.Agent.Execution;

/// <summary>
/// Sends a task's buffered log lines with <c>AppendLogs</c>, fenced by the lease token (ORC-08): every
/// second while the handler runs and once more before the result is reported. Flushes never overlap, so
/// lines arrive in sequence order. A stale lease stops the stream; other failures drop the batch with a
/// warning, because logs never fail a task.
/// </summary>
public sealed partial class TaskLogShipper(AgentClient client, TaskLease lease, TaskLogBuffer buffer, ILogger logger) : IDisposable
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private const int MaxLinesPerChunk = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _stale;

    /// <summary>Flushes every <see cref="FlushInterval"/> until <paramref name="stop"/>; never throws.</summary>
    public async Task PumpAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
                await FlushAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Stopped: the final flush follows.
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (!_stale && !buffer.IsEmpty)
            {
                var chunk = new LogChunk
                {
                    TaskRunId = lease.TaskRunId,
                    Attempt = lease.Attempt,
                    LeaseToken = lease.LeaseToken,
                    Lines = { buffer.Take(MaxLinesPerChunk) },
                };

                try
                {
                    await client.AppendLogsAsync(chunk, ct);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
                {
                    _stale = true;
                }
                catch (RpcException ex)
                {
                    LogDropped(logger, chunk.Lines.Count, lease.TaskKey, ex.StatusCode);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Count} log lines of task {TaskKey}: {Status}")]
    private static partial void LogDropped(ILogger logger, int count, string taskKey, StatusCode status);
}
