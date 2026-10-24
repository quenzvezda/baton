using Baton.Abstractions;
using Baton.Domain.Retries;

namespace Baton.Domain.Workflows;

/// <summary>
/// Options a task gets when it declares none (WF-05). They are written into the canonical graph JSON,
/// so changing one changes the hash of every definition relying on it: treat them as frozen.
/// </summary>
public static class TaskDefaults
{
    public const TriggerRule Rule = TriggerRule.AllSuccess;
    public const int PriorityOffset = 0;

    public static RetryPolicy Retry => RetryPolicy.Default;

    public static TimeSpan Timeout { get; } = TimeSpan.FromHours(1);
}

/// <summary>Limits checked when definitions are validated (WF-03). Not part of the hash.</summary>
public static class PlatformBounds
{
    public const int MaxAttempts = RetryPolicy.MaxAllowedAttempts;
    public const int MaxKeyLength = 64;

    /// <summary>The offset is added to the run priority (0–100), so a larger magnitude means nothing.</summary>
    public const int MaxPriorityOffset = 100;

    public static TimeSpan MaxTimeout { get; } = TimeSpan.FromHours(24);
}
