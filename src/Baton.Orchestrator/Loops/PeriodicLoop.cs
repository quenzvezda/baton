namespace Baton.Orchestrator.Loops;

/// <summary>
/// A loop that runs on every replica (ADR-0008): each tick claims its rows with <c>SKIP LOCKED</c>, so
/// replicas share the work instead of taking turns. The first tick runs at startup, then one per
/// <see cref="Interval"/>; a failed tick is logged and the next one retries.
/// </summary>
public abstract partial class PeriodicLoop(ILogger logger) : BackgroundService
{
    protected ILogger Logger { get; } = logger;

    protected abstract TimeSpan Interval { get; }

    protected internal abstract Task TickAsync(CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogTickFailed(Logger, ex, GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Loop} tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception, string loop);
}
