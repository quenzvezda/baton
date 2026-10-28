using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>
/// Ends a run once none of its tasks is left unfinished ("Run outcome", design.md "Finalization"): a cancelling
/// run ends Cancelled, or TimedOut when its deadline cancelled it; otherwise Failed if any task Failed, else
/// Succeeded (Skipped tasks alone never fail a run). The caller holds the run row and releases the limits
/// this returns in the same transaction.
/// </summary>
internal static class RunFinalizer
{
    private const string FinalizeSql = """
        WITH ended AS (
          UPDATE workflow_runs r SET
            status = CASE
                       WHEN r.status = 'cancelling' THEN CASE WHEN r.cancel_reason = 'timeout' THEN 'timed_out' ELSE 'cancelled' END
                       WHEN EXISTS (SELECT 1 FROM task_runs t WHERE t.workflow_run_id = r.id AND t.status = 'failed') THEN 'failed'
                       ELSE 'succeeded'
                     END,
            finished_at = now()
          WHERE r.id = @runId AND r.status IN ('running','cancelling')
            AND NOT EXISTS (SELECT 1 FROM task_runs t WHERE t.workflow_run_id = r.id
                            AND t.status NOT IN ('succeeded','failed','skipped','cancelled'))
          RETURNING r.id, r.status, r.held_limits),
        events AS (
          INSERT INTO run_events (workflow_run_id, type, data)
          SELECT id, 'run.' || status, jsonb_build_object('released', held_limits) FROM ended)
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
