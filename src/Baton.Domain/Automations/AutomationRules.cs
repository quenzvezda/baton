namespace Baton.Domain.Automations;

/// <summary>A trigger as an Operator configures it (AUT-04).</summary>
/// <param name="Kind"><c>cron</c>, <c>webhook</c> or <c>manual</c>.</param>
/// <param name="MisfirePolicy"><c>fire_once</c> (when absent), <c>skip</c> or <c>catch_up</c>; cron only (AUT-08).</param>
/// <param name="CatchUpLimit">1–10 for <c>catch_up</c> (3 when absent); absent for the other policies.</param>
public sealed record TriggerSettings(
    string? Kind, string? CronExpression = null, string? TimeZone = null, string? MisfirePolicy = null, int? CatchUpLimit = null);

/// <summary>An automation as an Operator configures it (AUT-01, AUT-02, AUT-05, AUT-06, AUT-07, AUT-09).</summary>
/// <param name="WorkflowVersion"><c>null</c> follows the latest registered version.</param>
public sealed record AutomationSettings(
    string? Name,
    string? Description,
    string? Owner,
    string? WorkflowKey,
    int? WorkflowVersion,
    Guid? HostGroupId,
    IReadOnlyList<string>? RequiredLabels,
    int Priority,
    int MaxConcurrentRuns,
    string? ConcurrencyPolicy,
    TimeSpan? RunTimeout,
    IReadOnlyList<TriggerSettings>? Triggers);

/// <summary>Validation of automation settings and the limit keys of runs (AUT-01..AUT-09, ADR-0009).</summary>
public static class AutomationRules
{
    public const string CronKind = "cron";
    public const string WebhookKind = "webhook";
    public const string ManualKind = "manual";

    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxOwnerLength = 200;
    public const int MaxLabelLength = 63;
    public const int MaxTriggers = 20;
    public const int DefaultPriority = 50;
    public const int MinPriority = 0;
    public const int MaxPriority = 100;
    public const int DefaultMaxConcurrentRuns = 1;
    public const int MaxMaxConcurrentRuns = 1000;
    public static readonly TimeSpan MaxRunTimeout = TimeSpan.FromDays(7);

