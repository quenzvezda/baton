namespace Baton.Persistence.Entities;

/// <summary>Row of <c>workflow_definitions</c>: one registered, immutable <c>key@version</c> (ADR-0004).</summary>
public sealed class WorkflowDefinitionEntity
{
    public long Id { get; set; }
    public required string Key { get; set; }
    public int Version { get; set; }

    /// <summary>SHA-256 (hex) of the canonical graph JSON.</summary>
    public required string GraphHash { get; set; }

    /// <summary>Canonical graph JSON (tasks, edges, options), stored as <c>jsonb</c>.</summary>
    public required string Graph { get; set; }

    /// <summary>JSON Schema of the params type, stored as <c>jsonb</c>.</summary>
    public required string ParamsSchema { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Set by Postgres <c>now()</c> on insert.</summary>
    public DateTimeOffset RegisteredAt { get; set; }

    public bool IsRetired { get; set; }
}
