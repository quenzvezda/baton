using System.Diagnostics.Metrics;

namespace Baton.Orchestrator.Health;

/// <summary>
/// The orchestrator's own counters (add-history-retention design.md decision 6), exported through
/// OpenTelemetry with the rest of the baseline (<c>AddMeter("Baton.*")</c>). Alert rules watch these;
/// the G1 runbooks name them.
/// </summary>
public sealed class OrchestratorMetrics
{
    public const string MeterName = "Baton.Orchestrator";

    private readonly Counter<long> _limitCorrections;
    private readonly Counter<long> _partitionsDropped;
    private readonly Counter<long> _runsPurged;

    public OrchestratorMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _limitCorrections = meter.CreateCounter<long>("baton.limits.corrections", unit: "{correction}",
            description: "Limit counters the reconciler had to correct; any increase means a bug.");
        _partitionsDropped = meter.CreateCounter<long>("baton.history.partitions_dropped", unit: "{partition}",
            description: "History partitions dropped by the 90-day retention.");
        _runsPurged = meter.CreateCounter<long>("baton.history.runs_purged", unit: "{run}",
            description: "Finished runs deleted by the 90-day retention.");
    }

    /// <summary>Tagged with the key's kind (<c>group-tasks</c>, <c>automation</c>, …), never the full key.</summary>
    public void LimitCorrected(string limitKey)
    {
        ArgumentNullException.ThrowIfNull(limitKey);
        var colon = limitKey.IndexOf(':', StringComparison.Ordinal);
        var kind = colon > 0 ? limitKey[..colon] : "other";
        _limitCorrections.Add(1, new KeyValuePair<string, object?>("limit.kind", kind));
    }

    public void PartitionsDropped(int count) => _partitionsDropped.Add(count);

    public void RunsPurged(int count) => _runsPurged.Add(count);
}
