using Baton.Domain.Retries;

namespace Baton.Domain.Tests.Retries;

public sealed class BackoffTests
{
    private static readonly RetryPolicy Policy = RetryPolicy.Default; // 30 s, x2, max 10 min

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(5, 480)]
    [InlineData(6, 600)]   // capped at max
    [InlineData(10, 600)]
    public void Without_jitter_delay_grows_exponentially_up_to_max(int attempt, double expectedSeconds)
    {
        var delay = Backoff.NextDelay(Policy, attempt, jitter: () => 0.0);

        Assert.Equal(expectedSeconds, delay.TotalSeconds, precision: 6);
    }

    [Fact]
    public void Jitter_adds_at_most_twenty_percent()
    {
        var delay = Backoff.NextDelay(Policy, 1, jitter: () => 0.999999);

        Assert.InRange(delay.TotalSeconds, 30.0, 36.0);
    }

    [Fact]
    public void Attempt_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.NextDelay(Policy, 0));
    }
}
