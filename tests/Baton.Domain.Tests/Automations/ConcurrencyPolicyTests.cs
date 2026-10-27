using Baton.Domain.Automations;

namespace Baton.Domain.Tests.Automations;

/// <summary>AUT-07: what a firing does at and below the automation's run limit.</summary>
public sealed class ConcurrencyPolicyTests
{
    [Theory]
    [InlineData(ConcurrencyPolicy.Skip)]
    [InlineData(ConcurrencyPolicy.Queue)]
    [InlineData(ConcurrencyPolicy.Replace)]
    public void Below_the_limit_every_policy_but_allow_creates_a_run_that_holds_the_automation_limit(ConcurrencyPolicy policy)
    {
        Assert.Equal(new FireDecision(FireAction.Create, true), ConcurrencyPolicies.Decide(policy, maxConcurrentRuns: 2, activeRuns: 1));
    }

    [Theory]
    [InlineData(ConcurrencyPolicy.Skip, FireAction.Skip)]
    [InlineData(ConcurrencyPolicy.Queue, FireAction.Create)]
    [InlineData(ConcurrencyPolicy.Replace, FireAction.ReplaceOldest)]
    [InlineData(ConcurrencyPolicy.Allow, FireAction.Create)]
    public void At_the_limit_the_policy_decides(ConcurrencyPolicy policy, FireAction action)
    {
        Assert.Equal(action, ConcurrencyPolicies.Decide(policy, maxConcurrentRuns: 1, activeRuns: 1).Action);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Allow_never_holds_the_automation_limit(int activeRuns)
    {
        Assert.False(ConcurrencyPolicies.Decide(ConcurrencyPolicy.Allow, maxConcurrentRuns: 1, activeRuns).HoldsAutomationLimit);
    }

    [Theory]
    [InlineData("skip", ConcurrencyPolicy.Skip)]
    [InlineData("queue", ConcurrencyPolicy.Queue)]
    [InlineData("replace", ConcurrencyPolicy.Replace)]
    [InlineData("allow", ConcurrencyPolicy.Allow)]
    public void Policies_round_trip_through_their_column_values(string value, ConcurrencyPolicy policy)
    {
        Assert.True(ConcurrencyPolicies.TryParse(value, out var parsed));
        Assert.Equal(policy, parsed);
        Assert.Equal(value, ConcurrencyPolicies.ToDb(policy));
    }

    [Theory]
    [InlineData("Skip")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_policy_values_are_rejected(string? value)
    {
        Assert.False(ConcurrencyPolicies.TryParse(value, out _));
    }
}
