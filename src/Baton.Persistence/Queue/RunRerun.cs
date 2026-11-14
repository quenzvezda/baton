using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>What a re-run request found.</summary>
public enum RerunOutcome
{
    NotFound,

    /// <summary>The run is not Failed or TimedOut; nothing changed (409).</summary>
    NotRerunnable,

    /// <summary>The run is Pending again and waits for admission.</summary>
    Requested,
}

/// <summary>
/// Re-runs a Failed or TimedOut run in place (WF-10, add-operability-and-insight design.md decisions 1, 9): one
/// transaction locks the run row (waiting the few milliseconds a finisher may hold it, rather than skipping),
/// checks its status, puts it back to Pending with one more re-run, clears what its last ending left behind
/// (finish time, cancel reason, pin, blocked limit, deadline) and records who asked. Tasks are not touched: they
/// stay final, invisible to claims and the reaper, until admission re-arms them under the run's limits
/// (<see cref="RunMaterializer.RearmTasksAsync"/>). The finalizer released the run's limits, so none are held.
/// </summary>
public sealed class RunRerun(NpgsqlDataSource dataSource)
{
    private const string LockRunSql = "SELECT status FROM workflow_runs WHERE id = @id FOR UPDATE";

    private const string RequestSql = """
        UPDATE workflow_runs SET status = 'pending', reruns = reruns + 1, finished_at = NULL, cancel_reason = NULL,
                                 pinned_host_id = NULL, blocked_on = NULL, deadline_at = NULL, held_limits = '{}'
        WHERE id = @id
        RETURNING reruns
        """;

    public async Task<RerunOutcome> RequestAsync(Guid runId, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        string? status;
        await using (var lockRun = new NpgsqlCommand(LockRunSql, connection, transaction))
        {
            lockRun.Parameters.AddWithValue("id", runId);
            status = await lockRun.ExecuteScalarAsync(ct) as string;
        }

        if (status is null)
            return RerunOutcome.NotFound;
        if (status is not ("failed" or "timed_out"))
            return RerunOutcome.NotRerunnable;

        int rerun;
        await using (var request = new NpgsqlCommand(RequestSql, connection, transaction))
        {
            request.Parameters.AddWithValue("id", runId);
            rerun = (int)(await request.ExecuteScalarAsync(ct))!;
        }

        await RunEvents.AddAsync(connection, transaction, runId, null, RunEventTypes.RunRerunRequested,
            new { actor, rerun, previousStatus = status }, ct);
        await transaction.CommitAsync(ct);
        return RerunOutcome.Requested;
    }
}
