using Baton.Orchestrator.Dispatch;
using Baton.Persistence.Queue;
using Microsoft.Extensions.Options;

namespace Baton.Orchestrator.Loops;

/// <summary>Every 1 s (ORC-10): Retry wait tasks whose backoff has passed become Queued.</summary>
public sealed partial class RetryPromoterLoop(RetryPromoter promoter, IOptions<DispatchOptions> options, ILogger<RetryPromoterLoop> logger)
    : PeriodicLoop(logger)
{
    protected override TimeSpan Interval => options.Value.RetryPromoterInterval;

    protected internal override async Task TickAsync(CancellationToken ct)
    {
        var promoted = await promoter.PromoteAsync(ct);
        if (promoted > 0)
            LogPromoted(Logger, promoted);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Queued {Count} due retries")]
    private static partial void LogPromoted(ILogger logger, int count);
}