    /// <summary>Errors keyed by camelCase field path (<c>triggers[0].timeZone</c>), empty when the settings are valid.</summary>
    public static Dictionary<string, string[]> Validate(AutomationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(settings.Name) || settings.Name.Trim().Length > MaxNameLength)
            errors["name"] = [$"Required, at most {MaxNameLength} characters."];
        if (settings.Description is { Length: > MaxDescriptionLength })
            errors["description"] = [$"At most {MaxDescriptionLength} characters."];
        if (string.IsNullOrWhiteSpace(settings.Owner) || settings.Owner.Length > MaxOwnerLength)
            errors["owner"] = [$"Required, at most {MaxOwnerLength} characters."];
        if (string.IsNullOrWhiteSpace(settings.WorkflowKey))
            errors["workflowKey"] = ["Required."];
        if (settings.WorkflowVersion is < 1)
            errors["workflowVersion"] = ["Must be at least 1, or absent to follow the latest version."];
        if (settings.HostGroupId is null || settings.HostGroupId == Guid.Empty)
            errors["hostGroupId"] = ["Required: the host group every task of the automation runs in."];
        if (settings.RequiredLabels?.Any(l => string.IsNullOrWhiteSpace(l) || l.Length > MaxLabelLength || l.Any(char.IsWhiteSpace)) == true)
            errors["requiredLabels"] = [$"Labels are 1–{MaxLabelLength} characters without spaces."];
        if (settings.Priority is < MinPriority or > MaxPriority)
            errors["priority"] = [$"Must be between {MinPriority} and {MaxPriority}."];
        if (settings.MaxConcurrentRuns is < 1 or > MaxMaxConcurrentRuns)
            errors["maxConcurrentRuns"] = [$"Must be between 1 and {MaxMaxConcurrentRuns}."];
        if (!ConcurrencyPolicies.TryParse(settings.ConcurrencyPolicy, out _))
            errors["concurrencyPolicy"] = ["One of 'skip', 'queue', 'replace', 'allow'."];
        if (settings.RunTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > MaxRunTimeout))
            errors["runTimeout"] = [$"Must be positive and at most {MaxRunTimeout.TotalDays:0} days, or absent for none."];

        ValidateTriggers(settings.Triggers, errors);
        return errors;
    }

    private static void ValidateTriggers(IReadOnlyList<TriggerSettings>? triggers, Dictionary<string, string[]> errors)
    {
        if (triggers is null || triggers.Count == 0)
        {
            errors["triggers"] = ["At least one trigger: cron, webhook or manual."];
            return;
        }

        if (triggers.Count > MaxTriggers)
            errors["triggers"] = [$"At most {MaxTriggers} triggers."];
        else if (triggers.Count(t => t?.Kind == ManualKind) > 1)
            errors["triggers"] = ["At most one manual trigger."];

        for (var i = 0; i < triggers.Count; i++)
        {
            var trigger = triggers[i];
            var path = $"triggers[{i}]";
            switch (trigger?.Kind)
            {
                case CronKind:
                    CronSchedule.TryCreate(trigger.CronExpression, trigger.TimeZone, out var cronErrors);
                    foreach (var (field, message) in cronErrors)
                        errors[$"{path}.{field}"] = [message];
                    ValidateMisfire(trigger, path, errors);
                    break;
                case WebhookKind or ManualKind:
                    if (trigger.CronExpression is not null || trigger.TimeZone is not null)
                        errors[$"{path}.cronExpression"] = ["Only cron triggers have a cron expression and time zone."];
                    if (trigger.MisfirePolicy is not (null or "fire_once") || trigger.CatchUpLimit is not null)
                        errors[$"{path}.misfirePolicy"] = ["Only cron triggers have a misfire policy."];
                    break;
                default:
                    errors[$"{path}.kind"] = ["One of 'cron', 'webhook', 'manual'."];
                    break;
            }
        }
    }

    /// <summary>AUT-08: a known policy; a catch-up limit of 1–10, and only for CatchUp.</summary>
    private static void ValidateMisfire(TriggerSettings trigger, string path, Dictionary<string, string[]> errors)
    {
        if (!MisfirePolicies.TryParse(trigger.MisfirePolicy ?? "fire_once", out var policy))
        {
            errors[$"{path}.misfirePolicy"] = ["One of 'fire_once', 'skip', 'catch_up'."];
            return;
        }

        if (policy != MisfirePolicy.CatchUp && trigger.CatchUpLimit is not null)
            errors[$"{path}.catchUpLimit"] = ["Only a CatchUp trigger has a catch-up limit."];
        else if (trigger.CatchUpLimit is < MisfirePolicies.MinCatchUpLimit or > MisfirePolicies.MaxCatchUpLimit)
            errors[$"{path}.catchUpLimit"] =
                [$"Must be between {MisfirePolicies.MinCatchUpLimit} and {MisfirePolicies.MaxCatchUpLimit}, or absent for {MisfirePolicies.DefaultCatchUpLimit}."];
    }

    /// <summary>The stored catch-up limit: the given or default limit for CatchUp, none otherwise.</summary>
    public static short? CatchUpLimitFor(string? misfirePolicy, int? catchUpLimit) =>
        misfirePolicy == "catch_up" ? (short)(catchUpLimit ?? MisfirePolicies.DefaultCatchUpLimit) : null;

    /// <summary>Key of the automation's run limit row in <c>concurrency_limits</c> (ADR-0009).</summary>
    public static string AutomationLimitKey(Guid automationId) => $"automation:{automationId}";

    /// <summary>Key of a workflow's run limit row, shared by all its versions (design.md decision 4).</summary>
    public static string WorkflowLimitKey(string workflowKey) => $"workflow:{workflowKey}";
}
