namespace Baton.Domain.Runs;

/// <summary>How a waiting task's priority ages (ORC-06): one point per <see cref="Step"/>, at most <see cref="Cap"/> points.</summary>
public sealed record AgingSettings(TimeSpan Step, int Cap)
{
    public static readonly AgingSettings Default = new(TimeSpan.FromMinutes(5), 20);

    /// <summary>Shorter steps would age faster than the 30 s aging loop can follow.</summary>
    public static readonly TimeSpan MinStep = TimeSpan.FromSeconds(30);

    /// <summary>Reasons the settings are unusable, empty when they are valid; a cap of 0 disables aging.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Step < MinStep)
            errors.Add($"Aging step must be at least {MinStep.TotalSeconds:0} s, was {Step}.");
        if (Cap is < 0 or > TaskPriority.Max)
            errors.Add($"Aging cap must be between 0 and {TaskPriority.Max}, was {Cap}.");
        return errors;
    }
}

/// <summary>
/// ORC-05: a task's base priority is its run's priority plus the task's offset, clamped to 0–100. ORC-06: while
/// it is Queued, its effective priority is the base plus one point per aging step waited, capped, and never
/// above 100. <c>AgingLoop</c>'s SQL computes the same formula with Postgres <c>now()</c>.
/// </summary>
public static class TaskPriority
{
    public const int Min = 0;
    public const int Max = 100;

    public static int Base(int runPriority, int offset) => Math.Clamp(runPriority + offset, Min, Max);

    public static int Effective(int basePriority, TimeSpan waited, AgingSettings aging)
    {
        ArgumentNullException.ThrowIfNull(aging);
        var steps = waited <= TimeSpan.Zero ? 0 : (long)(waited.Ticks / aging.Step.Ticks);
        return (int)Math.Min(Max, basePriority + Math.Min(aging.Cap, steps));
    }
}
