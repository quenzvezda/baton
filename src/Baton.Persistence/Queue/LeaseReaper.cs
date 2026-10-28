using Baton.Domain.Runs;
using Baton.Persistence.Definitions;
using Baton.Persistence.Hosts;
using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>What one reaper pass closed.</summary>
/// <param name="CancelledAfterGrace">Attempts whose cancellation was requested <see cref="LeaseReaper.CancelGrace"/> ago (ORC-11).</param>
/// <param name="RunsTimedOut">Runs past their deadline whose cancellation began (AUT-09).</param>
public readonly record struct ReapSummary(int Lost, int TimedOut, int HostsMarkedOffline, int CancelledAfterGrace = 0, int RunsTimedOut = 0);

/// <summary>
/// The reaper's SQL (ORC-09, WF-05; design spec §7 "Expired leases"). Candidates are read without locks;
/// each is closed in its own transaction that locks the run, then re-checks the task under lock (design.md
/// "Lock order"), so replicas and a concurrent Complete never close an attempt twice. Since M4 it also closes
/// cancelled attempts whose handler did not stop within <see cref="CancelGrace"/> (ORC-11, M4 design.md
/// decision 6) and starts the cancellation of runs past their deadline, which then end TimedOut (AUT-09).
/// </summary>
public sealed class LeaseReaper(NpgsqlDataSource dataSource, DefinitionGraphCache graphs)
{
    /// <summary>Backstop past the task timeout before the platform closes a running attempt (WF-05).</summary>
    public static readonly TimeSpan TimeoutGrace = TimeSpan.FromSeconds(60);

    /// <summary>How long a host has to stop a cancelled task before the platform closes it (ORC-11).</summary>
    public static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(30);

    private const int BatchSize = 500;

    private const string ExpiredSql = """
        SELECT id FROM task_runs
        WHERE status IN ('leased','running') AND lease_expires_at <= now()
        ORDER BY lease_expires_at
        LIMIT @limit
        """;

    // The attempt's Start, or its claim when Start never arrived (design.md "Defaults taken").
    private const string OverdueSql = """
        SELECT id FROM task_runs
        WHERE status IN ('leased','running') AND COALESCE(started_at, leased_at) + timeout + @grace <= now()
        LIMIT @limit
        """;

    private const string CancelGraceSql = """
        SELECT id FROM task_runs
        WHERE status IN ('leased','running') AND cancel_requested AND cancel_requested_at + @cancelGrace <= now()
        LIMIT @limit
        """;

    private const string PastDeadlineSql = """
        SELECT id FROM workflow_runs WHERE status = 'running' AND deadline_at <= now() ORDER BY deadline_at LIMIT @limit
        """;

    private const string LockPastDeadlineSql = """
        SELECT id FROM workflow_runs WHERE id = @id AND status = 'running' AND deadline_at <= now() FOR UPDATE SKIP LOCKED
        """;

    private const string LockRunSql = """
        SELECT r.id FROM workflow_runs r WHERE r.id = (SELECT workflow_run_id FROM task_runs WHERE id = @id) FOR UPDATE
        """;

    private static readonly string LockExpiredSql = $"""
        SELECT {EndingAttempt.Columns} FROM task_runs t
        WHERE t.id = @id AND t.status IN ('leased','running') AND t.lease_expires_at <= now()
        FOR UPDATE SKIP LOCKED
        """;

    private static readonly string LockOverdueSql = $"""
        SELECT {EndingAttempt.Columns} FROM task_runs t
        WHERE t.id = @id AND t.status IN ('leased','running') AND COALESCE(t.started_at, t.leased_at) + t.timeout + @grace <= now()
        FOR UPDATE SKIP LOCKED
        """;

    private static readonly string LockCancelGraceSql = $"""
        SELECT {EndingAttempt.Columns} FROM task_runs t
        WHERE t.id = @id AND t.status IN ('leased','running') AND t.cancel_requested AND t.cancel_requested_at + @cancelGrace <= now()
        FOR UPDATE SKIP LOCKED
        """;

