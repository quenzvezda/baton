using Baton.Persistence;
using Npgsql;

namespace Baton.Orchestrator.Loops;

/// <summary>
/// Keeps monthly history partitions created 3 months ahead (ADR-0011) by calling
/// <c>ensure_history_partitions</c>, which the baseline migration defines. Dropping partitions past
/// the 90-day retention is not done here yet (gate G1).
/// </summary>
public sealed partial class PartitionMaintenanceLoop(NpgsqlDataSource dataSource, ILogger<PartitionMaintenanceLoop> logger)
    : SingletonLoop(dataSource, logger)
{
    private const int MonthsAhead = 3;

    protected override long LockId => AdvisoryLocks.PartitionMaintenance;

    protected override TimeSpan Interval => TimeSpan.FromDays(1);

    protected override async Task TickAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT ensure_history_partitions(@monthsAhead)", connection, transaction);
        cmd.Parameters.AddWithValue("monthsAhead", MonthsAhead);

        var created = (int)(await cmd.ExecuteScalarAsync(ct))!;
        if (created > 0)
            LogPartitionsCreated(Logger, created);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created {Count} history partitions")]
    private static partial void LogPartitionsCreated(ILogger logger, int count);
}
