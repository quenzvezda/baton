using Baton.Abstractions;
using Baton.Domain.Retries;
using Baton.Domain.Runs;
using static Baton.Domain.Runs.TaskRunStatus;

namespace Baton.Domain.Tests.Runs;

/// <summary>ORC-03: readiness, trigger rules and skip propagation when a task becomes final.</summary>
public sealed class DagAdvanceTests
{
    // extract-orders ─┐
    //                 ├─> build-report ─> publish-report
    // extract-cust. ──┘         └───────> notify-failure (OneFailed, also on both extracts)
    private static readonly DispatchGraph Report = Graph(
        Node("extract-orders"),
        Node("extract-customers"),
        Node("build-report", deps: ["extract-orders", "extract-customers"]),
        Node("publish-report", deps: ["build-report"]),
        Node("notify-failure", TriggerRule.OneFailed, "build-report", "extract-orders", "extract-customers"));

    [Fact]
    public void Last_parent_succeeding_queues_the_child()
    {
        var waiting = Waiting(Report, ("build-report", 1, 0, 0));

        var transitions = DagAdvance.Advance(Report, waiting, "extract-customers", Succeeded);

        var build = Assert.Single(transitions, t => t.Key == "build-report");
        Assert.Equal(new TaskTransition("build-report", 0, 0, 0, Queued), build);
    }

    [Fact]
    public void A_parent_finishing_while_others_run_only_counts_it()
    {
        var waiting = Waiting(Report);

        var transitions = DagAdvance.Advance(Report, waiting, "extract-orders", Succeeded);

        Assert.Equal(new TaskTransition("build-report", 1, 0, 0, null), transitions.Single(t => t.Key == "build-report"));
        Assert.Equal(new TaskTransition("notify-failure", 2, 0, 0, null), transitions.Single(t => t.Key == "notify-failure"));
    }

    [Fact]
    public void Skip_propagates_through_all_success_children()
    {
        // build-report waits on extract-customers; extract-orders already failed.
        var waiting = Waiting(Report, ("build-report", 1, 1, 1), ("notify-failure", 2, 1, 1));

        var transitions = DagAdvance.Advance(Report, waiting, "extract-customers", Succeeded);

        Assert.Equal(Skipped, transitions.Single(t => t.Key == "build-report").NewStatus);
        Assert.Equal(Skipped, transitions.Single(t => t.Key == "publish-report").NewStatus);
        // notify-failure counted build-report's skip as its last parent and runs on the earlier failure.
        Assert.Equal(new TaskTransition("notify-failure", 0, 1, 2, Queued), transitions.Single(t => t.Key == "notify-failure"));
    }

    [Fact]
    public void One_failed_child_is_skipped_when_nothing_failed()
    {
        var waiting = Waiting(Report, ("notify-failure", 1, 0, 0));

        var transitions = DagAdvance.Advance(Report, waiting, "build-report", Succeeded);

        Assert.Equal(Skipped, transitions.Single(t => t.Key == "notify-failure").NewStatus);
        Assert.Equal(new TaskTransition("publish-report", 0, 0, 0, Queued), transitions.Single(t => t.Key == "publish-report"));
    }

    [Fact]
    public void Cancelled_parent_counts_as_unsuccessful_not_failed()
    {
        var graph = Graph(Node("a"), Node("b", TriggerRule.AllDone, "a"), Node("c", TriggerRule.OneFailed, "a"));

        var transitions = DagAdvance.Advance(graph, Waiting(graph), "a", Cancelled);

        Assert.Equal(new TaskTransition("b", 0, 0, 1, Queued), transitions.Single(t => t.Key == "b"));
        Assert.Equal(new TaskTransition("c", 0, 0, 1, Skipped), transitions.Single(t => t.Key == "c"));
    }

    [Fact]
    public void Tasks_not_waiting_are_left_alone()
    {
        var waiting = Waiting(Report, ("build-report", 1, 0, 0));
        waiting.Remove("notify-failure");

        var transitions = DagAdvance.Advance(Report, waiting, "extract-customers", Succeeded);

        Assert.DoesNotContain(transitions, t => t.Key == "notify-failure");
    }

    [Fact]
    public void Only_final_outcomes_advance_the_dag()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DagAdvance.Advance(Report, Waiting(Report), "extract-orders", Running));
    }

    [Fact]
    public void Failure_at_the_head_of_a_500_task_chain_skips_every_task_in_one_step()
    {
        var tasks = Enumerable.Range(0, 500)
            .Select(i => i == 0 ? Node("t000") : Node($"t{i:000}", deps: [$"t{i - 1:000}"]))
            .ToArray();
        var graph = Graph(tasks);

        var transitions = DagAdvance.Advance(graph, Waiting(graph), "t000", Failed);

        Assert.Equal(499, transitions.Count);
        Assert.All(transitions, t => Assert.Equal(Skipped, t.NewStatus));
    }

    [Fact]
    public void Success_in_a_500_task_fan_out_queues_every_child()
    {
        var tasks = new[] { Node("root") }
            .Concat(Enumerable.Range(1, 499).Select(i => Node($"leaf-{i:000}", deps: ["root"])))
            .ToArray();
        var graph = Graph(tasks);

        var transitions = DagAdvance.Advance(graph, Waiting(graph), "root", Succeeded);

        Assert.Equal(499, transitions.Count);
        Assert.All(transitions, t => Assert.Equal(Queued, t.NewStatus));
    }

    private static DispatchTask Node(string key, TriggerRule rule = TriggerRule.AllSuccess, params string[] deps) =>
        new(key, deps, RetryPolicy.Default, TimeSpan.FromHours(1), rule, [], 0);

    private static DispatchGraph Graph(params DispatchTask[] tasks) => new(tasks);

    /// <summary>Every non-root task Waiting with untouched counters, then the given overrides.</summary>
    private static Dictionary<string, WaitingTask> Waiting(
        DispatchGraph graph, params (string Key, int Remaining, int Failed, int Unsuccessful)[] overrides)
    {
        var waiting = graph.Tasks
            .Where(t => t.DependsOn.Count > 0)
            .ToDictionary(t => t.Key, t => new WaitingTask(t.Key, t.TriggerRule, t.DependsOn.Count, 0, 0), StringComparer.Ordinal);
        foreach (var (key, remaining, failed, unsuccessful) in overrides)
            waiting[key] = waiting[key] with { UpstreamRemaining = remaining, UpstreamFailed = failed, UpstreamUnsuccessful = unsuccessful };
        return waiting;
    }
}
