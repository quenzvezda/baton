namespace Baton.Persistence.Entities;

/// <summary>Row of <c>automation_triggers</c>: a cron, webhook or manual trigger of one automation.</summary>
public sealed class AutomationTriggerEntity
{
    public Guid Id { get; set; }
    public Guid AutomationId { get; set; }

    /// <summary><c>cron</c> | <c>webhook</c> | <c>manual</c>.</summary>
    public required string Kind { get; set; }

    public string? CronExpr { get; set; }

    /// <summary>IANA time zone, e.g. <c>Asia/Singapore</c>.</summary>
    public string? TimeZone { get; set; }

    /// <summary><c>fire_once</c> | <c>skip</c> | <c>catch_up</c>.</summary>
    public string MisfirePolicy { get; set; } = "fire_once";

    /// <summary>How many of the most recent missed fire times CatchUp runs, 1–10; set only for <c>catch_up</c> (AUT-08).</summary>
    public short? CatchUpLimit { get; set; }

    public DateTimeOffset? NextFireAt { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }
    /// <summary>The webhook secret, encrypted with Data Protection (ADR-0015).</summary>
    public byte[]? WebhookSecret { get; set; }

    public bool Enabled { get; set; } = true;
}
