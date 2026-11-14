using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>
/// Ends a run once none of its tasks is left unfinished ("Run outcome", design.md "Finalization"): a cancelling
/// run ends Cancelled, or TimedOut when its deadline cancelled it, or Failed when its pinned host was lost (ORC-12);
/// otherwise Failed if any task Failed, else Succeeded (Skipped tasks alone never fail a run). The caller holds the run row and releases the limits
/// this returns in the same transaction. A run that ends Failed or TimedOut schedules one notification delivery
/// per matching channel of its (not deleted) automation in the same statement: the outbox of ADR-0018 (OBS-05,
/// add-operability-and-insight design.md decision 18). Channel rows are only read, so no lock is added.
/// </summary>
internal static class RunFinalizer
{
    private const string FinalizeSql = """
        WITH ended AS (
          UPDATE workflow_runs r SET
            status = CASE
                       WHEN r.status = 'cancelling' THEN CASE r.cancel_reason WHEN 'timeout' THEN 'timed_out'
                                                                              WHEN 'pinned_host_lost' THEN 'failed'
                                                                              ELSE 'cancelled' END
                       WHEN EXISTS (SELECT 1 FROM task_runs t WHERE t.workflow_run_id = r.id AND t.status = 'failed') THEN 'failed'
                       ELSE 'succeeded'
                     END,
            finished_at = now()
          WHERE r.id = @runId AND r.status IN ('running','cancelling')
            AND NOT EXISTS (SELECT 1 FROM task_runs t WHERE t.workflow_run_id = r.id
                            AND t.status NOT IN ('succeeded','failed','skipped','cancelled'))
          RETURNING r.id, r.status, r.held_limits, r.cancel_reason, r.pinned_host_id, r.automation_id),
        events AS (
          INSERT INTO run_events (workflow_run_id, type, data)
          SELECT id, 'run.' || status,
                 CASE WHEN cancel_reason = 'pinned_host_lost'
                      THEN jsonb_build_object('released', held_limits, 'reason', 'pinned host lost', 'hostId', pinned_host_id)
                      ELSE jsonb_build_object('released', held_limits) END
          FROM ended),
        notify AS (
          INSERT INTO notification_deliveries (workflow_run_id, channel_id, event)
          SELECT e.id, c.id, e.status
          FROM ended e
          JOIN automations a ON a.id = e.automation_id AND a.deleted_at IS NULL
          JOIN notification_channels c ON c.automation_id = a.id AND e.status = ANY (c.events)
          WHERE e.status IN ('failed','timed_out'))
        SELECT held_limits FROM ended
        """;

    /// <returns>The limit keys the run held, to release; empty when the run is not over yet.</returns>
    public static async Task<IReadOnlyList<string>> TryFinalizeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(FinalizeSql, connection, transaction);
        cmd.Parameters.AddWithValue("runId", runId);
        return await cmd.ExecuteScalarAsync(ct) as string[] ?? [];
    }

    /// <summary>Finalizes if possible and releases what the run held, in key order.</summary>
    public static async Task FinalizeAndReleaseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, CancellationToken ct)
    {
        var held = await TryFinalizeAsync(connection, transaction, runId, ct);
        await LimitRows.ReleaseAsync(connection, transaction, held, ct);
    }
}
