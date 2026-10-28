namespace Baton.Domain.Automations;

/// <summary>What a trigger firing does while the automation is at its run limit (AUT-07, design spec §6 "Limits").</summary>
public enum ConcurrencyPolicy
{
    Skip,
    Queue,
    Replace,
    Allow,
}

/// <summary>What the firing does.</summary>
public enum FireAction
{
    /// <summary>Create a Pending run.</summary>
    Create,

    /// <summary>Create no run; record the skipped fire.</summary>
    Skip,

    /// <summary>Cancel the oldest active run that is not already cancelling, then create a run.</summary>
    ReplaceOldest,
}

/// <param name="HoldsAutomationLimit">Whether admission reserves the <c>automation:{id}</c> limit for the new run.</param>
public readonly record struct FireDecision(FireAction Action, bool HoldsAutomationLimit);

public static class ConcurrencyPolicies
{
    /// <summary>
    /// AUT-07 in the trigger transaction (design.md "Concurrency policy", decision 9). Below the limit every
    /// policy but Allow creates a run that holds the automation limit. At the limit, Skip skips, Queue
    /// creates a run that waits, Replace cancels the oldest. Allow never holds the automation limit, so its
    /// runs are bounded only by the workflow and group limits.
    /// </summary>
    /// <param name="activeRuns">Pending, Running and Cancelling runs of the automation.</param>
    public static FireDecision Decide(ConcurrencyPolicy policy, int maxConcurrentRuns, int activeRuns)
    {
        if (policy == ConcurrencyPolicy.Allow)
            return new(FireAction.Create, HoldsAutomationLimit: false);

        if (activeRuns < maxConcurrentRuns)
            return new(FireAction.Create, HoldsAutomationLimit: true);

        return policy switch
        {
            ConcurrencyPolicy.Skip => new(FireAction.Skip, HoldsAutomationLimit: false),
            ConcurrencyPolicy.Queue => new(FireAction.Create, HoldsAutomationLimit: true),
            ConcurrencyPolicy.Replace => new(FireAction.ReplaceOldest, HoldsAutomationLimit: true),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
        };
    }

    /// <summary><c>automations.concurrency_policy</c>.</summary>
    public static string ToDb(ConcurrencyPolicy policy) => policy switch
    {
        ConcurrencyPolicy.Skip => "skip",
        ConcurrencyPolicy.Queue => "queue",
        ConcurrencyPolicy.Replace => "replace",
        ConcurrencyPolicy.Allow => "allow",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null),
    };

    public static bool TryParse(string? value, out ConcurrencyPolicy policy)
    {
        (var ok, policy) = value switch
        {
            "skip" => (true, ConcurrencyPolicy.Skip),
            "queue" => (true, ConcurrencyPolicy.Queue),
            "replace" => (true, ConcurrencyPolicy.Replace),
            "allow" => (true, ConcurrencyPolicy.Allow),
            _ => (false, ConcurrencyPolicy.Skip),
        };
        return ok;
    }
}
