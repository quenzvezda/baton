using Baton.Domain.Hosts;
using Baton.Persistence.Hosts;
using Npgsql;

namespace Baton.Integration.Tests.Infrastructure;

/// <summary>Seeds groups, credentials and hosts straight into the shared database.</summary>
public static class HostData
{
    public static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    public static async Task<(Guid Id, string Name)> CreateGroupAsync(PostgresFixture pg, string prefix = "grp")
    {
        var id = Guid.CreateVersion7();
        var name = NewName(prefix);
        await ExecuteAsync(pg, "INSERT INTO host_groups (id, name) VALUES (@id, @name)", ("id", id), ("name", name));
        return (id, name);
    }

    /// <returns>The credential id and its token.</returns>
    public static async Task<(Guid Id, string Token)> IssueCredentialAsync(PostgresFixture pg, Guid groupId)
    {
        var id = Guid.CreateVersion7();
        var token = AgentToken.Generate();
        await ExecuteAsync(pg,
            "INSERT INTO agent_credentials (id, host_group_id, name, token_hash) VALUES (@id, @group, @name, @hash)",
            ("id", id), ("group", groupId), ("name", NewName("cred")), ("hash", AgentToken.Hash(token)));
        return (id, token);
    }

    public static async Task<Guid> InsertHostAsync(PostgresFixture pg, Guid groupId, string? name = null, int slots = 4)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(pg, """
            INSERT INTO hosts (id, name, host_group_id, capacity_slots, status, agent_version, definition_hashes, last_heartbeat_at)
            VALUES (@id, @name, @group, @slots, 'online', '1.0.0', '{}', now())
            """, ("id", id), ("name", name ?? NewName("host")), ("group", groupId), ("slots", slots));
        return id;
    }

    /// <summary>Moves the host's last heartbeat into the past, as if it had stopped sending them.</summary>
    public static Task AgeHeartbeatAsync(PostgresFixture pg, Guid hostId, TimeSpan age) =>
        ExecuteAsync(pg, "UPDATE hosts SET last_heartbeat_at = now() - @age WHERE id = @id", ("age", age), ("id", hostId));

    public static async Task<T> ScalarAsync<T>(PostgresFixture pg, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Command(pg, sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Query returned no row.");
        return reader.GetFieldValue<T>(0);
    }

    public static async Task<bool> IsEligibleAsync(PostgresFixture pg, Guid hostId) =>
        await ScalarAsync<bool>(pg, $"SELECT {HostSql.IsEligible("h")} FROM hosts h WHERE h.id = @id", ("id", hostId));

    public static async Task ExecuteAsync(PostgresFixture pg, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Command(pg, sql, parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand Command(PostgresFixture pg, string sql, (string Name, object Value)[] parameters)
    {
        var cmd = pg.DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
