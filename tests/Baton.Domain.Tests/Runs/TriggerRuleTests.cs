using Baton.Abstractions;
using Baton.Domain.Runs;
using static Baton.Domain.Runs.TaskRunStatus;

namespace Baton.Domain.Tests.Runs;

/// <summary>WF-06: every rule against every mix of final upstream outcomes.</summary>
public sealed class TriggerRuleTests
{
    public static TheoryData<TriggerRule, TaskRunStatus[], TriggerDecision> Cases => new()
    {
        // AllSuccess: runs only if every upstream task succeeded
        { TriggerRule.AllSuccess, [], TriggerDecision.Run },
        { TriggerRule.AllSuccess, [Succeeded, Succeeded], TriggerDecision.Run },
        { TriggerRule.AllSuccess, [Succeeded, Failed], TriggerDecision.Skip },
        { TriggerRule.AllSuccess, [Succeeded, Skipped], TriggerDecision.Skip },
        { TriggerRule.AllSuccess, [Succeeded, Cancelled], TriggerDecision.Skip },
        { TriggerRule.AllSuccess, [Failed, Skipped, Cancelled], TriggerDecision.Skip },

        // AllDone: always runs
        { TriggerRule.AllDone, [], TriggerDecision.Run },
        { TriggerRule.AllDone, [Succeeded, Succeeded], TriggerDecision.Run },
        { TriggerRule.AllDone, [Succeeded, Failed], TriggerDecision.Run },
        { TriggerRule.AllDone, [Skipped], TriggerDecision.Run },
        { TriggerRule.AllDone, [Cancelled], TriggerDecision.Run },
        { TriggerRule.AllDone, [Failed, Skipped, Cancelled], TriggerDecision.Run },

        // OneFailed: runs only if at least one upstream task failed
        { TriggerRule.OneFailed, [], TriggerDecision.Skip },
        { TriggerRule.OneFailed, [Succeeded, Succeeded], TriggerDecision.Skip },
        { TriggerRule.OneFailed, [Succeeded, Failed], TriggerDecision.Run },
        { TriggerRule.OneFailed, [Succeeded, Skipped], TriggerDecision.Skip },
        { TriggerRule.OneFailed, [Succeeded, Cancelled], TriggerDecision.Skip },
        { TriggerRule.OneFailed, [Failed, Skipped, Cancelled], TriggerDecision.Run },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rule_decides_by_upstream_outcomes(TriggerRule rule, TaskRunStatus[] upstream, TriggerDecision expected)
    {
        Assert.Equal(expected, TriggerRules.Decide(rule, upstream));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Counter_form_agrees_with_the_status_form(TriggerRule rule, TaskRunStatus[] upstream, TriggerDecision expected)
    {
        var failed = upstream.Count(s => s == Failed);
        var unsuccessful = upstream.Count(s => s.IsUnsuccessful());

        Assert.Equal(expected, TriggerRules.Decide(rule, failed, unsuccessful));
    }

    [Fact]
    public void Skipped_propagates_through_all_success_children_until_all_done()
    {
        // a fails → b (AllSuccess on a) skipped → c (AllSuccess on b) skipped → d (AllDone on c) runs
        var b = Outcome(TriggerRules.Decide(TriggerRule.AllSuccess, [Failed]));
        var c = Outcome(TriggerRules.Decide(TriggerRule.AllSuccess, [b]));
        var d = TriggerRules.Decide(TriggerRule.AllDone, [c]);

        Assert.Equal(Skipped, b);
        Assert.Equal(Skipped, c);
        Assert.Equal(TriggerDecision.Run, d);
    }

    [Fact]
    public void Deciding_before_upstream_is_final_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => TriggerRules.Decide(TriggerRule.AllDone, [Succeeded, Running]));
    }

    private static TaskRunStatus Outcome(TriggerDecision decision) =>
        decision == TriggerDecision.Skip ? Skipped : Succeeded;
}
