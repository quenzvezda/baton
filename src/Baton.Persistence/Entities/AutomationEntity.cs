namespace Baton.Persistence.Entities;

/// <summary>Row of <c>automations</c>: a configured workflow with parameters, routing and limits.</summary>
public sealed class AutomationEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public required string WorkflowKey { get; set; }

    /// <summary><c>null</c> = latest registered version.</summary>
    public int? WorkflowVersion { get; set; }

    /// <summary>Workflow parameters as JSON, stored as <c>jsonb</c>.</summary>
    public string Parameters { get; set; } = "{}";

    public Guid HostGroupId { get; set; }
    public string[] RequiredLabels { get; set; } = [];

    /// <summary>0..100, higher runs first.</summary>
    public short Priority { get; set; } = 50;

    public int MaxConcurrentRuns { get; set; } = 1;

    /// <summary><c>skip</c> | <c>queue</c> | <c>replace</c> | <c>allow</c>.</summary>
    public string ConcurrencyPolicy { get; set; } = "skip";

    /// <summary><c>per_task</c> | <c>pinned_run</c>.</summary>
    public string PlacementMode { get; set; } = "per_task";

    public TimeSpan? RunTimeout { get; set; }
    public required string Owner { get; set; }

    /// <summary>Set by Postgres <c>now()</c> on insert.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token for the editor.</summary>
    public int RowVersion { get; set; }

    /// <summary>Soft delete (M4 design.md decision 5): runs keep referring to the automation.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
