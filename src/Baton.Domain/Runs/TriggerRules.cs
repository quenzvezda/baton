using Baton.Abstractions;

namespace Baton.Domain.Runs;

public enum TriggerDecision
{
    Run,
    Skip,
}

/// <summary>
/// WF-06: what happens to a task once every upstream task is final. Failed, Skipped and Cancelled
/// upstream tasks all count as "not succeeded", so a Skipped task skips its AllSuccess children too.
/// </summary>
public static class TriggerRules
{
    /// <summary>Decides from the <c>task_runs</c> counters (<c>upstream_unsuccessful</c> = failed + skipped + cancelled).</summary>
    public static TriggerDecision Decide(TriggerRule rule, int upstreamFailed, int upstreamUnsuccessful)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(upstreamFailed);
        ArgumentOutOfRangeException.ThrowIfLessThan(upstreamUnsuccessful, upstreamFailed);

        return rule switch
        {
            TriggerRule.AllSuccess => upstreamUnsuccessful == 0 ? TriggerDecision.Run : TriggerDecision.Skip,
            TriggerRule.AllDone => TriggerDecision.Run,
            TriggerRule.OneFailed => upstreamFailed > 0 ? TriggerDecision.Run : TriggerDecision.Skip,
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown trigger rule."),
        };
    }

    /// <summary>Decides from the final statuses of the upstream tasks.</summary>
    public static TriggerDecision Decide(TriggerRule rule, IEnumerable<TaskRunStatus> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        var statuses = upstream.ToList();
        if (statuses.Any(s => !s.IsFinal()))
            throw new ArgumentException("Every upstream task must be final before the trigger rule is decided.", nameof(upstream));

        return Decide(
            rule,
            upstreamFailed: statuses.Count(s => s == TaskRunStatus.Failed),
            upstreamUnsuccessful: statuses.Count(s => s.IsUnsuccessful()));
    }
}
