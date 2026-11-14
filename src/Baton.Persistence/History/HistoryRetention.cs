using Npgsql;

namespace Baton.Persistence.History;

/// <summary>
/// 90-day retention for run history (ADR-0011; add-history-retention design.md decisions 2–5). Every
/// method runs in the caller's transaction — the daily <c>PartitionMaintenanceLoop</c> tick — and takes
/// its cutoff from Postgres <c>now()</c>.
/// </summary>
public static class HistoryRetention
{
    /// <summary>Partitioned history tables, one partition per UTC month named <c>{table}_yYYYYmMM</c>.</summary>
    public static readonly string[] HistoryTables = ["task_attempts", "task_logs", "run_events"];

    public const int RetentionDays = 90;

    /// <summary>At most this many runs are purged per call (design.md decision 4).</summary>
    public const int PurgeBatchSize = 5_000;

    /// <summary>Audit entries are kept a year, apart from run history (AUT-11, M7 design.md decision 15).</summary>
    public const int AuditRetentionDays = 365;

    /// <summary>At most this many audit entries are deleted per statement.</summary>
    public const int AuditPurgeBatchSize = 10_000;

    private const string PurgeAuditSql = """
        DELETE FROM audit_log
        WHERE id IN (
          SELECT id FROM audit_log
          WHERE at < now() - make_interval(days => @days)
          ORDER BY at
          LIMIT @batch)
        """;

    /// <summary>
    /// Deletes every audit entry older than <see cref="AuditRetentionDays"/>, in batches of
    /// <see cref="AuditPurgeBatchSize"/>, and returns how many were deleted.
    /// </summary>
    public static async Task<int> PurgeAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var total = 0;
        int deleted;
        do
        {
            await using var cmd = new NpgsqlCommand(PurgeAuditSql, connection, transaction);
            cmd.Parameters.AddWithValue("days", AuditRetentionDays);
            cmd.Parameters.AddWithValue("batch", AuditPurgeBatchSize);
            deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;
        }
        while (deleted == AuditPurgeBatchSize);

        return total;
    }

    /// <summary>
    /// Children of the history tables with Baton's naming, with the UTC start of their month. Partitions
    /// with any other name are ignored, so hand-made partitions are never dropped by accident.
    /// </summary>
    private const string PartitionsSql = """
        SELECT c.relname,
               to_date(substring(c.relname FROM '_y(\d{4}m\d{2})$'), 'YYYY"m"MM') AS month_start
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        JOIN pg_class p ON p.oid = i.inhparent
        JOIN pg_namespace n ON n.oid = p.relnamespace AND n.nspname = current_schema()
        WHERE p.relname = ANY (@tables)
          AND c.relname = p.relname || substring(c.relname FROM '_y\d{4}m\d{2}$')
        """;

    private const string ExpiredPartitionsSql = $"""
        SELECT relname FROM ({PartitionsSql}) parts
        WHERE month_start + interval '1 month' <= (now() AT TIME ZONE 'UTC') - make_interval(days => @days)
        ORDER BY relname
        """;

    private const string ShortTablesSql = $"""
        SELECT t.name
        FROM unnest(@tables::text[]) AS t(name)
        LEFT JOIN ({PartitionsSql}) parts
          ON parts.relname = t.name || to_char(parts.month_start, '"_y"YYYY"m"MM')
         AND parts.month_start > date_trunc('month', now() AT TIME ZONE 'UTC')
        GROUP BY t.name
        HAVING count(parts.relname) < @minAhead
        ORDER BY t.name
        """;

    private const string PurgeRunsSql = """
        DELETE FROM workflow_runs
        WHERE id IN (
          SELECT id FROM workflow_runs
          WHERE status IN ('succeeded','failed','cancelled','timed_out')
            AND finished_at < now() - make_interval(days => @days)
          ORDER BY finished_at
          LIMIT @batch)
        """;

    /// <summary>
    /// Drops every history partition whose whole month ended at least <see cref="RetentionDays"/> ago and
    /// returns their names. <c>lock_timeout</c> keeps writers from queueing behind a blocked drop
    /// (design.md decision 3); a timeout fails the caller's transaction.
    /// </summary>
    public static async Task<IReadOnlyList<string>> DropExpiredPartitionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using (var timeout = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction))
            await timeout.ExecuteNonQueryAsync(ct);

        var expired = new List<string>();
        await using (var cmd = new NpgsqlCommand(ExpiredPartitionsSql, connection, transaction))
        {
            cmd.Parameters.AddWithValue("tables", HistoryTables);
            cmd.Parameters.AddWithValue("days", RetentionDays);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                expired.Add(reader.GetString(0));
        }

        foreach (var partition in expired)
        {
            // Names come from the catalog and match the strict pattern above; still quoted as identifiers.
            await using var drop = new NpgsqlCommand($"DROP TABLE {QuoteIdentifier(partition)}", connection, transaction);
            await drop.ExecuteNonQueryAsync(ct);
        }

        return expired;
    }

    /// <summary>
    /// Deletes up to <see cref="PurgeBatchSize"/> finished runs whose <c>finished_at</c> is more than
    /// <see cref="RetentionDays"/> ago, oldest first; their tasks go through <c>ON DELETE CASCADE</c>.
    /// Runs that are not finished are never deleted. Returns how many runs were deleted.
    /// </summary>
    public static async Task<int> PurgeFinishedRunsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(PurgeRunsSql, connection, transaction);
        cmd.Parameters.AddWithValue("days", RetentionDays);
        cmd.Parameters.AddWithValue("batch", PurgeBatchSize);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// History tables with fewer than <paramref name="minAhead"/> partitions for months after the current
    /// UTC month (ADR-0011: alert when fewer than 2 exist). Empty when every table is covered.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindTablesShortOfFuturePartitionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int minAhead, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(ShortTablesSql, connection, transaction);
        cmd.Parameters.AddWithValue("tables", HistoryTables);
        cmd.Parameters.AddWithValue("minAhead", (long)minAhead);

        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