    // M2 design.md decision 9: persist what the inventory already derives, for the hosts page.
    private static readonly string OfflineSql = $"""
        UPDATE hosts h SET status = 'offline' WHERE h.status <> 'offline' AND {HostSql.IsOffline("h")}
        """;

    private readonly TaskFinisher _finisher = new(graphs);

    public async Task<ReapSummary> ReapAsync(CancellationToken ct)
    {
        var lost = 0;
        foreach (var id in await CandidatesAsync(ExpiredSql, ct))
        {
            if (await CloseAsync(id, LockExpiredSql, AttemptOutcome.Lost, null, ct))
                lost++;
        }

        var timedOut = 0;
        var error = TaskErrors.Json("Timeout", $"The attempt ran past its timeout plus {TimeoutGrace.TotalSeconds:0} s; closed by the platform.");
        foreach (var id in await CandidatesAsync(OverdueSql, ct))
        {
            if (await CloseAsync(id, LockOverdueSql, AttemptOutcome.TimedOut, error, ct))
                timedOut++;
        }

        var cancelled = 0;
        var graceError = TaskErrors.Json("CancelGraceExpired",
            $"The handler did not stop within {CancelGrace.TotalSeconds:0} s of the cancellation; closed by the platform.");
        foreach (var id in await CandidatesAsync(CancelGraceSql, ct))
        {
            if (await CloseAsync(id, LockCancelGraceSql, AttemptOutcome.Cancelled, graceError, ct))
                cancelled++;
        }

        var runsTimedOut = 0;
        foreach (var id in await CandidatesAsync(PastDeadlineSql, ct))
        {
            if (await TimeOutRunAsync(id, ct))
                runsTimedOut++;
        }

        await using var offline = dataSource.CreateCommand(OfflineSql);
        var hosts = await offline.ExecuteNonQueryAsync(ct);
        return new ReapSummary(lost, timedOut, hosts, cancelled, runsTimedOut);
    }

    private async Task<bool> TimeOutRunAsync(Guid runId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var lockRun = new NpgsqlCommand(LockPastDeadlineSql, connection, transaction))
        {
            lockRun.Parameters.AddWithValue("id", runId);
            if (await lockRun.ExecuteScalarAsync(ct) is null)
                return false;
        }

        await RunCancellation.CancelAsync(connection, transaction, runId, RunCancelReason.Timeout, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task<List<Guid>> CandidatesAsync(string sql, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("limit", BatchSize);
        cmd.Parameters.AddWithValue("grace", TimeoutGrace);
        cmd.Parameters.AddWithValue("cancelGrace", CancelGrace);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    private async Task<bool> CloseAsync(Guid id, string lockSql, AttemptOutcome outcome, string? error, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var lockRun = new NpgsqlCommand(LockRunSql, connection, transaction))
        {
            lockRun.Parameters.AddWithValue("id", id);
            await lockRun.ExecuteScalarAsync(ct);
        }

        EndingAttempt attempt;
        await using (var lockTask = new NpgsqlCommand(lockSql, connection, transaction))
        {
            lockTask.Parameters.AddWithValue("id", id);
            lockTask.Parameters.AddWithValue("grace", TimeoutGrace);
            lockTask.Parameters.AddWithValue("cancelGrace", CancelGrace);
            await using var reader = await lockTask.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return false;
            attempt = EndingAttempt.Read(reader);
        }

        var graph = await graphs.GetAsync(attempt.DefinitionHash, ct);
        var policy = graph[attempt.TaskKey].Retry with { MaxAttempts = attempt.MaxAttempts };
        var resolution = AttemptResolution.Resolve(outcome, retryable: true, attempt.CancelRequested, policy, attempt.Attempt);
        await _finisher.FinishAsync(connection, transaction, attempt, resolution, null,
            error ?? TaskErrors.Json("LeaseExpired", "The host stopped renewing the lease."), ct);

        await transaction.CommitAsync(ct);
        return true;
    }
}
