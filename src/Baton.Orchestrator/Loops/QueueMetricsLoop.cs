using Baton.Orchestrator.Health;
using Baton.Persistence.Runs;

namespace Baton.Orchestrator.Loops;

/// <summary>
/// Every 15 s (OBS-04, add-operability-and-insight design.md decision 30): reads the queue statistics and hands
/// them to the gauges, which must not query the database from their callbacks. Runs on every replica; it only
/// reads.
/// </summary>
public sealed class QueueMetricsLoop(QueueStats stats, OrchestratorMetrics metrics, ILogger<QueueMetricsLoop> logger)
    : PeriodicLoop(logger)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    protected override TimeSpan Interval => RefreshInterval;

    protected internal override async Task TickAsync(CancellationToken ct) =>
        metrics.UpdateQueue(await stats.ReadAsync(ct));
}
