using Baton.Domain.Runs;

namespace Baton.Domain.Tests.Runs;

/// <summary>ORC-06: effective priority of a waiting task (M6 task 1.3).</summary>
public sealed class PriorityAgingTests
{
    [Theory]
    [InlineData(40, 0, 40)]
    [InlineData(40, 4.99, 40)]
    [InlineData(40, 5, 41)]
    [InlineData(40, 12, 42)] // spec "Waiting task ages"
    [InlineData(40, 180, 60)] // spec "Cap"
    [InlineData(95, 60, 100)] // spec "Ceiling"
    [InlineData(40, 120, 60)] // spec "Aged task overtakes": 60 > 55
    public void Effective_priority_with_default_aging(int basePriority, double minutesWaited, int expected) =>
        Assert.Equal(expected, TaskPriority.Effective(basePriority, TimeSpan.FromMinutes(minutesWaited), AgingSettings.Default));

    [Fact]
    public void Cap_zero_disables_aging() =>
        Assert.Equal(40, TaskPriority.Effective(40, TimeSpan.FromHours(10), AgingSettings.Default with { Cap = 0 }));

    [Fact]
    public void Clock_skew_never_lowers_priority() =>
        Assert.Equal(40, TaskPriority.Effective(40, TimeSpan.FromMinutes(-3), AgingSettings.Default));

    [Fact]
    public void Default_settings_are_valid() => Assert.Empty(AgingSettings.Default.Validate());

    [Theory]
    [InlineData(29, 20)]
    [InlineData(300, -1)]
    [InlineData(300, 101)]
    public void Invalid_settings_are_reported(int stepSeconds, int cap) =>
        Assert.NotEmpty(new AgingSettings(TimeSpan.FromSeconds(stepSeconds), cap).Validate());
}
