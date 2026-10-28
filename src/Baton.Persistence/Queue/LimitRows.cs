using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>Outcome of reserving a run's limits; <see cref="BlockedOn"/> is the first full limit in key order.</summary>
internal readonly record struct Reservation(bool Reserved, IReadOnlyList<string> Held, string? BlockedOn, int Max, int InUse);

/// <summary>
/// <c>concurrency_limits</c> rows in the caller's transaction (ADR-0009). Rows are always locked in key order,
/// so two transactions that touch overlapping keys never wait on each other in a cycle. A key without a row
/// is unlimited.
/// </summary>
internal static class LimitRows
{
    private const string LockSql = """
        SELECT key, max, in_use FROM concurrency_limits WHERE key = ANY (@keys) ORDER BY key FOR UPDATE
        """;

    private const string ReserveSql = "UPDATE concurrency_limits SET in_use = in_use + 1 WHERE key = ANY (@keys)";

    // GREATEST: a row created after the reservation must not violate in_use >= 0; the reconciler corrects it.
    private const string ReleaseSql = "UPDATE concurrency_limits SET in_use = GREATEST(in_use - 1, 0) WHERE key = ANY (@keys)";

    /// <summary>Locks every existing row of <paramref name="keys"/> and takes one unit of each, or none if any is full.</summary>
    public static async Task<Reservation> TryReserveAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var rows = await LockAsync(connection, transaction, keys, ct);
        foreach (var (key, max, inUse) in rows)
        {
            if (inUse >= max)
                return new Reservation(false, [], key, max, inUse);
        }

        var held = rows.Select(r => r.Key).ToArray();
        if (held.Length > 0)
        {
            await using var reserve = new NpgsqlCommand(ReserveSql, connection, transaction);
            reserve.Parameters.AddWithValue("keys", held);
            await reserve.ExecuteNonQueryAsync(ct);
        }

        return new Reservation(true, held, null, 0, 0);
    }

    /// <summary>Gives back one unit of each key, locking the rows in key order first.</summary>
    public static async Task ReleaseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
            return;

        var rows = await LockAsync(connection, transaction, keys, ct);
        if (rows.Count == 0)
            return;

        await using var release = new NpgsqlCommand(ReleaseSql, connection, transaction);
        release.Parameters.AddWithValue("keys", rows.Select(r => r.Key).ToArray());
        await release.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<(string Key, int Max, int InUse)>> LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var rows = new List<(string, int, int)>();
        if (keys.Count == 0)
            return rows;

        await using var cmd = new NpgsqlCommand(LockSql, connection, transaction);
        cmd.Parameters.AddWithValue("keys", keys.Distinct(StringComparer.Ordinal).ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        return rows;
    }
}
