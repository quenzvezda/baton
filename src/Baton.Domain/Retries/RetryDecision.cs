namespace Baton.Domain.Retries;

/// <summary>What happens to a task after a failed attempt: retry after <see cref="Delay"/>, or fail.</summary>
public readonly record struct RetryDecision(bool Retry, TimeSpan Delay)
{
    public static RetryDecision Fail { get; } = new(false, TimeSpan.Zero);

    public static RetryDecision RetryAfter(TimeSpan delay) => new(true, delay);

    /// <summary>
    /// A non-retryable failure (WF-08) or the last attempt fails the task; otherwise retry with backoff.
    /// </summary>
    /// <param name="attempt">1-based number of the attempt that just failed.</param>
    /// <param name="jitter">Optional source in [0,1) passed to <see cref="Backoff.NextDelay"/>.</param>
    public static RetryDecision AfterFailure(RetryPolicy policy, int attempt, bool nonRetryable, Func<double>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        return nonRetryable || attempt >= policy.MaxAttempts
            ? Fail
            : RetryAfter(Backoff.NextDelay(policy, attempt, jitter));
    }
}
