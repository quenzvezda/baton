using System.Diagnostics;

namespace Baton.Persistence;

/// <summary>
/// Activities the server processes start themselves (OBS-06, add-operability-and-insight design.md decision 27),
/// exported with the rest of the baseline (<c>AddSource("Baton.*")</c>). A run's trace starts where the run is
/// created: the API request for Run now and webhooks, <see cref="TriggerFire"/> for cron.
/// </summary>
public static class BatonActivities
{
    public const string OrchestratorSourceName = "Baton.Orchestrator";

    /// <summary>Around each cron firing; the root of the trace of the runs it creates.</summary>
    public const string TriggerFire = "baton.trigger.fire";

    public static readonly ActivitySource Orchestrator = new(OrchestratorSourceName);

    /// <summary>The W3C <c>traceparent</c> of the current activity, stored on the run it creates.</summary>
    public static string? CurrentTraceParent() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current.Id : null;
}
