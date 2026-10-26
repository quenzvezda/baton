using Baton.Domain.Retries;
using Baton.Domain.Runs;

namespace Baton.Domain.Tests.Runs;

/// <summary>ORC-09, ORC-10, WF-05: what a task becomes after an attempt ends.</summary>
public sealed class RetryClassificationTests
{
    private static readonly RetryPolicy Policy = RetryPolicy.Default; // 3 attempts, 30 s, x2, max 10 min

    [Fact]
    public void Success_is_final()
    {
        var resolution = Resolve(AttemptOutcome.Succeeded, attempt: 1);

        Assert.Equal(new AttemptResolution(AttemptOutcome.Succeeded, TaskRunStatus.Succeeded, TimeSpan.Zero), resolution);
    }

    [Fact]
    public void Timeout_is_retried_with_backoff()
    {
        var resolution = Resolve(AttemptOutcome.TimedOut, attempt: 1);

        Assert.Equal(new AttemptResolution(AttemptOutcome.TimedOut, TaskRunStatus.RetryWait, TimeSpan.FromSeconds(30)), resolution);
    }

    [Fact]
    public void Second_failed_attempt_waits_60_to_72_seconds()
    {
        var shortest = AttemptResolution.Resolve(AttemptOutcome.Failed, retryable: true, cancelRequested: false, Policy, 2, () => 0.0);
        var longest = AttemptResolution.Resolve(AttemptOutcome.Failed, retryable: true, cancelRequested: false, Policy, 2, () => 0.999999);

        Assert.Equal(TimeSpan.FromSeconds(60), shortest.RetryDelay);
        Assert.InRange(longest.RetryDelay.TotalSeconds, 60.0, 72.0);
    }

    [Fact]
    public void Non_retryable_failure_fails_at_once()
    {
        var resolution = AttemptResolution.Resolve(AttemptOutcome.Failed, retryable: false, cancelRequested: false, Policy, 1);

        Assert.Equal(TaskRunStatus.Failed, resolution.Status);
    }

    [Fact]
    public void Exhausted_attempts_fail_the_task()
    {
        Assert.Equal(TaskRunStatus.Failed, Resolve(AttemptOutcome.Failed, attempt: 3).Status);
        Assert.Equal(TaskRunStatus.Failed, Resolve(AttemptOutcome.TimedOut, attempt: 3).Status);
    }

    [Fact]
    public void Lost_lease_with_attempts_left_is_retried()
    {
        var resolution = Resolve(AttemptOutcome.Lost, attempt: 1);

        Assert.Equal(AttemptOutcome.Lost, resolution.Outcome);
        Assert.Equal(TaskRunStatus.RetryWait, resolution.Status);
    }

    [Fact]
    public void Lost_lease_on_the_last_attempt_fails_the_task()
    {
        Assert.Equal(TaskRunStatus.Failed, Resolve(AttemptOutcome.Lost, attempt: 3).Status);
    }

    [Fact]
    public void Lost_lease_of_a_cancel_requested_task_cancels_it()
    {
        var resolution = AttemptResolution.Resolve(AttemptOutcome.Lost, retryable: true, cancelRequested: true, Policy, 1);

        Assert.Equal(new AttemptResolution(AttemptOutcome.Lost, TaskRunStatus.Cancelled, TimeSpan.Zero), resolution);
    }

    [Fact]
    public void Requested_cancellation_is_final()
    {
        var resolution = AttemptResolution.Resolve(AttemptOutcome.Cancelled, retryable: false, cancelRequested: true, Policy, 1);

        Assert.Equal(TaskRunStatus.Cancelled, resolution.Status);
    }

    [Fact]
    public void Cancellation_nobody_requested_is_a_retryable_failure()
    {
        var resolution = AttemptResolution.Resolve(AttemptOutcome.Cancelled, retryable: false, cancelRequested: false, Policy, 1, () => 0.0);

        Assert.Equal(new AttemptResolution(AttemptOutcome.Failed, TaskRunStatus.RetryWait, TimeSpan.FromSeconds(30)), resolution);
    }

    private static AttemptResolution Resolve(AttemptOutcome outcome, int attempt) =>
        AttemptResolution.Resolve(outcome, retryable: true, cancelRequested: false, Policy, attempt, () => 0.0);
}
