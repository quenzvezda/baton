using Npgsql;

namespace Baton.Persistence.Runs;

/// <summary>One host group's queue (OBS-04).</summary>
/// <param name="OldestWaitSeconds">How long the oldest Queued task has waited; <c>null</c> when none is queued.</param>
/// <param name="ActiveLeases">Tasks Leased or Running.</param>
/// <param name="LeaseExpirationsLastHour">Attempts lost to lease expiry in the last 60 minutes.</param>
public sealed record QueueGroupStats(
    Guid HostGroupId,
    string Name,
    bool Paused,
    int Queued,
    double? OldestWaitSeconds,
    int PendingRuns,
    int ActiveLeases,
    int LeaseExpirationsLastHour);

/// <summary>
/// Queue statistics per host group (OBS-04, add-operability-and-insight design.md decision 24), for the API and
/// the orchestrator's gauges. One round trip of hand-written SQL (ADR-0007), every figure from current state and
/// Postgres <c>now()</c>. Each aggregate is served by a partial index: <c>ix_tasks_dispatch</c> (queued),
/// <c>ix_tasks_leases</c> (leased, running), <c>ix_runs_admission</c> (pending) and <c>ix_attempts_lost</c>.
/// </summary>
public sealed class QueueStats(NpgsqlDataSource dataSource)
{
    private const string StatsSql = """
        SELECT g.id, g.name, g.dispatch_paused, q.queued,
               extract(epoch FROM now() - q.oldest)::float8,
               p.pending, a.active, l.lost
        FROM host_groups g
        CROSS JOIN LATERAL (
          SELECT count(*)::int AS queued, min(t.queued_at) AS oldest
          FROM task_runs t WHERE t.host_group_id = g.id AND t.status = 'queued') q
        CROSS JOIN LATERAL (
          SELECT count(*)::int AS pending
          FROM workflow_runs r WHERE r.host_group_id = g.id AND r.status = 'pending') p
        CROSS JOIN LATERAL (
          SELECT count(*)::int AS active
          FROM task_runs t WHERE t.host_group_id = g.id AND t.status IN ('leased','running')) a
        CROSS JOIN LATERAL (
          SELECT count(*)::int AS lost
          FROM task_attempts ta JOIN task_runs t ON t.id = ta.task_run_id
          WHERE ta.outcome = 'lost' AND ta.finished_at >= now() - interval '1 hour' AND t.host_group_id = g.id) l
        ORDER BY g.name
        """;

    /// <summary>The SQL, for <c>EXPLAIN</c> in the M7 report.</summary>
    public static string Sql => StatsSql;

    public async Task<IReadOnlyList<QueueGroupStats>> ReadAsync(CancellationToken ct)
    {
        var stats = new List<QueueGroupStats>();
        await using var cmd = dataSource.CreateCommand(StatsSql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            stats.Add(new QueueGroupStats(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : Math.Max(0, reader.GetDouble(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7)));
        }

        return stats;
    }
}
