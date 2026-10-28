using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>What a cancel request found.</summary>
public enum CancelOutcome
{
    NotFound,

    /// <summary>The run had already ended; nothing changed (409).</summary>
    AlreadyFinished,

    /// <summary>A Pending run, cancelled at once.</summary>
    Cancelled,

    /// <summary>A Running run is now Cancelling; it ends once its leased tasks are final.</summary>
    Cancelling,

    /// <summary>The run was already cancelling; nothing changed.</summary>
    AlreadyCancelling,
}

/// <summary>
/// Cancels a run (ORC-11, design spec §6 "Cancellation") for an Operator, a Replace firing or the run's deadline:
/// a Pending run ends Cancelled at once; a Running run becomes Cancelling, its Waiting, Queued and Retry wait
/// tasks are Cancelled at once, its Leased and Running tasks get <c>cancel_requested</c> (their hosts are told
/// on the next heartbeat; the reaper closes them after <see cref="LeaseReaper.CancelGrace"/>), and it ends once
/// every task is final. Lock order: run row, then task rows, then limit rows.
/// </summary>
public sealed class RunCancellation(NpgsqlDataSource dataSource)
{
    private const string LockRunSql = "SELECT status FROM workflow_runs WHERE id = @id FOR UPDATE";

    private const string CancelPendingSql = """
        UPDATE workflow_runs SET status = 'cancelled', cancel_reason = @reason, finished_at = now() WHERE id = @id
        """;

    private const string CancelUnleasedSql = """
        WITH cancelled AS (
          UPDATE task_runs SET status = 'cancelled', finished_at = now(), not_before = NULL
          WHERE workflow_run_id = @id AND status IN ('waiting','queued','retry_wait')
          RETURNING task_key, attempt)
        INSERT INTO run_events (workflow_run_id, task_key, type, data)
        SELECT @id, task_key, 'task.cancelled', jsonb_build_object('attempt', attempt) FROM cancelled
        """;

    // A statement of its own (own snapshot), so it sees a task that a concurrent claim leased while the one
    // above waited for its row lock.
    private const string RequestLeasedSql = """
        WITH requested AS (
          UPDATE task_runs SET cancel_requested = true, cancel_requested_at = now()
          WHERE workflow_run_id = @id AND status IN ('leased','running') AND NOT cancel_requested
          RETURNING task_key, attempt, lease_host_id)
        INSERT INTO run_events (workflow_run_id, task_key, type, data)
        SELECT @id, task_key, 'task.cancel_requested', jsonb_build_object('attempt', attempt, 'hostId', lease_host_id)
        FROM requested
        """;

    private const string MarkCancellingSql = """
        UPDATE workflow_runs SET status = 'cancelling', cancel_reason = @reason WHERE id = @id
        """;

    public async Task<CancelOutcome> CancelAsync(Guid runId, RunCancelReason reason, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var outcome = await CancelAsync(connection, transaction, runId, reason, ct);
        await transaction.CommitAsync(ct);
        return outcome;
    }

    /// <summary>In the caller's transaction, e.g. a Replace firing that holds the automation row.</summary>
    internal static async Task<CancelOutcome> CancelAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, RunCancelReason reason, CancellationToken ct)
    {
        string? status;
        await using (var lockRun = new NpgsqlCommand(LockRunSql, connection, transaction))
        {
            lockRun.Parameters.AddWithValue("id", runId);
            status = await lockRun.ExecuteScalarAsync(ct) as string;
        }

        switch (status)
        {
            case null:
                return CancelOutcome.NotFound;
            case "cancelling":
                return CancelOutcome.AlreadyCancelling;
            case "pending":
                await using (var batch = new NpgsqlBatch(connection, transaction))
                {
                    batch.BatchCommands.Add(Command(CancelPendingSql, runId, reason));
                    batch.BatchCommands.Add(RunEvents.Command(runId, null, RunEventTypes.RunCancelled, new { reason = DbNames.CancelReason(reason) }));
                    await batch.ExecuteNonQueryAsync(ct);
                }

                return CancelOutcome.Cancelled;
            case "running":
                break;
            default:
                return CancelOutcome.AlreadyFinished;
        }

        await using (var batch = new NpgsqlBatch(connection, transaction))
        {
            batch.BatchCommands.Add(Command(CancelUnleasedSql, runId, reason));
            batch.BatchCommands.Add(Command(RequestLeasedSql, runId, reason));
            batch.BatchCommands.Add(Command(MarkCancellingSql, runId, reason));
            batch.BatchCommands.Add(RunEvents.Command(runId, null, RunEventTypes.RunCancelling, new { reason = DbNames.CancelReason(reason) }));
            await batch.ExecuteNonQueryAsync(ct);
        }

        // Nothing leased: the run is over now.
        await RunFinalizer.FinalizeAndReleaseAsync(connection, transaction, runId, ct);
        return CancelOutcome.Cancelling;
    }

    private static NpgsqlBatchCommand Command(string sql, Guid runId, RunCancelReason reason)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddWithValue("id", runId);
        if (sql.Contains("@reason", StringComparison.Ordinal))
            command.Parameters.AddWithValue("reason", DbNames.CancelReason(reason));
        return command;
    }
}
