namespace Baton.Domain.Retries;

/// <summary>Per-task retry policy. Defaults from design spec §6: 3 attempts, 30 s, x2, max 10 min.</summary>
public sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialDelay, double Multiplier, TimeSpan MaxDelay)
{
    /// <summary>Platform bound proposed in design spec §5.</summary>
    public const int MaxAllowedAttempts = 10;

    public static RetryPolicy Default { get; } =
        new(3, TimeSpan.FromSeconds(30), 2.0, TimeSpan.FromMinutes(10));
}
