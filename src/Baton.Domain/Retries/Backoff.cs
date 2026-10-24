namespace Baton.Domain.Retries;

/// <summary>
/// delay_n = min(max, initial × multiplier^(n−1)) × (1 + j), j ∈ [0, 0.2]  (ORC-10).
/// </summary>
public static class Backoff
{
    public const double MaxJitter = 0.2;

    /// <param name="attempt">1-based number of the attempt that just failed.</param>
    /// <param name="jitter">Optional source in [0,1); defaults to <see cref="Random.Shared"/>. Injected for tests.</param>
    public static TimeSpan NextDelay(RetryPolicy policy, int attempt, Func<double>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var seconds = Math.Min(
            policy.MaxDelay.TotalSeconds,
            policy.InitialDelay.TotalSeconds * Math.Pow(policy.Multiplier, attempt - 1));

        var j = (jitter ?? Random.Shared.NextDouble)() * MaxJitter;
        return TimeSpan.FromSeconds(seconds * (1 + j));
    }
}
