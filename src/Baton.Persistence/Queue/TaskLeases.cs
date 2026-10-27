using Baton.Domain.Runs;
using Baton.Persistence.Definitions;
using Baton.Persistence.Hosts;
using Npgsql;
using NpgsqlTypes;

namespace Baton.Persistence.Queue;

/// <summary>A lease the host says it holds.</summary>
public readonly record struct HeldLease(Guid TaskRunId, long LeaseToken);

/// <summary>Leases the host no longer owns, and owned leases whose cancellation was requested (ORC-07).</summary>
public sealed record LeaseRenewal(IReadOnlyList<Guid> Lost, IReadOnlyList<Guid> CancelRequested);

/// <summary>What an agent reports when an attempt ends.</summary>
/// <param name="Outcome">Succeeded, Failed, TimedOut or Cancelled.</param>
/// <param name="Retryable">For a failure: whether a retry may succeed (WF-08).</param>
public sealed record AttemptReport(
    Guid TaskRunId,
    long LeaseToken,
    AttemptOutcome Outcome,
    string? OutputJson,
    int OutputBytes,
    string? ErrorJson,
    bool Retryable);

/// <summary>One log line from a handler (OBS-03).</summary>
public sealed record TaskLogLine(int Seq, DateTime LoggedAt, int Level, string Message);

/// <summary>
/// Every agent → orchestrator write after a claim, fenced by the lease token (ORC-07, ORC-08, ADR-0006).
/// A lease counts only while <c>lease_expires_at &gt; now()</c>, even before the reaper closes it
/// (design.md "Implementation notes"), and only for a host the calling credential may act for.
/// </summary>
public sealed class TaskLeases(NpgsqlDataSource dataSource, DefinitionGraphCache graphs)
{
    /// <summary>The lease is current: same token, still held and not expired, by a host the credential may act for.</summary>
    private static readonly string Fenced = $"""
        t.lease_token = @token AND t.status IN ('leased','running') AND t.lease_expires_at > now()
        AND EXISTS (SELECT 1 FROM hosts h WHERE h.id = t.lease_host_id AND {HostSql.MayActFor("h")})
        """;

    private const string RenewSql = """
        UPDATE task_runs t SET lease_expires_at = now() + make_interval(secs => g.lease_seconds)
        FROM unnest(@ids::uuid[], @tokens::bigint[]) AS l(id, token), host_groups g
        WHERE t.id = l.id AND t.lease_token = l.token AND t.lease_host_id = @hostId
          AND t.status IN ('leased','running') AND t.lease_expires_at > now()
          AND g.id = t.host_group_id
        RETURNING t.id, t.cancel_requested
        """;

    private static readonly string StartSql = $"""
        WITH started AS (
          UPDATE task_runs t SET status = 'running', started_at = now()
          WHERE t.id = @id AND t.status = 'leased' AND {Fenced}
          RETURNING t.workflow_run_id, t.task_key, t.attempt)
        INSERT INTO run_events (workflow_run_id, task_key, type, data)
        SELECT workflow_run_id, task_key, 'task.started', jsonb_build_object('attempt', attempt) FROM started
        """;

    private static readonly string IsCurrentSql = $"SELECT t.attempt FROM task_runs t WHERE t.id = @id AND {Fenced}";

    // Agent times are kept for display only, clamped near now() so a wrong clock cannot miss a partition;
    // a retried chunk carries the same times and is deduplicated by the primary key.
    private const string AppendLogsSql = """
        INSERT INTO task_logs (task_run_id, attempt, seq, logged_at, level, message)
        SELECT @id, @attempt, l.seq,
               CASE WHEN l.at BETWEEN now() - interval '1 day' AND now() + interval '5 minutes' THEN l.at ELSE now() END,
               l.level, l.message
        FROM unnest(@seqs::int[], @ats::timestamptz[], @levels::smallint[], @messages::text[]) AS l(seq, at, level, message)
        ON CONFLICT DO NOTHING
        """;

    private const string LockRunSql = """
        SELECT r.id FROM workflow_runs r WHERE r.id = (SELECT workflow_run_id FROM task_runs WHERE id = @id) FOR UPDATE
        """;

    private static readonly string LockTaskSql = $"""
        SELECT {EndingAttempt.Columns} FROM task_runs t WHERE t.id = @id AND {Fenced} FOR UPDATE
        """;

    private readonly TaskFinisher _finisher = new(graphs);

