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
}

/// <summary>The automation editor's inputs as text, so a rejected save keeps what was typed (AUT-03).</summary>
public sealed class AutomationDraft
{
    /// <summary>API error keys the editor shows next to an input of its own.</summary>
    public static readonly IReadOnlySet<string> Fields = new HashSet<string>(StringComparer.Ordinal)
    {
        "name", "description", "workflowKey", "workflowVersion", "hostGroupId", "priority", "maxConcurrentRuns",
        "concurrencyPolicy", "runTimeout", "requiredLabels", "triggers",
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
    public List<TriggerDraft> Triggers { get; } = [];

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
        Triggers.Clear();
        Triggers.AddRange(automation.Triggers.Select(t => new TriggerDraft
        {
            Id = t.Id,
            Kind = t.Kind,
            CronExpression = t.CronExpression,
            TimeZone = t.TimeZone,
            Enabled = t.Enabled,
            WebhookUrl = t.WebhookUrl,
        }));
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
                t.Enabled))],
            string.IsNullOrWhiteSpace(Description) ? null : Description,
            Enabled,
            WorkflowVersion,
            parameters,
            [.. (RequiredLabels ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            Priority,
            MaxConcurrentRuns,
            ConcurrencyPolicy,
            timeout);
        return errors.Count == 0;
    }
}
