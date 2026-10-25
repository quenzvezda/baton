using Baton.Domain.Hosts;
using Baton.Persistence.Hosts;
using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>A valid, unrevoked agent credential (ADR-0013).</summary>
public sealed record AgentCredential(Guid Id, Guid HostGroupId);

/// <summary>What an agent reports about itself when it registers (HST-02).</summary>
public sealed record HostRegistration(
    string HostName,
    string HostGroup,
    IReadOnlyList<string> Labels,
    int CapacitySlots,
    string AgentVersion,
    IReadOnlyList<string> DefinitionHashes);

public enum RegistrationOutcome
{
    Registered,

    /// <summary>No group has the requested name.</summary>
    UnknownGroup,

    /// <summary>The credential belongs to another group than the requested one.</summary>
    WrongGroup,

    /// <summary>A host of that name exists and the credential may not act for it.</summary>
    HostNotPermitted,
}

public readonly record struct RegistrationResult(RegistrationOutcome Outcome, Guid HostId, bool Draining, int LeaseSeconds);

/// <summary>
/// Agent-facing SQL on the hot path (ADR-0007): credential lookup, registration and heartbeat. Every
/// timestamp is Postgres <c>now()</c>; agent clocks are never used.
/// </summary>
public sealed class AgentRegistry(NpgsqlDataSource dataSource)
{
    private const string FindCredentialSql = """
        SELECT id, host_group_id FROM agent_credentials
        WHERE token_hash = @tokenHash AND revoked_at IS NULL
        """;

    private const string FindGroupSql = "SELECT id FROM host_groups WHERE name = @group";

    // Create-or-update by name in one statement, so concurrent registrations of a name end in one row.
    // Re-registration refreshes what the agent reports but never the group: an Admin move is durable.
    private static readonly string RegisterSql = $"""
        INSERT INTO hosts AS h (id, name, host_group_id, labels, capacity_slots, status, agent_version,
                                definition_hashes, last_heartbeat_at, credential_id)
        VALUES (@id, @name, @credentialGroupId, @labels, @slots, '{HostStatus.Online}', @version,
                @hashes, now(), @credentialId)
        ON CONFLICT (name) DO UPDATE SET
          labels            = EXCLUDED.labels,
          capacity_slots    = EXCLUDED.capacity_slots,
          agent_version     = EXCLUDED.agent_version,
          definition_hashes = EXCLUDED.definition_hashes,
          last_heartbeat_at = now(),
          credential_id     = EXCLUDED.credential_id,
          status            = CASE WHEN h.drain_requested THEN '{HostStatus.Draining}' ELSE '{HostStatus.Online}' END
        WHERE {HostSql.MayActFor("h")}
        RETURNING h.id, h.drain_requested,
                  (SELECT g.lease_seconds FROM host_groups g WHERE g.id = h.host_group_id)
        """;

    private static readonly string HeartbeatSql = $"""
        UPDATE hosts h SET
          last_heartbeat_at = now(),
          status = CASE WHEN h.drain_requested OR @shuttingDown THEN '{HostStatus.Draining}' ELSE '{HostStatus.Online}' END
        WHERE h.id = @hostId AND {HostSql.MayActFor("h")}
        RETURNING h.drain_requested
        """;

    /// <returns><c>null</c> for an unknown or revoked token.</returns>
    public async Task<AgentCredential?> FindCredentialAsync(string token, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(FindCredentialSql);
        cmd.Parameters.AddWithValue("tokenHash", AgentToken.Hash(token));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new AgentCredential(reader.GetGuid(0), reader.GetGuid(1)) : null;
    }

    public async Task<RegistrationResult> RegisterAsync(AgentCredential credential, HostRegistration registration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(registration);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        Guid groupId;
        await using (var find = new NpgsqlCommand(FindGroupSql, connection, transaction))
        {
            find.Parameters.AddWithValue("group", registration.HostGroup);
            if (await find.ExecuteScalarAsync(ct) is not Guid found)
                return new RegistrationResult(RegistrationOutcome.UnknownGroup, Guid.Empty, false, 0);
            groupId = found;
        }

        if (groupId != credential.HostGroupId)
            return new RegistrationResult(RegistrationOutcome.WrongGroup, Guid.Empty, false, 0);

        await using var cmd = new NpgsqlCommand(RegisterSql, connection, transaction);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("name", registration.HostName);
        cmd.Parameters.AddWithValue("credentialGroupId", credential.HostGroupId);
        cmd.Parameters.AddWithValue("credentialId", credential.Id);
        cmd.Parameters.AddWithValue("labels", registration.Labels.ToArray());
        cmd.Parameters.AddWithValue("slots", registration.CapacitySlots);
        cmd.Parameters.AddWithValue("version", registration.AgentVersion);
        cmd.Parameters.AddWithValue("hashes", registration.DefinitionHashes.ToArray());

        RegistrationResult result;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            // No row back: the name belongs to a host this credential may not act for.
            result = await reader.ReadAsync(ct)
                ? new RegistrationResult(RegistrationOutcome.Registered, reader.GetGuid(0), reader.GetBoolean(1), reader.GetInt32(2))
                : new RegistrationResult(RegistrationOutcome.HostNotPermitted, Guid.Empty, false, 0);
        }

        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>Records a heartbeat at <c>now()</c> (HST-03).</summary>
    /// <param name="shuttingDown">The agent is draining itself before it stops.</param>
    /// <returns>Whether an operator drained the host, or <c>null</c> when the credential may act for no such host.</returns>
    public async Task<bool?> HeartbeatAsync(AgentCredential credential, Guid hostId, bool shuttingDown, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);

        await using var cmd = dataSource.CreateCommand(HeartbeatSql);
        cmd.Parameters.AddWithValue("hostId", hostId);
        cmd.Parameters.AddWithValue("credentialGroupId", credential.HostGroupId);
        cmd.Parameters.AddWithValue("shuttingDown", shuttingDown);
        return await cmd.ExecuteScalarAsync(ct) is bool drained ? drained : null;
    }
}
