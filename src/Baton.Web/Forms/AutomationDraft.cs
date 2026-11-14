using System.Globalization;
using System.Text.Json;
using Baton.Web.Api;

namespace Baton.Web.Forms;

/// <summary>A trigger as edited; <see cref="Id"/> keeps an existing trigger on save.</summary>
public sealed class TriggerDraft
{
    public Guid? Id { get; set; }
    public string Kind { get; set; } = "cron";
    public string? CronExpression { get; set; }
    public string? TimeZone { get; set; }
    public bool Enabled { get; set; } = true;
    public string? WebhookUrl { get; set; }

    /// <summary>Cron only (AUT-08): <c>fire_once</c>, <c>skip</c> or <c>catch_up</c>.</summary>
    public string MisfirePolicy { get; set; } = "fire_once";

    /// <summary>Sent only for <c>catch_up</c>.</summary>
    public int? CatchUpLimit { get; set; }

    /// <summary>Switching to CatchUp fills in the default limit of 3 (the editor's "Catch-up limit only for CatchUp").</summary>
    public void MisfirePolicyChanged()
    {
        if (MisfirePolicy == "catch_up")
            CatchUpLimit ??= 3;
    }
}

/// <summary>
/// A notification channel as edited (OBS-05); <see cref="Id"/> keeps an existing channel on save. A webhook's URL is
/// write-only: <see cref="Url"/> starts empty and stays <c>null</c> in the request unless a new one is typed.
/// </summary>
public sealed class NotificationDraft
{
    public const string Email = "email";
    public const string Webhook = "webhook";

    public Guid? Id { get; set; }
    public string Kind { get; set; } = Email;
    public bool OnFailed { get; set; } = true;
    public bool OnTimedOut { get; set; }

    /// <summary>Email only: addresses separated by commas, semicolons or new lines.</summary>
    public string? Recipients { get; set; }

    /// <summary>Webhook only: a new URL; empty keeps the stored one.</summary>
    public string? Url { get; set; }

    /// <summary>An existing webhook channel's stored URL as the API shows it: <c>scheme://host</c> only.</summary>
    public string? CurrentHost { get; set; }

    public static NotificationDraft From(NotificationInfo channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new NotificationDraft
        {
            Id = channel.Id,
            Kind = channel.Kind,
            OnFailed = channel.On.Contains("failed"),
            OnTimedOut = channel.On.Contains("timed_out"),
            Recipients = channel.Recipients is { } to ? string.Join(", ", to) : null,
            CurrentHost = channel.Url,
        };
    }

    private static readonly char[] Separators = [',', ';', '\n', '\r'];

    public NotificationRequest ToRequest() => new(
        Id,
        Kind,
        [.. new[] { OnFailed ? "failed" : null, OnTimedOut ? "timed_out" : null }.OfType<string>()],
        Kind == Email ? [.. (Recipients ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : null,
        Kind == Webhook && !string.IsNullOrWhiteSpace(Url) ? Url.Trim() : null);
}

/// <summary>The automation editor's inputs as text, so a rejected save keeps what was typed (AUT-03).</summary>
public sealed class AutomationDraft
{
    /// <summary>API error keys the editor shows next to an input of its own.</summary>
    public static readonly IReadOnlySet<string> Fields = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "description", "workflowKey", "workflowVersion", "hostGroupId", "priority", "maxConcurrentRuns",
        "concurrencyPolicy", "runTimeout", "requiredLabels", "triggers", "placementMode", "notifications",
    };

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string WorkflowKey { get; set; } = "";
    public int? WorkflowVersion { get; set; }
    public string HostGroupId { get; set; } = "";
    public int Priority { get; set; } = 50;
    public int MaxConcurrentRuns { get; set; } = 1;
    public string ConcurrencyPolicy { get; set; } = "skip";
    public string? RunTimeout { get; set; }
    public string? RequiredLabels { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>ORC-12: <c>per_task</c> or <c>pinned_run</c>.</summary>
    public string PlacementMode { get; set; } = "per_task";

    public List<TriggerDraft> Triggers { get; } = [];

    /// <summary>OBS-05; always sent, since channels left out of a save are removed.</summary>
    public List<NotificationDraft> Notifications { get; } = [];

    public void Load(Automation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        Name = automation.Name;
        Description = automation.Description;
        WorkflowKey = automation.WorkflowKey;
        WorkflowVersion = automation.WorkflowVersion;
        HostGroupId = automation.HostGroupId.ToString();
        Priority = automation.Priority;
        MaxConcurrentRuns = automation.MaxConcurrentRuns;
        ConcurrencyPolicy = automation.ConcurrencyPolicy;
        RunTimeout = automation.RunTimeout?.ToString("c", CultureInfo.InvariantCulture);
        RequiredLabels = string.Join(", ", automation.RequiredLabels);
        Enabled = automation.Enabled;
        PlacementMode = automation.PlacementMode;
        Triggers.Clear();
        Triggers.AddRange(automation.Triggers.Select(t => new TriggerDraft
        {
            Id = t.Id,
            Kind = t.Kind,
            CronExpression = t.CronExpression,
            TimeZone = t.TimeZone,
            Enabled = t.Enabled,
            WebhookUrl = t.WebhookUrl,
            MisfirePolicy = t.MisfirePolicy ?? "fire_once",
            CatchUpLimit = t.CatchUpLimit,
        }));
        Notifications.Clear();
        Notifications.AddRange((automation.Notifications ?? []).Select(NotificationDraft.From));
    }

    /// <summary>The request body; fails only on text the API could not even read (the API validates the rest).</summary>
    public bool TryBuild(ParameterForm? form, out AutomationRequest request, out Dictionary<string, string> errors)
    {
        errors = new Dictionary<string, string>(StringComparer.Ordinal);
        TimeSpan? timeout = null;
        if (!string.IsNullOrWhiteSpace(RunTimeout))
        {
            if (TimeSpan.TryParse(RunTimeout.Trim(), CultureInfo.InvariantCulture, out var parsed))
                timeout = parsed;
            else
                errors["runTimeout"] = "Use hh:mm:ss, or d.hh:mm:ss for days.";
        }

        Guid? groupId = Guid.TryParse(HostGroupId, out var group) ? group : null;
        JsonElement? parameters = null;
        if (form is not null && form.TryBuild(out var built, out _))
            parameters = JsonSerializer.SerializeToElement(built);

        request = new AutomationRequest(
            Name.Trim(),
            string.IsNullOrEmpty(WorkflowKey) ? null : WorkflowKey,
            groupId,
            [.. Triggers.Select(t => new TriggerRequest(t.Id, t.Kind,
                t.Kind == "cron" ? t.CronExpression?.Trim() : null,
                t.Kind == "cron" ? t.TimeZone?.Trim() : null,
                t.Enabled,
                t.Kind == "cron" ? t.MisfirePolicy : null,
                t.Kind == "cron" && t.MisfirePolicy == "catch_up" ? t.CatchUpLimit : null))],
            string.IsNullOrWhiteSpace(Description) ? null : Description,
            Enabled,
            WorkflowVersion,
            parameters,
            [.. (RequiredLabels ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            Priority,
            MaxConcurrentRuns,
            ConcurrencyPolicy,
            timeout,
            PlacementMode,
            [.. Notifications.Select(n => n.ToRequest())]);
        return errors.Count == 0;
    }
}
