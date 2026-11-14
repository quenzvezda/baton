using Baton.Domain.Automations;

namespace Baton.Domain.Tests.Automations;

/// <summary>AUT-08: which missed cron fire times get a run after downtime (M6 task 1.2, design.md decision 6).</summary>
public sealed class MisfirePlannerTests
{
    private static readonly CronSchedule Hourly = CronSchedule.Create("0 * * * *", "UTC");
    private static readonly CronSchedule DailyAt2 = CronSchedule.Create("0 2 * * *", "UTC");

    private static DateTimeOffset At(int hour, int minute = 0, int second = 0, int day = 14) =>
        new(2026, 11, day, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void FireOnce_fires_the_most_recent_missed_time()
    {
        // Spec "FireOnce after an outage": 01:00, 02:00 and 03:00 missed, back at 03:30.
        var plan = MisfirePlanner.Plan(Hourly, At(1), At(3, 30), MisfirePolicy.FireOnce, null);

        Assert.Equal([At(3)], plan.Fires);
        Assert.Equal(2, plan.PassedOver);
        Assert.Equal(At(1), plan.FirstPassedOver);
        Assert.Equal(At(2), plan.LastPassedOver);
        Assert.Equal(At(4), plan.Next);
    }

    [Fact]
    public void Skip_fires_no_missed_time()
    {
        // Spec "Skip after an outage": a daily 02:00 trigger back at 02:05.
        var plan = MisfirePlanner.Plan(DailyAt2, At(2), At(2, 5), MisfirePolicy.Skip, null);

        Assert.Empty(plan.Fires);
        Assert.Equal(1, plan.PassedOver);
        Assert.Equal(At(2), plan.FirstPassedOver);
        Assert.Equal(At(2), plan.LastPassedOver);
        Assert.Equal(At(2, day: 15), plan.Next);
    }

    [Fact]
    public void CatchUp_fires_the_most_recent_N_in_time_order()
    {
        // Spec "CatchUp with a limit": 5 missed (01:00..05:00), limit 3.
        var plan = MisfirePlanner.Plan(Hourly, At(1), At(5, 30), MisfirePolicy.CatchUp, 3);

        Assert.Equal([At(3), At(4), At(5)], plan.Fires);
        Assert.Equal(2, plan.PassedOver);
        Assert.Equal(At(1), plan.FirstPassedOver);
        Assert.Equal(At(2), plan.LastPassedOver);
    }

    [Fact]
    public void CatchUp_with_fewer_missed_times_than_its_limit_fires_them_all()
    {
        var plan = MisfirePlanner.Plan(Hourly, At(1), At(2, 30), MisfirePolicy.CatchUp, 3);

        Assert.Equal([At(1), At(2)], plan.Fires);
        Assert.Equal(0, plan.PassedOver);
        Assert.Null(plan.FirstPassedOver);
    }

    [Theory]
    [InlineData(MisfirePolicy.FireOnce)]
    [InlineData(MisfirePolicy.Skip)]
    [InlineData(MisfirePolicy.CatchUp)]
    public void Fire_up_to_60_s_late_is_on_time(MisfirePolicy policy)
    {
        // Spec "Slightly late fire is not a misfire": due 02:00:00, reached at 02:00:20; 60 s exactly is still on time.
        Assert.Equal([At(2)], MisfirePlanner.Plan(DailyAt2, At(2), At(2, 0, 20), policy, 3).Fires);
        Assert.Equal([At(2)], MisfirePlanner.Plan(DailyAt2, At(2), At(2, 1, 0), policy, 3).Fires);
        Assert.Equal(0, MisfirePlanner.Plan(DailyAt2, At(2), At(2, 1, 0), policy, 3).PassedOver);
    }

    [Fact]
    public void On_time_fire_after_missed_ones_always_fires()
    {
        // Back at 03:00:30: 01:00 and 02:00 are missed, 03:00 is 30 s late and on time.
        var plan = MisfirePlanner.Plan(Hourly, At(1), At(3, 0, 30), MisfirePolicy.Skip, null);

        Assert.Equal([At(3)], plan.Fires);
        Assert.Equal(2, plan.PassedOver);
    }

    [Fact]
    public void Long_outage_is_counted_without_unbounded_memory()
    {
        // A per-minute trigger down for 30 days: 43,199 missed times, of which only the last 3 are kept, plus the
        // one 30 s ago, which is on time.
        var everyMinute = CronSchedule.Create("* * * * *", "UTC");
        var start = At(0, day: 1);
        var now = start.AddDays(30).AddMinutes(-0.5);

        var plan = MisfirePlanner.Plan(everyMinute, start, now, MisfirePolicy.CatchUp, 3);

        Assert.Equal([now.AddMinutes(-3.5), now.AddMinutes(-2.5), now.AddMinutes(-1.5), now.AddMinutes(-0.5)], plan.Fires);
        Assert.Equal(43_199 - 3, plan.PassedOver);
        Assert.Equal(start, plan.FirstPassedOver);
    }

    [Fact]
    public void Trigger_not_yet_due_plans_nothing()
    {
        var plan = MisfirePlanner.Plan(Hourly, At(4), At(3, 59), MisfirePolicy.FireOnce, null);

        Assert.Empty(plan.Fires);
        Assert.Equal(At(4), plan.Next);
    }

    [Theory]
    [InlineData("fire_once", MisfirePolicy.FireOnce)]
    [InlineData("skip", MisfirePolicy.Skip)]
    [InlineData("catch_up", MisfirePolicy.CatchUp)]
    public void Policy_names_round_trip(string name, MisfirePolicy policy)
    {
        Assert.Equal(policy, MisfirePolicies.Parse(name));
        Assert.Equal(name, MisfirePolicies.ToDb(policy));
    }
}
