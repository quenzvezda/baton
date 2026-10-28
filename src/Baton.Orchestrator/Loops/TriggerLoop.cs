using Baton.Orchestrator.Dispatch;
using Baton.Persistence.Queue;
using Microsoft.Extensions.Options;

namespace Baton.Orchestrator.Loops;

/// <summary>
/// Every 1 s on every replica (ORC-01, AUT-04, NFR-TriggerAccuracy): fires due cron triggers, at most one run
/// per fire time (<see cref="CronTriggers"/>). A trigger whose firing fails is logged and retried next tick.
/// </summary>
public sealed partial class TriggerLoop(CronTriggers triggers, IOptions<DispatchOptions> options, ILogger<TriggerLoop> logger)
    : PeriodicLoop(logger)
{
    protected override TimeSpan Interval => options.Value.TriggerInterval;

    protected internal override async Task TickAsync(CancellationToken ct)
    {
        var pass = await triggers.FireDueAsync(ct);
        if (pass.Fired > 0)
            LogFired(Logger, pass.Fired, pass.Created, pass.Skipped);
        foreach (var (triggerId, error) in pass.Failed)
            LogFailed(Logger, error, triggerId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fired {Fired} cron triggers: {Created} runs created, {Skipped} skipped")]
    private static partial void LogFired(ILogger logger, int fired, int created, int skipped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Firing cron trigger {TriggerId} failed; retrying next tick")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid triggerId);
}
