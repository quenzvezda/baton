namespace Baton.Domain.Runs;

/// <summary>ORC-05: a task's base priority is its run's priority plus the task's offset, clamped to 0–100.</summary>
public static class TaskPriority
{
    public const int Min = 0;
    public const int Max = 100;

    public static int Base(int runPriority, int offset) => Math.Clamp(runPriority + offset, Min, Max);
}
