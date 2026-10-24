namespace Baton.Persistence.Entities;

/// <summary>Row of <c>hosts</c>: one registered agent.</summary>
public sealed class HostEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public Guid HostGroupId { get; set; }
    public string[] Labels { get; set; } = [];
    public int CapacitySlots { get; set; }

    /// <summary><c>online</c> | <c>draining</c> | <c>offline</c>.</summary>
    public required string Status { get; set; }

    public required string AgentVersion { get; set; }

    /// <summary>Graph hashes of the definitions this agent build can run.</summary>
    public string[] DefinitionHashes { get; set; } = [];

    public DateTimeOffset LastHeartbeatAt { get; set; }

    /// <summary>Set by Postgres <c>now()</c> on insert.</summary>
    public DateTimeOffset RegisteredAt { get; set; }
}
