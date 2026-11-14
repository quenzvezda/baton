using System.Diagnostics.Metrics;
using Baton.Persistence.Runs;

namespace Baton.Orchestrator.Health;

/// <summary>
/// The orchestrator's own metrics (add-history-retention design.md decision 6; add-operability-and-insight
/// design.md decision 30), exported through OpenTelemetry with the rest of the baseline (<c>AddMeter("Baton.*")</c>).
/// Alert rules watch these; the runbooks name them. Per-group instruments carry <c>baton.host_group</c> (the group
/// id) and, on the gauges, <c>baton.host_group.name</c>. Every replica reports the queue gauges from its own
/// snapshot, so alerts aggregate them with <c>max</c>, never <c>sum</c>.
/// </summary>
public sealed class OrchestratorMetrics
{
    public const string MeterName = "Baton.Orchestrator";

    public const string HostGroupTag = "baton.host_group";
    public const string HostGroupNameTag = "baton.host_group.name";
    public const string NotificationKindTag = "baton.notification.kind";

    private readonly Counter<long> _limitCorrections;
    private readonly Counter<long> _partitionsDropped;
    private readonly Counter<long> _runsPurged;
    private readonly Counter<long> _tasksAged;
    private readonly Counter<long> _leasesExpired;
    private readonly Counter<long> _notificationsSent;
    private readonly Counter<long> _notificationsFailed;

    private volatile IReadOnlyList<QueueGroupStats> _queue = [];

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
        _tasksAged = meter.CreateCounter<long>("baton.tasks.aged", unit: "{task}",
            description: "Queued tasks whose effective priority the aging loop raised (ORC-06).");
        _leasesExpired = meter.CreateCounter<long>("baton.leases.expired", unit: "{lease}",
            description: "Leases the reaper found expired; their attempts end lost (ORC-09, OBS-04).");
        _notificationsSent = meter.CreateCounter<long>("baton.notifications.sent", unit: "{notification}",
            description: "Run notifications delivered (OBS-05).");
        _notificationsFailed = meter.CreateCounter<long>("baton.notifications.failed", unit: "{notification}",
            description: "Run notifications given up after their last attempt (OBS-05, OQ-08).");

        meter.CreateObservableGauge("baton.queue.depth", () => Observe(s => s.Queued), unit: "{task}",
            description: "Queued tasks per host group (OBS-04).");
        meter.CreateObservableGauge("baton.queue.oldest_wait", () => Observe(s => s.OldestWaitSeconds ?? 0), unit: "s",
            description: "How long the oldest queued task of the group has waited (OBS-04).");
        meter.CreateObservableGauge("baton.leases.active", () => Observe(s => s.ActiveLeases), unit: "{lease}",
            description: "Tasks leased or running per host group (OBS-04).");
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

    public void TasksAged(int count) => _tasksAged.Add(count);

    public void LeasesExpired(IReadOnlyDictionary<Guid, int>? byGroup)
    {
        foreach (var (group, count) in byGroup ?? new Dictionary<Guid, int>())
            _leasesExpired.Add(count, new KeyValuePair<string, object?>(HostGroupTag, group.ToString()));
    }

    /// <param name="kind"><c>email</c> or <c>webhook</c>.</param>
    public void NotificationSent(string kind) =>
        _notificationsSent.Add(1, new KeyValuePair<string, object?>(NotificationKindTag, kind));

    public void NotificationFailed(string kind) =>
        _notificationsFailed.Add(1, new KeyValuePair<string, object?>(NotificationKindTag, kind));

    /// <summary>Replaces the snapshot the queue gauges report (<c>QueueMetricsLoop</c>).</summary>
    public void UpdateQueue(IReadOnlyList<QueueGroupStats> stats) => _queue = stats ?? [];

    private IEnumerable<Measurement<double>> Observe(Func<QueueGroupStats, double> value) =>
        _queue.Select(s => new Measurement<double>(value(s),
            new KeyValuePair<string, object?>(HostGroupTag, s.HostGroupId.ToString()),
            new KeyValuePair<string, object?>(HostGroupNameTag, s.Name)));
}
