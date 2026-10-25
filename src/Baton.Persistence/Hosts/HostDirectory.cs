using Npgsql;

namespace Baton.Persistence.Hosts;

/// <summary>A task a host currently holds (leased or running).</summary>
public sealed record HostTask(Guid TaskRunId, Guid WorkflowRunId, string TaskKey, int Attempt, string Status);

/// <summary>One row of the host inventory (HST-06), with the status derived from heartbeat age.</summary>
public sealed record HostView(
    Guid Id,
    string Name,
    Guid HostGroupId,
    string HostGroupName,
    string Status,
    bool DrainRequested,
    IReadOnlyList<string> Labels,
    int SlotsTotal,
    int SlotsUsed,
    IReadOnlyList<HostTask> CurrentTasks,
    string AgentVersion,
    IReadOnlyList<string> DefinitionHashes,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset RegisteredAt);

public enum HostRemoval
{
    Removed,
    NotFound,

    /// <summary>The host is Online or Draining; only Offline hosts can be removed.</summary>
    NotOffline,

    /// <summary>A run is pinned to the host.</summary>
    InUse,
}

/// <summary>
/// Inventory, drain, move and removal of hosts (HST-04..06). Plain SQL so the derived status in
/// <see cref="HostSql"/> stays the single definition of Online.
/// </summary>
public sealed class HostDirectory(NpgsqlDataSource dataSource)
{
    private static readonly string ListSql = $"""
        SELECT h.id, h.name, h.host_group_id, g.name, {HostSql.DerivedStatus("h")}, h.drain_requested, h.labels,
               h.capacity_slots, h.agent_version, h.definition_hashes, h.last_heartbeat_at, h.registered_at
        FROM hosts h JOIN host_groups g ON g.id = h.host_group_id
        WHERE @hostId::uuid IS NULL OR h.id = @hostId
        ORDER BY h.name;

        SELECT t.lease_host_id, t.id, t.workflow_run_id, t.task_key, t.attempt, t.status
        FROM task_runs t
        WHERE t.lease_host_id IS NOT NULL AND t.status IN ('leased', 'running')
          AND (@hostId::uuid IS NULL OR t.lease_host_id = @hostId)
        ORDER BY t.lease_host_id, t.task_key;
        """;

    private const string DrainSql = """
        UPDATE hosts SET drain_requested = true,
                         status = CASE WHEN status = 'online' THEN 'draining' ELSE status END
        WHERE id = @hostId
        """;

    private const string UndrainSql = """
        UPDATE hosts SET drain_requested = false,
                         status = CASE WHEN status = 'draining' THEN 'online' ELSE status END
        WHERE id = @hostId
        """;

    private const string MoveSql = "UPDATE hosts SET host_group_id = @groupId WHERE id = @hostId";

    private static readonly string RemoveSql = $"""
        WITH target AS (SELECT id, {HostSql.IsOffline("h")} AS offline FROM hosts h WHERE h.id = @hostId FOR UPDATE),
             removed AS (DELETE FROM hosts WHERE id IN (SELECT id FROM target WHERE offline) RETURNING id)
        SELECT (SELECT offline FROM target), EXISTS (SELECT 1 FROM removed)
        """;

    public async Task<IReadOnlyList<HostView>> ListAsync(CancellationToken ct) => await QueryAsync(null, ct);

    public async Task<HostView?> FindAsync(Guid hostId, CancellationToken ct) =>
        (await QueryAsync(hostId, ct)).SingleOrDefault();

    /// <returns><c>false</c> when no such host exists.</returns>
    public Task<bool> DrainAsync(Guid hostId, CancellationToken ct) => UpdateAsync(DrainSql, hostId, null, ct);

    /// <returns><c>false</c> when no such host exists.</returns>
    public Task<bool> UndrainAsync(Guid hostId, CancellationToken ct) => UpdateAsync(UndrainSql, hostId, null, ct);

    /// <summary>
    /// Moves the host to another group (HST-05). Claims read the group from the host row inside the claim
    /// transaction, so the move takes effect on the next claim and leaves held tasks alone.
    /// </summary>
    /// <returns><c>false</c> when no such host exists.</returns>
    public Task<bool> MoveAsync(Guid hostId, Guid groupId, CancellationToken ct) => UpdateAsync(MoveSql, hostId, groupId, ct);

    public async Task<HostRemoval> RemoveAsync(Guid hostId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(RemoveSql);
        cmd.Parameters.AddWithValue("hostId", hostId);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            if (reader.IsDBNull(0))
                return HostRemoval.NotFound;
            return reader.GetBoolean(1) ? HostRemoval.Removed : HostRemoval.NotOffline;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return HostRemoval.InUse;
        }
    }

    private async Task<bool> UpdateAsync(string sql, Guid hostId, Guid? groupId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("hostId", hostId);
        if (groupId is { } group)
            cmd.Parameters.AddWithValue("groupId", group);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<IReadOnlyList<HostView>> QueryAsync(Guid? hostId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(ListSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid?>("hostId", hostId) { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<(Guid Id, Func<IReadOnlyList<HostTask>, HostView> Create)>();
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var name = reader.GetString(1);
            var groupId = reader.GetGuid(2);
            var groupName = reader.GetString(3);
            var status = reader.GetString(4);
            var drainRequested = reader.GetBoolean(5);
            var labels = reader.GetFieldValue<string[]>(6);
            var slots = reader.GetInt32(7);
            var version = reader.GetString(8);
            var hashes = reader.GetFieldValue<string[]>(9);
            var lastHeartbeat = reader.GetFieldValue<DateTimeOffset>(10);
            var registeredAt = reader.GetFieldValue<DateTimeOffset>(11);
            rows.Add((id, tasks => new HostView(id, name, groupId, groupName, status, drainRequested, labels, slots,
                tasks.Count, tasks, version, hashes, lastHeartbeat, registeredAt)));
        }

        await reader.NextResultAsync(ct);
        var tasksByHost = new Dictionary<Guid, List<HostTask>>();
        while (await reader.ReadAsync(ct))
        {
            var host = reader.GetGuid(0);
            if (!tasksByHost.TryGetValue(host, out var tasks))
                tasksByHost[host] = tasks = [];
            tasks.Add(new HostTask(reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5)));
        }

        return [.. rows.Select(r => r.Create(tasksByHost.TryGetValue(r.Id, out var tasks) ? tasks : []))];
    }
}
