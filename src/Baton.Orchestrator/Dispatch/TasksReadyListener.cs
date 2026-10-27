using Baton.Domain.Retries;
using Baton.Persistence;
using Baton.Persistence.Queue;
using Npgsql;

namespace Baton.Orchestrator.Dispatch;

/// <summary>
/// Holds this replica's one <c>LISTEN tasks_ready</c> session on the non-pooled <c>listen</c> data source
/// and turns each notification (payload = host group id) into a <see cref="TasksReadySignal"/> wake-up.
/// Reconnects with backoff; while it is down, claims still re-check every <see cref="DispatchOptions.ClaimRecheck"/>.
/// </summary>
public sealed partial class TasksReadyListener(
    [FromKeyedServices(PersistenceServiceCollectionExtensions.ListenDataSourceKey)] NpgsqlDataSource dataSource,
    TasksReadySignal signal,
    ILogger<TasksReadyListener> logger) : BackgroundService
{
    private static readonly RetryPolicy ReconnectBackoff = new(int.MaxValue, TimeSpan.FromSeconds(1), 2.0, TimeSpan.FromSeconds(30));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += (_, e) =>
                {
                    if (Guid.TryParse(e.Payload, out var group))
                        signal.Notify(group);
                };

                await using (var listen = new NpgsqlCommand($"LISTEN {DbNames.TasksReadyChannel}", connection))
                    await listen.ExecuteNonQueryAsync(stoppingToken);

                failures = 0;
                signal.NotifyAll();
                LogListening(logger, DbNames.TasksReadyChannel);
                while (true)
                    await connection.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                var delay = Backoff.NextDelay(ReconnectBackoff, ++failures);
                LogListenFailed(logger, ex, delay);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening on {Channel}")]
    private static partial void LogListening(ILogger logger, string channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LISTEN connection failed; claims fall back to polling, reconnecting in {Delay}")]
    private static partial void LogListenFailed(ILogger logger, Exception exception, TimeSpan delay);
}
