namespace Baton.Persistence.Entities;

/// <summary>Row of <c>concurrency_limits</c>: a counter row per limit key (ADR-0009).</summary>
public sealed class ConcurrencyLimitEntity
{
    /// <summary><c>automation:{id}</c>, <c>workflow:{key}</c>, <c>group-runs:{id}</c> or <c>group-tasks:{id}</c>.</summary>
    public required string Key { get; set; }

    public int Max { get; set; }
    public int InUse { get; set; }
}
