using Baton.Orchestrator.Health;
using Baton.Persistence;
using Baton.Persistence.History;
using Npgsql;

namespace Baton.Orchestrator.Loops;

/// <summary>
/// Daily history housekeeping on one replica (ADR-0011): keeps monthly history partitions created
/// 3 months ahead through <c>ensure_history_partitions</c> (baseline migration), then enforces the
/// 90-day retention — drops expired partitions and purges old finished runs (add-history-retention).
/// All in the singleton tick's transaction.
/// </summary>
public sealed partial class PartitionMaintenanceLoop(
    NpgsqlDataSource dataSource, OrchestratorMetrics metrics, ILogger<PartitionMaintenanceLoop> logger)
    : SingletonLoop(dataSource, logger)
{
    private const int MonthsAhead = 3;

    protected override long LockId => AdvisoryLocks.PartitionMaintenance;

    protected override TimeSpan Interval => TimeSpan.FromDays(1);

    protected override async Task TickAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using (var cmd = new NpgsqlCommand("SELECT ensure_history_partitions(@monthsAhead)", connection, transaction))
        {
            cmd.Parameters.AddWithValue("monthsAhead", MonthsAhead);
            var created = (int)(await cmd.ExecuteScalarAsync(ct))!;
            if (created > 0)
                LogPartitionsCreated(Logger, created);
        }

        var dropped = await HistoryRetention.DropExpiredPartitionsAsync(connection, transaction, ct);
        if (dropped.Count > 0)
        {
            metrics.PartitionsDropped(dropped.Count);
            LogPartitionsDropped(Logger, dropped.Count, dropped);
        }

        var purged = await HistoryRetention.PurgeFinishedRunsAsync(connection, transaction, ct);
        if (purged > 0)
        {
            metrics.RunsPurged(purged);
            LogRunsPurged(Logger, purged, HistoryRetention.RetentionDays);
        }

        var audited = await HistoryRetention.PurgeAuditAsync(connection, transaction, ct);
        if (audited > 0)
            LogAuditPurged(Logger, audited, HistoryRetention.AuditRetentionDays);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} audit entries older than {Days} days")]
    private static partial void LogAuditPurged(ILogger logger, int count, int days);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created {Count} history partitions")]
    private static partial void LogPartitionsCreated(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped {Count} expired history partitions: {Partitions}")]
    private static partial void LogPartitionsDropped(ILogger logger, int count, IReadOnlyList<string> partitions);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} runs finished more than {Days} days ago")]
    private static partial void LogRunsPurged(ILogger logger, int count, int days);
}
