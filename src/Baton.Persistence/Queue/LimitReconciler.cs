using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>A limit counter the reconciler corrected.</summary>
public readonly record struct LimitCorrection(string Key, int Was, int Now);

/// <summary>
/// Safety net for <c>concurrency_limits.in_use</c> (design.md decision 8, design spec §7 "Operational notes"):
/// locks every row in key order (ADR-0009) and recomputes it from live rows. <c>group-tasks:{id}</c> counts
/// leased and running tasks of the group; every other key counts active runs that hold it.
/// </summary>
public static class LimitReconciler
{
    private const string LockSql = "SELECT key FROM concurrency_limits ORDER BY key FOR UPDATE";

    private const string ReconcileSql = """
        WITH actual AS (
          SELECT l.key, l.in_use AS was,
                 CASE WHEN l.key LIKE 'group-tasks:%' THEN
                   (SELECT count(*)::int FROM task_runs t
                    WHERE t.status IN ('leased','running') AND t.host_group_id::text = substr(l.key, 13))
                 ELSE
                   (SELECT count(*)::int FROM workflow_runs r
                    WHERE r.status IN ('running','cancelling') AND l.key = ANY (r.held_limits))
                 END AS counted
          FROM concurrency_limits l)
        UPDATE concurrency_limits l SET in_use = a.counted
        FROM actual a
        WHERE l.key = a.key AND l.in_use <> a.counted
        RETURNING l.key, a.was, a.counted
        """;

    /// <summary>Runs in the caller's (singleton) transaction.</summary>
    public static async Task<IReadOnlyList<LimitCorrection>> ReconcileAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using (var lockRows = new NpgsqlCommand(LockSql, connection, transaction))
            await lockRows.ExecuteNonQueryAsync(ct);

        var corrections = new List<LimitCorrection>();
        await using var cmd = new NpgsqlCommand(ReconcileSql, connection, transaction);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            corrections.Add(new LimitCorrection(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        return corrections;
    }
}
