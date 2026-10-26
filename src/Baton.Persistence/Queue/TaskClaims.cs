using Baton.Persistence.Definitions;
using Baton.Persistence.Hosts;
using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>One task leased to a host: everything the agent needs to run the attempt.</summary>
/// <param name="UpstreamOutputs">Outputs of the task's succeeded ancestors by task key, as JSON (design.md decision 6).</param>
public sealed record ClaimedTask(
    Guid TaskRunId,
    Guid RunId,
    string TaskKey,
    string DefinitionHash,
    int Attempt,
    long LeaseToken,
    TimeSpan Timeout,
    string ParametersJson,
    IReadOnlyDictionary<string, string> UpstreamOutputs);

/// <summary>The outcome of one claim: the host's group (to wait on) and the leases, possibly none.</summary>
public sealed record ClaimResult(Guid HostGroupId, IReadOnlyList<ClaimedTask> Tasks);

/// <summary>
/// The claim transaction (ORC-04, ORC-05, ORC-07, HST-01; design spec §7 "Claim"; design.md decisions 3
/// and 4): the host must be Online; the budget is the smallest of the agent's free slots, the host's
/// capacity minus the leases it holds, and the room in <c>group-tasks</c>; the best eligible tasks are
/// taken with <c>SKIP LOCKED</c> by effective priority, then queue time, each under a new fencing token.
/// </summary>
public sealed class TaskClaims(NpgsqlDataSource dataSource, DefinitionGraphCache graphs)
{
    // The host row is locked so two claims of one host cannot both spend its free capacity.
    private static readonly string HostRowSql = $"""
        SELECT h.host_group_id, h.labels, h.definition_hashes, h.capacity_slots, {HostSql.IsEligible("h")},
               g.lease_seconds,
               (SELECT count(*)::int FROM task_runs t WHERE t.lease_host_id = h.id AND t.status IN ('leased','running'))
        FROM hosts h JOIN host_groups g ON g.id = h.host_group_id
        WHERE h.id = @hostId AND {HostSql.MayActFor("h")}
        FOR NO KEY UPDATE OF h
        """;

    private const string RoomSql = "SELECT max - in_use FROM concurrency_limits WHERE key = @key FOR UPDATE";

    private const string LeaseSql = """
        WITH picked AS (
          SELECT id FROM task_runs
          WHERE status = 'queued'
            AND host_group_id = @groupId
            AND required_labels <@ @labels
            AND definition_hash = ANY (@hashes)
            AND (pinned_host_id IS NULL OR pinned_host_id = @hostId)
          ORDER BY effective_priority DESC, queued_at
          LIMIT @budget
          FOR UPDATE SKIP LOCKED),
        leased AS (
          UPDATE task_runs t SET
            status           = 'leased',
            attempt          = t.attempt + 1,
            lease_host_id    = @hostId,
            lease_token      = nextval('lease_token_seq'),
            lease_expires_at = now() + make_interval(secs => @leaseSeconds),
            leased_at        = now(),
            started_at       = NULL
          FROM picked
          WHERE t.id = picked.id
          RETURNING t.id, t.workflow_run_id, t.task_key, t.definition_hash, t.attempt, t.lease_token, t.timeout, t.leased_at),
        attempts AS (
          INSERT INTO task_attempts (task_run_id, attempt, host_id, lease_token, started_at)
          SELECT id, attempt, @hostId, lease_token, leased_at FROM leased),
        events AS (
          INSERT INTO run_events (workflow_run_id, task_key, type, data)
          SELECT workflow_run_id, task_key, 'task.leased',
                 jsonb_build_object('attempt', attempt, 'hostId', @hostId, 'leaseToken', lease_token)
          FROM leased)
        SELECT id, workflow_run_id, task_key, definition_hash, attempt, lease_token, timeout FROM leased
        """;

    private const string TakeRoomSql = "UPDATE concurrency_limits SET in_use = in_use + @count WHERE key = @key";

    private const string ParametersSql = "SELECT id, parameters::text FROM workflow_runs WHERE id = ANY (@runIds)";

    private const string OutputsSql = """
        SELECT workflow_run_id, task_key, output::text FROM task_runs
        WHERE (workflow_run_id, task_key) IN (SELECT * FROM unnest(@runIds::uuid[], @keys::text[]))
          AND status = 'succeeded' AND output IS NOT NULL
        """;

