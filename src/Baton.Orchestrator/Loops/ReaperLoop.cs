using Baton.Orchestrator.Dispatch;
using Baton.Persistence.Queue;
using Microsoft.Extensions.Options;

namespace Baton.Orchestrator.Loops;

/// <summary>
/// Every 5 s (ORC-09, WF-05): closes expired leases as Lost, attempts running past their timeout plus
/// 60 s as timed out, and persists Offline for hosts that stopped heartbeating (M2 design.md decision 9).
/// </summary>
public sealed partial class ReaperLoop(LeaseReaper reaper, IOptions<DispatchOptions> options, ILogger<ReaperLoop> logger)
    : PeriodicLoop(logger)
{
    protected override TimeSpan Interval => options.Value.ReaperInterval;

    protected internal override async Task TickAsync(CancellationToken ct)
    {
        var summary = await reaper.ReapAsync(ct);
        if (summary.Lost > 0 || summary.TimedOut > 0)
            LogReaped(Logger, summary.Lost, summary.TimedOut);
        if (summary.HostsMarkedOffline > 0)
            LogOffline(Logger, summary.HostsMarkedOffline);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reaped {Lost} lost and {TimedOut} timed-out attempts")]
    private static partial void LogReaped(ILogger logger, int lost, int timedOut);

    [LoggerMessage(Level = LogLevel.Information, Message = "Marked {Count} hosts offline")]
    private static partial void LogOffline(ILogger logger, int count);
}
