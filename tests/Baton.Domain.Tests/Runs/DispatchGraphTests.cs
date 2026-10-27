using Baton.Abstractions;
using Baton.Domain.Hosts;
using Baton.Domain.Retries;
using Baton.Domain.Runs;
using Baton.Domain.Tests.Samples;
using Baton.Domain.Workflows;

namespace Baton.Domain.Tests.Runs;

/// <summary>The orchestrator reads the graph back from the canonical JSON stored at registration (design.md decision 5).</summary>
public sealed class DispatchGraphTests
{
    private static readonly CompiledWorkflow Sample = WorkflowCompiler.Compile(new NightlySalesReport());

    [Fact]
    public void Canonical_json_round_trips_every_option_dispatch_needs()
    {
        var graph = DispatchGraph.Parse(Sample.CanonicalJson);

        Assert.Equal(Sample.Graph.Tasks.Count, graph.Tasks.Count);
        foreach (var node in Sample.Graph.Tasks)
        {
            var task = graph[node.Key];
            Assert.Equal(node.DependsOn.Order(StringComparer.Ordinal), task.DependsOn);
            Assert.Equal(node.Retry, task.Retry);
            Assert.Equal(node.Timeout, task.Timeout);
            Assert.Equal(node.TriggerRule, task.TriggerRule);
            Assert.Equal(node.RequiredLabels.Order(StringComparer.Ordinal), task.RequiredLabels);
            Assert.Equal(node.PriorityOffset, task.PriorityOffset);
        }
    }

    [Fact]
    public void Property_order_does_not_matter()
    {
        // jsonb stores objects with its own key order.
        const string json = """
            {"tasks":[{"triggerRule":"OneFailed","timeout":"00:05:00","retry":{"maxDelay":"00:01:00","multiplier":3,
            "initialDelay":"00:00:01","maxAttempts":2},"requiredLabels":["gpu"],"priorityOffset":-5,"key":"a","dependsOn":[],
            "handler":"X"}],"version":1,"key":"k","params":"P"}
            """;

        var task = DispatchGraph.Parse(json)["a"];

        Assert.Equal(TriggerRule.OneFailed, task.TriggerRule);
        Assert.Equal(TimeSpan.FromMinutes(5), task.Timeout);
        Assert.Equal(new RetryPolicy(2, TimeSpan.FromSeconds(1), 3, TimeSpan.FromMinutes(1)), task.Retry);
        Assert.Equal(["gpu"], task.RequiredLabels);
        Assert.Equal(-5, task.PriorityOffset);
    }

    [Fact]
    public void Ancestors_are_every_task_upstream_not_only_parents()
    {
        var graph = DispatchGraph.Parse(Sample.CanonicalJson);

        Assert.Equal(["build-report", "extract-customers", "extract-orders"], graph.Ancestors("publish-report"));
        Assert.Empty(graph.Ancestors("extract-orders"));
    }

    [Fact]
    public void Downstream_lists_direct_children()
    {
        var graph = DispatchGraph.Parse(Sample.CanonicalJson);

        Assert.Equal(["notify-failure", "publish-report"], graph.Downstream("build-report").Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(95, 10, 100)]
    [InlineData(5, -10, 0)]
    [InlineData(50, 20, 70)]
    public void Base_priority_is_run_priority_plus_offset_clamped(int run, int offset, int expected)
    {
        Assert.Equal(expected, TaskPriority.Base(run, offset));
    }

    [Theory]
    [InlineData(60, 10)]
    [InlineData(3600, 10)]
    [InlineData(30, 5)]
    [InlineData(3, 0.5)]
    public void Heartbeat_interval_follows_short_leases(int leaseSeconds, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), HostLiveness.HeartbeatIntervalFor(TimeSpan.FromSeconds(leaseSeconds)));
    }
}
