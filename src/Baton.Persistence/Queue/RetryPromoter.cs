using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>
/// ORC-10: moves Retry wait tasks whose backoff has passed back to Queued, with <c>SKIP LOCKED</c> so every
/// replica may run it, and wakes their groups' claimers on commit.
/// </summary>
public sealed class RetryPromoter(NpgsqlDataSource dataSource)
{
    private const string PromoteSql = """
        WITH due AS (
          SELECT id FROM task_runs
          WHERE status = 'retry_wait' AND not_before <= now()
          ORDER BY not_before
          LIMIT 500
          FOR UPDATE SKIP LOCKED),
        queued AS (
          UPDATE task_runs t SET status = 'queued', queued_at = now(), not_before = NULL
          FROM due WHERE t.id = due.id
          RETURNING t.workflow_run_id, t.task_key, t.attempt, t.host_group_id),
        events AS (
          INSERT INTO run_events (workflow_run_id, task_key, type, data)
          SELECT workflow_run_id, task_key, 'task.queued', jsonb_build_object('attempt', attempt + 1, 'retry', true)
          FROM queued),
        groups AS (SELECT DISTINCT host_group_id FROM queued)
        SELECT (SELECT count(*)::int FROM queued), pg_notify('tasks_ready', host_group_id::text)
        FROM groups
        """;

    /// <returns>How many tasks became Queued.</returns>
    public async Task<int> PromoteAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(PromoteSql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? reader.GetInt32(0) : 0;
    }
}
