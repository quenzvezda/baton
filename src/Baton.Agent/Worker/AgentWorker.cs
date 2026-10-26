using System.Collections.Concurrent;
using Baton.Agent.Execution;
using Baton.Agent.Protocol.V1;
using Baton.Domain.Hosts;
using Baton.Domain.Retries;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Baton.Agent.Worker;

/// <summary>
/// Registers, then heartbeats every interval, feeding the replies to <see cref="AgentLifecycle"/>
/// (HST-02..04). Registration is retried with backoff whatever the error (unknown group, bad credential,
/// orchestrator down), because the fix is usually made elsewhere. Next to it a claim loop long-polls for
/// as many tasks as there are free slots and runs each with <see cref="TaskExecutor"/> (design spec §8
/// "Agent"); heartbeats carry the leases and stop the handlers the orchestrator says were lost or
/// cancelled (ORC-07). On shutdown the agent reports it is draining, claims nothing more and waits for its
/// running tasks.
/// </summary>
public sealed partial class AgentWorker(
    AgentClient client,
    AgentLifecycle lifecycle,
    AgentIdentity identity,
    TaskExecutor executor,
    IOptions<AgentOptions> options,
    ILoggerFactory loggers,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly RetryPolicy RegistrationBackoff =
        new(int.MaxValue, TimeSpan.FromSeconds(1), 2.0, TimeSpan.FromSeconds(60));

    /// <summary>How long the claim loop idles before it looks again when it may not claim.</summary>
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(500);

    private readonly AgentOptions _settings = options.Value;
    private readonly ConcurrentDictionary<string, RunningTask> _running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _claimStop = new();
    private readonly SemaphoreSlim _slotFreed = new(0);
    private readonly ILogger _taskLogger = loggers.CreateLogger("Baton.Task");
    private CancellationToken _hostStopping;
    private volatile string _hostId = "";
    private TimeSpan _interval = TimeSpan.FromSeconds(options.Value.HeartbeatIntervalSeconds);

    /// <summary>Tasks this agent is running now.</summary>
    public int RunningCount => _running.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _hostStopping = stoppingToken;
        using var claimStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _claimStop.Token);
        var claiming = Task.Run(() => ClaimLoopAsync(claimStop.Token), CancellationToken.None);

        var failedRegistrations = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = _interval;
            try
            {
                if (lifecycle.State == AgentState.Registering)
                {
                    await RegisterAsync(stoppingToken);
                    failedRegistrations = 0;
                    delay = _interval;
                }
                else
                {
                    await HeartbeatAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (lifecycle.State == AgentState.Registering)
            {
                delay = Backoff.NextDelay(RegistrationBackoff, ++failedRegistrations);
                LogRegistrationFailed(logger, identity.HostName, _settings.HostGroup, ex.StatusCode, ex.Status.Detail, delay);
            }
            catch (RpcException ex)
            {
                // Transient errors were already retried by the pipeline; try again next interval.
                LogHeartbeatFailed(logger, identity.HostName, ex.StatusCode, ex.Status.Detail);
            }

            await Task.Delay(delay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await claiming;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lifecycle.ShutdownRequested();
        await _claimStop.CancelAsync();
        LogStopping(logger, identity.HostName, lifecycle.RunningTasks);

        if (_hostId.Length > 0)
        {
            try
            {
                // Tell the orchestrator at once, so it stops counting this host as eligible.
                await client.HeartbeatAsync(HeartbeatRequest(draining: true), cancellationToken);
            }
            catch (Exception ex) when (ex is RpcException or OperationCanceledException)
            {
                LogShutdownHeartbeatFailed(logger, ex, identity.HostName);
            }
        }

        // Heartbeats go on meanwhile, so the running tasks keep their leases.
        await lifecycle.Stopped.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _claimStop.Dispose();
        _slotFreed.Dispose();
        base.Dispose();
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        var request = new RegisterRequest
        {
            HostName = identity.HostName,
            HostGroup = _settings.HostGroup,
            CapacitySlots = _settings.CapacitySlots,
            AgentVersion = identity.Version,
            Labels = { _settings.Labels },
            DefinitionHashes = { identity.DefinitionHashes },
        };

        var reply = await client.RegisterAsync(request, ct);
        _hostId = reply.HostId;
        if (reply.HeartbeatInterval?.ToTimeSpan() is { } interval && interval > TimeSpan.Zero)
            _interval = interval;
        lifecycle.Registered(reply.Draining);
        LogRegistered(logger, identity.HostName, _settings.HostGroup, reply.HostId, reply.Draining);
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        var wasDraining = lifecycle.State == AgentState.Draining;
        HeartbeatReply reply;
        try
        {
            reply = await client.HeartbeatAsync(HeartbeatRequest(lifecycle.ReportsDraining), ct);
            lifecycle.HeartbeatReplied(reply.Draining);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // Removed by an Admin, or the database was reset: register again.
            lifecycle.HostLost();
            LogHostLost(logger, identity.HostName);
            return;
        }

        foreach (var id in reply.LostTaskIds)
        {
            if (_running.TryGetValue(id, out var task))
            {
                task.MarkLost();
                LogLeaseLost(logger, task.Lease.TaskKey, id);
            }
        }

        foreach (var id in reply.CancelTaskIds)
        {
            if (_running.TryGetValue(id, out var task))
                task.RequestCancel();
        }

        var isDraining = lifecycle.State == AgentState.Draining;
        if (isDraining != wasDraining)
            LogDrainChanged(logger, identity.HostName, isDraining);
    }

    private HeartbeatRequest HeartbeatRequest(bool draining) => new()
    {
        HostId = _hostId,
        Draining = draining,
        Leases = { _running.Values.Select(t => t.Ref) },
    };

    private async Task ClaimLoopAsync(CancellationToken ct)
    {
        var longPoll = Duration.FromTimeSpan(TimeSpan.FromSeconds(_settings.ClaimLongPollSeconds));
        while (!ct.IsCancellationRequested)
        {
            var free = _settings.CapacitySlots - _running.Count;
            if (!lifecycle.CanClaim || free <= 0 || _hostId.Length == 0)
            {
                await WaitForSlotAsync(IdleWait, ct);
                continue;
            }

            ClaimReply reply;
            try
            {
                reply = await client.ClaimAsync(new ClaimRequest { HostId = _hostId, FreeSlots = free, MaxWait = longPoll }, ct);
            }
            catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or RpcException)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                lifecycle.HostLost();
                LogHostLost(logger, identity.HostName);
                continue;
            }
            catch (RpcException ex)
            {
                LogClaimFailed(logger, identity.HostName, ex.StatusCode, ex.Status.Detail);
                await WaitForSlotAsync(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            foreach (var lease in reply.Leases)
                Launch(lease);
        }
    }

    private async Task WaitForSlotAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _slotFreed.WaitAsync(timeout, ct);
        }
        catch (OperationCanceledException)
        {
            // Stopping; the loop condition ends it.
        }
    }

    private void Launch(TaskLease lease)
    {
        if (!lifecycle.TaskLeased())
        {
            // Already stopped: the lease expires on the orchestrator and the task runs elsewhere.
            LogNotStarted(logger, lease.TaskKey, lease.TaskRunId);
            return;
        }

        var task = new RunningTask(lease);
        _running[lease.TaskRunId] = task;
        _ = Task.Run(() => RunAsync(task), CancellationToken.None);
    }

    private async Task RunAsync(RunningTask task)
    {
        var lease = task.Lease;
        try
        {
            if (!await StartAsync(lease))
            {
                task.MarkLost();
                return;
            }

            var buffer = new TaskLogBuffer(_taskLogger);
            using var logs = new TaskLogShipper(client, lease, buffer, logger);
            using var stopPump = new CancellationTokenSource();
            var pump = logs.PumpAsync(stopPump.Token);

            var result = await executor.ExecuteAsync(task, buffer, _hostStopping);

            await stopPump.CancelAsync();
            await pump;
            await logs.FlushAsync(CancellationToken.None);

            if (task.Lost)
            {
                LogResultDiscarded(logger, lease.TaskKey, lease.TaskRunId);
                return;
            }

            await CompleteAsync(lease, result);
        }
        catch (Exception ex)
        {
            // Never expected (the executor catches handler failures); the lease expires and the task is retried.
            LogTaskCrashed(logger, ex, lease.TaskKey, lease.TaskRunId);
        }
        finally
        {
            _running.TryRemove(lease.TaskRunId, out _);
            task.Dispose();
            lifecycle.TaskFinished();
            _slotFreed.Release();
        }
    }

    /// <returns><c>false</c> when the lease is already stale.</returns>
    private async Task<bool> StartAsync(TaskLease lease)
    {
        try
        {
            await client.StartAsync(new StartRequest { TaskRunId = lease.TaskRunId, LeaseToken = lease.LeaseToken }, CancellationToken.None);
            return true;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            LogResultDiscarded(logger, lease.TaskKey, lease.TaskRunId);
            return false;
        }
        catch (RpcException ex)
        {
            // Start only records timing; the attempt runs regardless.
            LogStartFailed(logger, lease.TaskKey, ex.StatusCode);
            return true;
        }
    }

    private async Task CompleteAsync(TaskLease lease, TaskResult result)
    {
        var request = new CompleteRequest
        {
            TaskRunId = lease.TaskRunId,
            LeaseToken = lease.LeaseToken,
            Outcome = result.Outcome,
            OutputJson = result.Output,
            Error = result.Error,
        };

        try
        {
            // Report even if our own token fired; the server's fencing check decides.
            await client.CompleteAsync(request, CancellationToken.None);
            LogCompleted(logger, lease.TaskKey, lease.Attempt, result.Outcome);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            LogResultDiscarded(logger, lease.TaskKey, lease.TaskRunId);
        }
        catch (RpcException ex)
        {
            LogCompleteFailed(logger, lease.TaskKey, lease.TaskRunId, ex.StatusCode);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered {Host} in group {Group} as {HostId} (draining: {Draining})")]
    private static partial void LogRegistered(ILogger logger, string host, string group, string hostId, bool draining);

    [LoggerMessage(Level = LogLevel.Error, Message = "Registering {Host} in group {Group} failed with {Status}: {Detail}; retrying in {Delay}")]
    private static partial void LogRegistrationFailed(ILogger logger, string host, string group, StatusCode status, string detail, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat of {Host} failed with {Status}: {Detail}")]
    private static partial void LogHeartbeatFailed(ILogger logger, string host, StatusCode status, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not report shutdown of {Host}; it goes Offline after missed heartbeats")]
    private static partial void LogShutdownHeartbeatFailed(ILogger logger, Exception exception, string host);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Orchestrator does not know {Host}; registering again")]
    private static partial void LogHostLost(ILogger logger, string host);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Host} draining: {Draining}")]
    private static partial void LogDrainChanged(ILogger logger, string host, bool draining);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping {Host}: claiming nothing more, waiting for {Running} running tasks")]
    private static partial void LogStopping(ILogger logger, string host, int running);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claim of {Host} failed with {Status}: {Detail}")]
    private static partial void LogClaimFailed(ILogger logger, string host, StatusCode status, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease of task {TaskKey} ({TaskRunId}) was lost; stopping its handler")]
    private static partial void LogLeaseLost(ILogger logger, string taskKey, string taskRunId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent stopped; task {TaskKey} ({TaskRunId}) was not started and will run elsewhere")]
    private static partial void LogNotStarted(ILogger logger, string taskKey, string taskRunId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease of task {TaskKey} ({TaskRunId}) is stale; its result was discarded")]
    private static partial void LogResultDiscarded(ILogger logger, string taskKey, string taskRunId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Start of task {TaskKey} failed with {Status}; running it anyway")]
    private static partial void LogStartFailed(ILogger logger, string taskKey, StatusCode status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {TaskKey} attempt {Attempt} reported {Outcome}")]
    private static partial void LogCompleted(ILogger logger, string taskKey, int attempt, TaskOutcome outcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reporting task {TaskKey} ({TaskRunId}) failed with {Status}; the lease expires and it is retried")]
    private static partial void LogCompleteFailed(ILogger logger, string taskKey, string taskRunId, StatusCode status);

    [LoggerMessage(Level = LogLevel.Error, Message = "Running task {TaskKey} ({TaskRunId}) crashed")]
    private static partial void LogTaskCrashed(ILogger logger, Exception exception, string taskKey, string taskRunId);
}
