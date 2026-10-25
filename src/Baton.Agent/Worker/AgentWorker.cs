using Baton.Agent.Protocol.V1;
using Baton.Domain.Hosts;
using Baton.Domain.Retries;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Baton.Agent.Worker;

/// <summary>
/// Registers, then heartbeats every interval, feeding the replies to <see cref="AgentLifecycle"/>
/// (HST-02..04). Registration is retried with backoff whatever the error (unknown group, bad credential,
/// orchestrator down), because the fix is usually made elsewhere. On shutdown the agent reports it is
/// draining, claims nothing more and waits for its running tasks; claiming and running arrive in M3.
/// </summary>
public sealed partial class AgentWorker(
    AgentClient client,
    AgentLifecycle lifecycle,
    AgentIdentity identity,
    IOptions<AgentOptions> options,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly RetryPolicy RegistrationBackoff =
        new(int.MaxValue, TimeSpan.FromSeconds(1), 2.0, TimeSpan.FromSeconds(60));

    private readonly AgentOptions _settings = options.Value;
    private string _hostId = "";
    private TimeSpan _interval = TimeSpan.FromSeconds(options.Value.HeartbeatIntervalSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                return;
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
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lifecycle.ShutdownRequested();
        LogStopping(logger, identity.HostName, lifecycle.RunningTasks);

        if (_hostId.Length > 0)
        {
            try
            {
                // Tell the orchestrator at once, so it stops counting this host as eligible.
                await client.HeartbeatAsync(new HeartbeatRequest { HostId = _hostId, Draining = true }, cancellationToken);
            }
            catch (Exception ex) when (ex is RpcException or OperationCanceledException)
            {
                LogShutdownHeartbeatFailed(logger, ex, identity.HostName);
            }
        }

        await lifecycle.Stopped.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await base.StopAsync(cancellationToken);
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
        try
        {
            var reply = await client.HeartbeatAsync(
                new HeartbeatRequest { HostId = _hostId, Draining = lifecycle.ReportsDraining }, ct);
            lifecycle.HeartbeatReplied(reply.Draining);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // Removed by an Admin, or the database was reset: register again.
            lifecycle.HostLost();
            LogHostLost(logger, identity.HostName);
            return;
        }

        var isDraining = lifecycle.State == AgentState.Draining;
        if (isDraining != wasDraining)
            LogDrainChanged(logger, identity.HostName, isDraining);
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
}
