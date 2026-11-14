using Baton.Abstractions;
using Baton.Domain.Retries;
using Baton.Domain.Runs;
using static Baton.Domain.Runs.TaskRunStatus;

namespace Baton.Domain.Tests.Runs;

/// <summary>WF-10: which tasks a re-run resets and how they start (design.md decision 8).</summary>
public sealed class RerunPlanTests
{
    // extract-a ─┐
    //            ├─> build-report ─> publish
    // extract-b ─┘        └──────> notify-failure (OneFailed on build-report and both extracts)
    private static readonly DispatchGraph Report = Graph(
        Node("extract-a"),
        Node("extract-b"),
        Node("build-report", deps: ["extract-a", "extract-b"]),
        Node("publish", deps: ["build-report"]),
        Node("notify-failure", TriggerRule.OneFailed, "build-report", "extract-a", "extract-b"));

    [Fact]
    public void Last_task_failed_resets_it_and_its_skipped_downstream()
    {
        var plan = RerunPlan.Plan(Report, Statuses(
            ("extract-a", Succeeded), ("extract-b", Succeeded), ("build-report", Failed), ("publish", Skipped),
            ("notify-failure", Succeeded)));

        Assert.Equal(
            [new RerunTask("build-report", Queued, 0, 0, 0), new RerunTask("publish", Waiting, 1, 0, 0)],
            plan);
    }

    [Fact]
    public void Succeeded_failure_only_task_is_kept()
    {
        var plan = RerunPlan.Plan(Report, Statuses(
            ("extract-a", Succeeded), ("extract-b", Succeeded), ("build-report", Failed), ("publish", Skipped),
            ("notify-failure", Succeeded)));

        Assert.DoesNotContain(plan, t => t.Key == "notify-failure");
        Assert.DoesNotContain(plan, t => t.Key.StartsWith("extract", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_failure_only_task_waits_for_its_reset_upstream()
    {
        var plan = RerunPlan.Plan(Report, Statuses(
            ("extract-a", Succeeded), ("extract-b", Succeeded), ("build-report", Failed), ("publish", Skipped),
            ("notify-failure", Failed)));

        Assert.Contains(new RerunTask("notify-failure", Waiting, 1, 0, 0), plan);
    }

    [Fact]
    public void Skipped_task_with_kept_upstream_stays_skipped()
    {
        // a fails -> b (AllSuccess) skipped; c (OneFailed on x) skipped because x succeeded.
        var graph = Graph(Node("a"), Node("b", deps: ["a"]), Node("x"), Node("c", TriggerRule.OneFailed, "x"));

        var plan = RerunPlan.Plan(graph, Statuses(("a", Failed), ("b", Skipped), ("x", Succeeded), ("c", Skipped)));

        Assert.Equal(["a", "b"], plan.Select(t => t.Key));
    }

    [Fact]
    public void Diamond_resets_only_the_failed_branch_and_the_join()
    {
        // root -> left, right -> join; right failed.
        var graph = Graph(Node("root"), Node("left", deps: ["root"]), Node("right", deps: ["root"]), Node("join", deps: ["left", "right"]));

        var plan = RerunPlan.Plan(graph, Statuses(("root", Succeeded), ("left", Succeeded), ("right", Failed), ("join", Skipped)));

        Assert.Equal([new RerunTask("right", Queued, 0, 0, 0), new RerunTask("join", Waiting, 1, 0, 0)], plan);
    }

    [Fact]
    public void Timed_out_run_resets_cancelled_waiting_tasks()
    {
        var plan = RerunPlan.Plan(Report, Statuses(
            ("extract-a", Succeeded), ("extract-b", Cancelled), ("build-report", Cancelled), ("publish", Cancelled),
            ("notify-failure", Cancelled)));

        Assert.Equal(
        [
            new RerunTask("extract-b", Queued, 0, 0, 0),
            new RerunTask("build-report", Waiting, 1, 0, 0),
            new RerunTask("notify-failure", Waiting, 2, 0, 0),
            new RerunTask("publish", Waiting, 1, 0, 0),
        ], plan);
    }

    [Fact]
    public void Reset_task_whose_rule_no_longer_fires_is_skipped_and_propagates()
    {
        // x succeeded; alert (OneFailed on x) was cancelled by a timeout, followup waits on alert.
        var graph = Graph(Node("x"), Node("alert", TriggerRule.OneFailed, "x"), Node("followup", deps: ["alert"]));

        var plan = RerunPlan.Plan(graph, Statuses(("x", Succeeded), ("alert", Cancelled), ("followup", Cancelled)));

        Assert.Equal([new RerunTask("alert", Skipped, 0, 0, 0), new RerunTask("followup", Skipped, 0, 0, 1)], plan);
    }

    [Fact]
    public void Kept_skipped_upstream_counts_as_unsuccessful()
    {
        // s (OneFailed on ok) stayed Skipped; join (AllDone on s and f) was cancelled while f ran.
        var graph = Graph(Node("ok"), Node("s", TriggerRule.OneFailed, "ok"), Node("f"), Node("join", TriggerRule.AllDone, "s", "f"));

        var plan = RerunPlan.Plan(graph, Statuses(("ok", Succeeded), ("s", Skipped), ("f", Failed), ("join", Cancelled)));

        Assert.Equal([new RerunTask("f", Queued, 0, 0, 0), new RerunTask("join", Waiting, 1, 0, 1)], plan);
    }

    [Fact]
    public void Unfinished_task_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => RerunPlan.Plan(Report, Statuses(
            ("extract-a", Succeeded), ("extract-b", Running), ("build-report", Waiting), ("publish", Waiting),
            ("notify-failure", Waiting))));
    }

    private static Dictionary<string, TaskRunStatus> Statuses(params (string Key, TaskRunStatus Status)[] statuses) =>
        statuses.ToDictionary(s => s.Key, s => s.Status, StringComparer.Ordinal);

    private static DispatchTask Node(string key, TriggerRule rule = TriggerRule.AllSuccess, params string[] deps) =>
        new(key, deps, RetryPolicy.Default, TimeSpan.FromHours(1), rule, [], 0);

    private static DispatchGraph Graph(params DispatchTask[] tasks) => new(tasks);
}