    /// <summary>Renews every current lease the host holds; anything else in <paramref name="leases"/> is lost.</summary>
    public async Task<LeaseRenewal> RenewAsync(Guid hostId, IReadOnlyCollection<HeldLease> leases, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(leases);
        if (leases.Count == 0)
            return new LeaseRenewal([], []);

        var renewed = new Dictionary<Guid, bool>();
        await using (var cmd = dataSource.CreateCommand(RenewSql))
        {
            cmd.Parameters.AddWithValue("hostId", hostId);
            cmd.Parameters.AddWithValue("ids", leases.Select(l => l.TaskRunId).ToArray());
            cmd.Parameters.AddWithValue("tokens", leases.Select(l => l.LeaseToken).ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                renewed[reader.GetGuid(0)] = reader.GetBoolean(1);
        }

        return new LeaseRenewal(
            [.. leases.Select(l => l.TaskRunId).Where(id => !renewed.ContainsKey(id)).Distinct()],
            [.. renewed.Where(r => r.Value).Select(r => r.Key)]);
    }

    /// <summary>Leased → Running (ORC-08). A repeated Start of a running attempt is accepted again.</summary>
    /// <returns><c>false</c> when the lease is stale.</returns>
    public async Task<bool> StartAsync(Guid credentialGroupId, Guid taskRunId, long leaseToken, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var start = new NpgsqlCommand(StartSql, connection, transaction))
        {
            AddFence(start, credentialGroupId, taskRunId, leaseToken);
            await start.ExecuteNonQueryAsync(ct);
        }

        var current = await CurrentAttemptAsync(connection, transaction, credentialGroupId, taskRunId, leaseToken, ct);
        await transaction.CommitAsync(ct);
        return current is not null;
    }

    /// <returns>The number of lines stored, or <c>null</c> when the lease is stale and nothing was stored.</returns>
    public async Task<int?> AppendLogsAsync(
        Guid credentialGroupId, Guid taskRunId, int attempt, long leaseToken, IReadOnlyList<TaskLogLine> lines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        if (await CurrentAttemptAsync(connection, transaction, credentialGroupId, taskRunId, leaseToken, ct) != attempt)
            return null;

        await using var insert = new NpgsqlCommand(AppendLogsSql, connection, transaction);
        insert.Parameters.AddWithValue("id", taskRunId);
        insert.Parameters.AddWithValue("attempt", attempt);
        insert.Parameters.AddWithValue("seqs", lines.Select(l => l.Seq).ToArray());
        insert.Parameters.AddWithValue("ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, lines.Select(l => l.LoggedAt).ToArray());
        insert.Parameters.AddWithValue("levels", lines.Select(l => (short)l.Level).ToArray());
        insert.Parameters.AddWithValue("messages", lines.Select(l => l.Message).ToArray());
        var stored = await insert.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return stored;
    }

    /// <summary>
    /// Ends the attempt and advances the DAG in one transaction (ORC-03, ORC-08). Output over 256 KB fails
    /// the task without retry (WF-07).
    /// </summary>
    /// <returns><c>false</c> when the lease is stale; nothing was changed.</returns>
    public async Task<bool> CompleteAsync(Guid credentialGroupId, AttemptReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var lockRun = new NpgsqlCommand(LockRunSql, connection, transaction))
        {
            lockRun.Parameters.AddWithValue("id", report.TaskRunId);
            if (await lockRun.ExecuteScalarAsync(ct) is null)
                return false;
        }

        EndingAttempt attempt;
        await using (var lockTask = new NpgsqlCommand(LockTaskSql, connection, transaction))
        {
            AddFence(lockTask, credentialGroupId, report.TaskRunId, report.LeaseToken);
            await using var reader = await lockTask.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return false;
            attempt = EndingAttempt.Read(reader);
        }

        var (outcome, output, error, retryable) = (report.Outcome, report.OutputJson, report.ErrorJson, report.Retryable);
        if (outcome == AttemptOutcome.Succeeded && report.OutputBytes > TaskOutput.MaxBytes)
        {
            (outcome, output, retryable) = (AttemptOutcome.Failed, null, false);
            error = TaskErrors.Json("OutputTooLarge",
                $"Output is {report.OutputBytes} bytes; the limit is {TaskOutput.MaxBytes} (256 KB).");
        }

        var graph = await graphs.GetAsync(attempt.DefinitionHash, ct);
        var policy = graph[attempt.TaskKey].Retry with { MaxAttempts = attempt.MaxAttempts };
        var resolution = AttemptResolution.Resolve(outcome, retryable, attempt.CancelRequested, policy, attempt.Attempt);
        await _finisher.FinishAsync(connection, transaction, attempt, resolution,
            resolution.Status == TaskRunStatus.Succeeded ? output : null, error, ct);

        await transaction.CommitAsync(ct);
        return true;
    }

    private static async Task<int?> CurrentAttemptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid credentialGroupId, Guid taskRunId, long leaseToken,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(IsCurrentSql, connection, transaction);
        AddFence(cmd, credentialGroupId, taskRunId, leaseToken);
        return await cmd.ExecuteScalarAsync(ct) is int attempt ? attempt : null;
    }

    private static void AddFence(NpgsqlCommand cmd, Guid credentialGroupId, Guid taskRunId, long leaseToken)
    {
        cmd.Parameters.AddWithValue("id", taskRunId);
        cmd.Parameters.AddWithValue("token", leaseToken);
        cmd.Parameters.AddWithValue("credentialGroupId", credentialGroupId);
    }
}
