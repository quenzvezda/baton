using Baton.Domain.Retries;

namespace Baton.Domain.Runs;

/// <summary>How one attempt ended. Mirrors <c>task_attempts.outcome</c> (NFR-Auditability).</summary>
public enum AttemptOutcome
{
    Succeeded,
    Failed,
    TimedOut,

    /// <summary>The lease expired: the host died, stalled or lost the network (ORC-09).</summary>
    Lost,

    Cancelled,
}

/// <summary>The recorded outcome of an attempt and what its task becomes.</summary>
/// <param name="RetryDelay">How long the task waits in Retry wait; zero otherwise.</param>
public readonly record struct AttemptResolution(AttemptOutcome Outcome, TaskRunStatus Status, TimeSpan RetryDelay)
{
    /// <summary>
    /// ORC-09, ORC-10: success is final. Any other ending of a task whose cancellation was requested
    /// cancels it, never retried. A Cancelled report without a request means the host stopped the handler
    /// on its own: a retryable failure. Timeouts and lost leases are retryable; a failure is retryable
    /// unless the handler said otherwise; a retry waits for <see cref="Backoff"/> while attempts remain.
    /// </summary>
    /// <param name="attempt">1-based number of the attempt that ended.</param>
    /// <param name="retryable">For <see cref="AttemptOutcome.Failed"/>: whether the error may succeed on retry.</param>
    /// <param name="jitter">Optional source in [0,1) for the backoff jitter; injected by tests.</param>
    public static AttemptResolution Resolve(
        AttemptOutcome ended, bool retryable, bool cancelRequested, RetryPolicy policy, int attempt, Func<double>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (ended == AttemptOutcome.Succeeded)
            return new(ended, TaskRunStatus.Succeeded, TimeSpan.Zero);

        if (ended == AttemptOutcome.Cancelled && !cancelRequested)
            (ended, retryable) = (AttemptOutcome.Failed, true);

        if (cancelRequested)
            return new(ended, TaskRunStatus.Cancelled, TimeSpan.Zero);

        var nonRetryable = ended == AttemptOutcome.Failed && !retryable;
        var decision = RetryDecision.AfterFailure(policy, attempt, nonRetryable, jitter);
        return decision.Retry
            ? new(ended, TaskRunStatus.RetryWait, decision.Delay)
            : new(ended, TaskRunStatus.Failed, TimeSpan.Zero);
    }
}
