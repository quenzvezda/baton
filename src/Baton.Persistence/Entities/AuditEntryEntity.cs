namespace Baton.Persistence.Entities;

/// <summary>
/// Row of <c>audit_log</c> (AUT-11, add-operability-and-insight design.md decision 12): one change to
/// configuration, who made it and the entity before and after, written in the change's own transaction.
/// </summary>
public sealed class AuditEntryEntity
{
    public long Id { get; set; }

    /// <summary>Set by Postgres <c>now()</c> on insert.</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>The OIDC <c>sub</c> of the person who made the change.</summary>
    public required string ActorSubject { get; set; }

    /// <summary>Display name, as <c>/api/me</c> reports it.</summary>
    public required string ActorName { get; set; }

    /// <summary><c>automation</c> | <c>host_group</c> | <c>agent_credential</c> | <c>workflow_limit</c>.</summary>
    public required string EntityType { get; set; }

    /// <summary>The entity's id, or the workflow key for a workflow limit.</summary>
    public required string EntityId { get; set; }

    /// <summary>What happened, e.g. <c>create</c>, <c>update</c>, <c>pause</c>, <c>rotate_secret</c>.</summary>
    public required string Action { get; set; }

    /// <summary>The entity as the API returned it before the change, as JSON; <c>null</c> for a create.</summary>
    public string? Before { get; set; }

    /// <summary>The entity as the API returns it after the change, as JSON; <c>null</c> for a delete.</summary>
    public string? After { get; set; }
}
