using Baton.Domain.Notifications;

namespace Baton.Domain.Tests.Notifications;

/// <summary>OBS-05: delivery retry schedule (design.md decision 19).</summary>
public sealed class NotificationScheduleTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 30)]
    public void Delays_are_1_5_15_30_minutes_with_jitter(int attempt, int minutes)
    {
        var expected = TimeSpan.FromMinutes(minutes);

        Assert.Equal(expected, NotificationSchedule.Delay(attempt, () => 0.5));
        Assert.Equal(expected * 0.9, NotificationSchedule.Delay(attempt, () => 0.0));
        Assert.True(NotificationSchedule.Delay(attempt, () => 0.999999) <= expected * 1.1);
    }

    [Fact]
    public void Fifth_failure_is_final()
    {
        Assert.False(NotificationSchedule.IsFinal(4));
        Assert.True(NotificationSchedule.IsFinal(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => NotificationSchedule.Delay(5));
    }
}
