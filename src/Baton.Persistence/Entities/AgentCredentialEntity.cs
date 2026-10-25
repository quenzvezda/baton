namespace Baton.Persistence.Entities;

/// <summary>Row of <c>agent_credentials</c>: a bearer token scoped to one host group (ADR-0013).</summary>
public sealed class AgentCredentialEntity
{
    public Guid Id { get; set; }
    public Guid HostGroupId { get; set; }

    /// <summary>Unique within the group, e.g. <c>etl-agents-2026</c>.</summary>
    public required string Name { get; set; }

    /// <summary>SHA-256 of the token; the token itself is never stored.</summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>Set by Postgres <c>now()</c> on insert.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary><c>null</c> while the credential is valid.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
