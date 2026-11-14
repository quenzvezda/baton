namespace Baton.Domain.Automations;

/// <summary>What one due cron trigger does on this pass.</summary>
/// <param name="Fires">Fire times to create runs for, oldest first: the selected missed times, then those on time.</param>
/// <param name="PassedOver">Missed times that get no run.</param>
/// <param name="FirstPassedOver">The oldest missed time that gets no run.</param>
/// <param name="LastPassedOver">The most recent missed time that gets no run.</param>
/// <param name="Next">The first fire time after now, or <c>null</c> when the expression never fires again.</param>
public sealed record MisfirePlan(
    IReadOnlyList<DateTimeOffset> Fires,
    long PassedOver,
    DateTimeOffset? FirstPassedOver,
    DateTimeOffset? LastPassedOver,
    DateTimeOffset? Next);

/// <summary>
/// AUT-08 (design.md decision 6). The fire times due are <c>next_fire_at</c> and every later fire time up to
/// <c>now</c>. A time at most <see cref="MisfirePolicies.Threshold"/> late is on time and always fires. The
/// others are missed: the policy keeps the most recent N of them (FireOnce 1, Skip 0, CatchUp its limit) and
/// passes over the rest. Only N missed times are held at once, so memory stays bounded however long the outage.
/// </summary>
public static class MisfirePlanner
{
    public static MisfirePlan Plan(CronSchedule schedule, DateTimeOffset nextFireAt, DateTimeOffset now,
        MisfirePolicy policy, int? catchUpLimit)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (nextFireAt > now)
            return new MisfirePlan([], 0, null, null, nextFireAt);

        var keep = MisfirePolicies.MissedToFire(policy, catchUpLimit);
        var kept = new Queue<DateTimeOffset>(keep + 1);
        var onTime = new List<DateTimeOffset>();
        long passedOver = 0;
        DateTimeOffset? firstPassedOver = null, lastPassedOver = null;

        foreach (var fireTime in schedule.Between(nextFireAt, now).Prepend(nextFireAt))
        {
            if (now - fireTime <= MisfirePolicies.Threshold)
            {
                onTime.Add(fireTime);
                continue;
            }

            kept.Enqueue(fireTime);
            if (kept.Count <= keep)
                continue;

            var dropped = kept.Dequeue();
            passedOver++;
            firstPassedOver ??= dropped;
            lastPassedOver = dropped;
        }

        return new MisfirePlan([.. kept, .. onTime], passedOver, firstPassedOver, lastPassedOver, schedule.NextAfter(now));
    }
}
