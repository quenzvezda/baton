using Baton.Domain.Automations;

namespace Baton.Domain.Tests.Automations;

/// <summary>AUT-04: cron next-fire computation in IANA time zones (M4 task 2.1).</summary>
public sealed class CronScheduleTests
{
    [Fact]
    public void Two_am_in_Singapore_is_six_pm_utc_the_day_before()
    {
        var schedule = CronSchedule.Create("0 2 * * *", "Asia/Singapore");

        var next = schedule.NextAfter(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 10, 27, 18, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Next_fire_is_strictly_after_the_given_instant()
    {
        var schedule = CronSchedule.Create("*/5 * * * *", "UTC");
        var onTheDot = new DateTimeOffset(2026, 10, 27, 10, 5, 0, TimeSpan.Zero);

        Assert.Equal(onTheDot.AddMinutes(5), schedule.NextAfter(onTheDot));
    }

    [Fact]
    public void A_time_skipped_by_the_spring_forward_fires_right_after_the_jump()
    {
        // New York jumped from 02:00 EST to 03:00 EDT on 2026-03-08; 02:30 never happened that day.
        var schedule = CronSchedule.Create("30 2 * * *", "America/New_York");

        var next = schedule.NextAfter(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), next); // 03:00 EDT
    }

    [Fact]
    public void A_time_repeated_by_the_fall_back_fires_once()
    {
        // New York fell back from 02:00 EDT to 01:00 EST on 2026-11-01; 01:30 happened twice.
        var schedule = CronSchedule.Create("30 1 * * *", "America/New_York");

        var first = schedule.NextAfter(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero));
        var second = schedule.NextAfter(first!.Value);

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), first); // 01:30 EDT
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), second); // the next day, 01:30 EST
    }

    [Fact]
    public void After_downtime_the_next_fire_is_in_the_future()
    {
        var schedule = CronSchedule.Create("0 * * * *", "UTC");
        var now = new DateTimeOffset(2026, 10, 27, 13, 20, 0, TimeSpan.Zero);

        // Missed 11:00, 12:00 and 13:00; v1 fires once and moves on (FireOnce).
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 14, 0, 0, TimeSpan.Zero), schedule.NextAfter(now));
    }

    [Theory]
    [InlineData("0 2 * * * *", "UTC", "cronExpression")] // seconds field: v1 takes 5 fields
    [InlineData("not cron", "UTC", "cronExpression")]
    [InlineData("", "UTC", "cronExpression")]
    [InlineData("0 2 * * *", "Mars/Olympus_Mons", "timeZone")]
    [InlineData("0 2 * * *", "", "timeZone")]
    public void Invalid_expression_or_zone_is_reported_by_field(string expression, string zone, string field)
    {
        Assert.Null(CronSchedule.TryCreate(expression, zone, out var errors));
        Assert.Equal([field], errors.Keys);
    }
}
