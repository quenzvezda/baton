using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>What one admission pass did.</summary>
/// <param name="GroupsBusy">Groups another replica was admitting, skipped this pass.</param>
public readonly record struct AdmissionPass(int Admitted, int PassedOver, int GroupsBusy);

/// <summary>
/// Admission (ORC-02, ADR-0008, ADR-0009, design.md "Admission"): per host group, up to 100 Pending runs by
/// <c>priority DESC, created_at</c>. Each run gets a transaction of its own that locks the run
/// (<c>SKIP LOCKED</c>), then its limit rows in key order, and either reserves them all and starts the run or
/// passes it over, so a run that does not fit never blocks a later one that does. Because every transaction
/// locks the rows of one run only, in key order, admission cannot deadlock with another replica, a completing
/// task or a cancellation. One replica admits a group at a time: the pass holds
/// <c>pg_try_advisory_xact_lock</c> on a separate connection for the group while it works.
/// </summary>
public sealed class RunAdmission(NpgsqlDataSource dataSource, RunMaterializer materializer)
{
    public const int BatchSize = 100;

    private const string GroupsSql = "SELECT DISTINCT host_group_id FROM workflow_runs WHERE status = 'pending'";

    private const string GroupLockSql = "SELECT pg_try_advisory_xact_lock(@lockId)";

    private const string CandidatesSql = """
        SELECT id FROM workflow_runs
        WHERE host_group_id = @groupId AND status = 'pending'
        ORDER BY priority DESC, created_at, id
        LIMIT @limit
        """;

    private const string LockRunSql = """
        SELECT r.limit_keys, r.priority, r.required_labels, d.graph_hash, r.blocked_on
        FROM workflow_runs r JOIN workflow_definitions d ON d.id = r.definition_id
        WHERE r.id = @id AND r.status = 'pending'
        FOR UPDATE OF r SKIP LOCKED
        """;

    private const string StartSql = """
        UPDATE workflow_runs SET status = 'running', started_at = now(), deadline_at = now() + run_timeout,
                                 held_limits = @held, blocked_on = NULL
        WHERE id = @id
        """;

    private const string BlockedSql = "UPDATE workflow_runs SET blocked_on = @key WHERE id = @id";

    public async Task<AdmissionPass> AdmitAsync(CancellationToken ct)
    {
        var groups = new List<Guid>();
        await using (var cmd = dataSource.CreateCommand(GroupsSql))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                groups.Add(reader.GetGuid(0));
        }

        var pass = new AdmissionPass();
        foreach (var group in groups)
        {
            var (admitted, passedOver, busy) = await AdmitGroupAsync(group, ct);
            pass = new AdmissionPass(pass.Admitted + admitted, pass.PassedOver + passedOver, pass.GroupsBusy + (busy ? 1 : 0));
        }

        return pass;
    }

    /// <summary>One group's pass; <c>busy</c> when another replica holds the group.</summary>
    public async Task<(int Admitted, int PassedOver, bool Busy)> AdmitGroupAsync(Guid groupId, CancellationToken ct)
    {
        await using var lockConnection = await dataSource.OpenConnectionAsync(ct);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(ct);
        await using (var groupLock = new NpgsqlCommand(GroupLockSql, lockConnection, lockTransaction))
        {
            groupLock.Parameters.AddWithValue("lockId", AdvisoryLocks.Admission(groupId));
            if (await groupLock.ExecuteScalarAsync(ct) is not true)
                return (0, 0, true);
        }

        var candidates = new List<Guid>();
        await using (var cmd = dataSource.CreateCommand(CandidatesSql))
        {
            cmd.Parameters.AddWithValue("groupId", groupId);
            cmd.Parameters.AddWithValue("limit", BatchSize);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                candidates.Add(reader.GetGuid(0));
        }

        int admitted = 0, passedOver = 0;
        var groupRunsKey = DbNames.GroupRunsLimit(groupId);
        for (var i = 0; i < candidates.Count; i++)
        {
            var decision = await TryAdmitAsync(candidates[i], groupId, ct);
            if (decision is not { } d)
                continue;
            if (d.Admitted)
            {
                admitted++;
                continue;
            }

            passedOver++;
            if (d.BlockedOn == groupRunsKey)
            {
                // Every later run of the group needs this row too.
                passedOver += candidates.Count - i - 1;
                break;
            }
        }

        await lockTransaction.CommitAsync(ct);
        return (admitted, passedOver, false);
    }

    /// <returns><c>null</c> when the run is no longer Pending or another transaction holds it.</returns>
    private async Task<AdmitDecision?> TryAdmitAsync(Guid runId, Guid groupId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        string[] limitKeys, labels;
        short priority;
        string hash;
        string? blockedOn;
        await using (var lockRun = new NpgsqlCommand(LockRunSql, connection, transaction))
        {
            lockRun.Parameters.AddWithValue("id", runId);
            await using var reader = await lockRun.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            limitKeys = reader.GetFieldValue<string[]>(0);
            priority = reader.GetInt16(1);
            labels = reader.GetFieldValue<string[]>(2);
            hash = reader.GetString(3);
            blockedOn = reader.IsDBNull(4) ? null : reader.GetString(4);
        }

        var reservation = await LimitRows.TryReserveAsync(connection, transaction, limitKeys, ct);
        await using var batch = new NpgsqlBatch(connection, transaction);
        if (!reservation.Reserved)
        {
            if (reservation.BlockedOn == blockedOn)
                return new AdmitDecision(blockedOn);

            var blocked = new NpgsqlBatchCommand(BlockedSql);
            blocked.Parameters.AddWithValue("id", runId);
            blocked.Parameters.AddWithValue("key", reservation.BlockedOn!);
            batch.BatchCommands.Add(blocked);
            batch.BatchCommands.Add(RunEvents.Command(runId, null, RunEventTypes.RunBlocked,
                new { limit = reservation.BlockedOn, max = reservation.Max, inUse = reservation.InUse }));
            await batch.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return new AdmitDecision(reservation.BlockedOn);
        }

        var start = new NpgsqlBatchCommand(StartSql);
        start.Parameters.AddWithValue("id", runId);
        start.Parameters.AddWithValue("held", reservation.Held.ToArray());
        batch.BatchCommands.Add(start);
        batch.BatchCommands.Add(RunEvents.Command(runId, null, RunEventTypes.RunAdmitted, new { heldLimits = reservation.Held }));
        batch.BatchCommands.Add(RunEvents.Command(runId, null, RunEventTypes.RunStarted, null));
        await batch.ExecuteNonQueryAsync(ct);

        await materializer.CreateTasksAsync(connection, transaction, runId, hash, groupId, priority, labels, ct);
        await transaction.CommitAsync(ct);
        return new AdmitDecision(null);
    }

    /// <param name="BlockedOn">The full limit that passed the run over; <c>null</c> when it was admitted.</param>
    private readonly record struct AdmitDecision(string? BlockedOn)
    {
        public bool Admitted => BlockedOn is null;
    }
}
