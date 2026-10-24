using Baton.Domain.Retries;

namespace Baton.Domain.Tests.Retries;

/// <summary>WF-08: a non-retryable failure fails the task without using its remaining attempts.</summary>
public sealed class RetryDecisionTests
{
    private static readonly RetryPolicy FourAttempts = RetryPolicy.Default with { MaxAttempts = 4 };

    [Fact]
    public void Non_retryable_failure_on_the_first_attempt_fails_the_task()
    {
        var decision = RetryDecision.AfterFailure(FourAttempts, attempt: 1, nonRetryable: true);

        Assert.Equal(RetryDecision.Fail, decision);
    }

    [Fact]
    public void Retryable_failure_with_attempts_left_is_retried_with_backoff()
    {
        var decision = RetryDecision.AfterFailure(FourAttempts, attempt: 1, nonRetryable: false, jitter: () => 0.0);

        Assert.Equal(RetryDecision.RetryAfter(TimeSpan.FromSeconds(30)), decision);
    }

    [Fact]
    public void Failure_on_the_last_attempt_fails_the_task()
    {
        var decision = RetryDecision.AfterFailure(FourAttempts, attempt: 4, nonRetryable: false);

        Assert.False(decision.Retry);
    }
}