    /// <param name="credentialGroupId">Group of the calling agent's credential (ADR-0013).</param>
    /// <returns><c>null</c> when the credential may act for no such host.</returns>
    public async Task<ClaimResult?> ClaimAsync(Guid credentialGroupId, Guid hostId, int freeSlots, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        Guid groupId;
        string[] labels, hashes;
        int budget, leaseSeconds;
        await using (var host = new NpgsqlCommand(HostRowSql, connection, transaction))
        {
            host.Parameters.AddWithValue("hostId", hostId);
            host.Parameters.AddWithValue("credentialGroupId", credentialGroupId);
            await using var reader = await host.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            groupId = reader.GetGuid(0);
            labels = reader.GetFieldValue<string[]>(1);
            hashes = reader.GetFieldValue<string[]>(2);
            var eligible = reader.GetBoolean(4);
            leaseSeconds = reader.GetInt32(5);
            budget = eligible ? Math.Min(freeSlots, reader.GetInt32(3) - reader.GetInt32(6)) : 0;
        }

        var empty = new ClaimResult(groupId, []);
        if (budget <= 0)
            return empty;

        var limitKey = DbNames.GroupTasksLimit(groupId);
        await using (var room = new NpgsqlCommand(RoomSql, connection, transaction))
        {
            room.Parameters.AddWithValue("key", limitKey);
            if (await room.ExecuteScalarAsync(ct) is int left)
                budget = Math.Min(budget, left);
        }

        if (budget <= 0)
            return empty;

        var leased = new List<(Guid Id, Guid RunId, string Key, string Hash, int Attempt, long Token, TimeSpan Timeout)>();
        await using (var lease = new NpgsqlCommand(LeaseSql, connection, transaction))
        {
            lease.Parameters.AddWithValue("groupId", groupId);
            lease.Parameters.AddWithValue("hostId", hostId);
            lease.Parameters.AddWithValue("labels", labels);
            lease.Parameters.AddWithValue("hashes", hashes);
            lease.Parameters.AddWithValue("budget", budget);
            lease.Parameters.AddWithValue("leaseSeconds", (double)leaseSeconds);
            await using var reader = await lease.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                leased.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                    reader.GetInt64(5), reader.GetFieldValue<TimeSpan>(6)));
            }
        }

        if (leased.Count == 0)
            return empty;

        await using (var take = new NpgsqlCommand(TakeRoomSql, connection, transaction))
        {
            take.Parameters.AddWithValue("key", limitKey);
            take.Parameters.AddWithValue("count", leased.Count);
            await take.ExecuteNonQueryAsync(ct);
        }

        var parameters = new Dictionary<Guid, string>();
        await using (var cmd = new NpgsqlCommand(ParametersSql, connection, transaction))
        {
            cmd.Parameters.AddWithValue("runIds", leased.Select(l => l.RunId).Distinct().ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                parameters[reader.GetGuid(0)] = reader.GetString(1);
        }

        var outputs = await ReadAncestorOutputsAsync(connection, transaction, leased.Select(l => (l.RunId, l.Key, l.Hash)), ct);
        await transaction.CommitAsync(ct);

        return new ClaimResult(groupId, [.. leased.Select(l => new ClaimedTask(
            l.Id, l.RunId, l.Key, l.Hash, l.Attempt, l.Token, l.Timeout, parameters[l.RunId],
            outputs.GetValueOrDefault((l.RunId, l.Key)) ?? new Dictionary<string, string>()))]);
    }

    private async Task<Dictionary<(Guid RunId, string Key), Dictionary<string, string>>> ReadAncestorOutputsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<(Guid RunId, string Key, string Hash)> tasks,
        CancellationToken ct)
    {
        // Which ancestors each leased task may read, and who needs each ancestor's output.
        var readers = new Dictionary<(Guid RunId, string Ancestor), List<string>>();
        foreach (var (runId, key, hash) in tasks)
        {
            var graph = await graphs.GetAsync(hash, ct);
            foreach (var ancestor in graph.Ancestors(key))
            {
                if (!readers.TryGetValue((runId, ancestor), out var list))
                    readers[(runId, ancestor)] = list = [];
                list.Add(key);
            }
        }

        var outputs = new Dictionary<(Guid RunId, string Key), Dictionary<string, string>>();
        if (readers.Count == 0)
            return outputs;

        await using var cmd = new NpgsqlCommand(OutputsSql, connection, transaction);
        cmd.Parameters.AddWithValue("runIds", readers.Keys.Select(k => k.RunId).ToArray());
        cmd.Parameters.AddWithValue("keys", readers.Keys.Select(k => k.Ancestor).ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var (runId, ancestor, json) = (reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
            foreach (var key in readers[(runId, ancestor)])
            {
                if (!outputs.TryGetValue((runId, key), out var map))
                    outputs[(runId, key)] = map = new Dictionary<string, string>(StringComparer.Ordinal);
                map[ancestor] = json;
            }
        }

        return outputs;
    }
}
