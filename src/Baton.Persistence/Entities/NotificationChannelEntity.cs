namespace Baton.Persistence.Entities;

/// <summary>
/// Row of <c>notification_channels</c> (OBS-05, add-operability-and-insight design.md decision 16): where an
/// automation's Failed or TimedOut runs are reported. A webhook channel's URL and signing secret are stored
/// encrypted with Data Protection (ADR-0015, ADR-0018).
/// </summary>
public sealed class NotificationChannelEntity
{
    public Guid Id { get; set; }
    public Guid AutomationId { get; set; }

    /// <summary><c>email</c> | <c>webhook</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>Run outcomes that fire the channel: <c>failed</c>, <c>timed_out</c>.</summary>
    public string[] Events { get; set; } = [];

    /// <summary>Email only: 1–20 recipient addresses.</summary>
    public string[]? EmailTo { get; set; }

    /// <summary>Webhook only: the encrypted URL.</summary>
    public byte[]? WebhookUrl { get; set; }

    /// <summary>Webhook only: <c>scheme://host</c> of the URL, safe to show.</summary>
    public string? WebhookUrlDisplay { get; set; }

    /// <summary>Webhook only: the encrypted signing secret.</summary>
    public byte[]? WebhookSecret { get; set; }
}
