using Baton.Abstractions;
using Baton.Domain.Retries;
using Baton.Domain.Tests.Samples;
using Baton.Domain.Workflows;

namespace Baton.Domain.Tests.Workflows;

/// <summary>WF-02, WF-05: the builder turns a definition into an immutable graph with defaults applied.</summary>
public sealed class GraphBuilderTests
{
    [Fact]
    public void Task_without_options_gets_the_defaults()
    {
        var graph = WorkflowGraph.FromDefinition(InlineWorkflow.Of(wf => wf.Task<StepA>("only")));

        var task = Assert.Single(graph.Tasks);
        Assert.Equal(new RetryPolicy(3, TimeSpan.FromSeconds(30), 2.0, TimeSpan.FromMinutes(10)), task.Retry);
        Assert.Equal(TimeSpan.FromHours(1), task.Timeout);
        Assert.Equal(TriggerRule.AllSuccess, task.TriggerRule);
        Assert.Empty(task.DependsOn);
        Assert.Empty(task.RequiredLabels);
        Assert.Equal(0, task.PriorityOffset);
        Assert.Equal(typeof(StepA), task.HandlerType);
    }

    [Fact]
    public void Declared_options_are_recorded()
    {
        var graph = WorkflowGraph.FromDefinition(InlineWorkflow.Of(wf => wf.Task<StepA>("big")
            .Retry(4, TimeSpan.FromSeconds(10), multiplier: 3, maxDelay: TimeSpan.FromMinutes(2))
            .Timeout(TimeSpan.FromMinutes(15))
            .When(TriggerRule.AllDone)
            .RequireLabels("large-memory")
            .PriorityOffset(-5)));

        var task = Assert.Single(graph.Tasks);
        Assert.Equal(new RetryPolicy(4, TimeSpan.FromSeconds(10), 3, TimeSpan.FromMinutes(2)), task.Retry);
        Assert.Equal(TimeSpan.FromMinutes(15), task.Timeout);
        Assert.Equal(TriggerRule.AllDone, task.TriggerRule);
        Assert.Equal(["large-memory"], task.RequiredLabels);
        Assert.Equal(-5, task.PriorityOffset);
    }

    [Fact]
    public void Retry_without_max_delay_keeps_the_default_max_delay()
    {
        var graph = WorkflowGraph.FromDefinition(InlineWorkflow.Of(wf => wf.Task<StepA>("a").Retry(5, TimeSpan.FromSeconds(1))));

        Assert.Equal(TimeSpan.FromMinutes(10), graph.Tasks[0].Retry.MaxDelay);
        Assert.Equal(2.0, graph.Tasks[0].Retry.Multiplier);
    }

    [Fact]
    public void Spec_example_builds_its_graph()
    {
        var graph = WorkflowGraph.FromDefinition(new NightlySalesReport());

        Assert.Equal("nightly-sales-report@2", graph.Id);
        Assert.Equal("Nightly sales report", graph.DisplayName);
        Assert.Equal(typeof(SalesReportParams), graph.ParamsType);
        Assert.Equal(
            ["extract-orders", "extract-customers", "build-report", "publish-report", "notify-failure"],
            graph.Tasks.Select(t => t.Key));

        var orders = graph.Tasks[0];
        Assert.Equal(4, orders.Retry.MaxAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), orders.Timeout);

        var notify = graph.Tasks[4];
        Assert.Equal(TriggerRule.OneFailed, notify.TriggerRule);
        Assert.Equal(["extract-orders", "extract-customers", "build-report"], notify.DependsOn);
    }

    [Fact]
    public void Independent_branches_are_roots_and_the_join_waits_for_both()
    {
        var graph = WorkflowGraph.FromDefinition(new NightlySalesReport());

        var roots = graph.Tasks.Where(t => t.DependsOn.Count == 0).Select(t => t.Key);
        var report = graph.Tasks.Single(t => t.Key == "build-report");

        Assert.Equal(["extract-orders", "extract-customers"], roots);
        Assert.Equal(["extract-orders", "extract-customers"], report.DependsOn);
    }

    [Fact]
    public void Build_with_a_different_params_type_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => WorkflowGraph.FromDefinition(new MismatchedParams()));
    }

    private sealed class MismatchedParams : IWorkflowDefinition
    {
        public string Key => "mismatched";
        public int Version => 1;
        public string DisplayName => "Mismatched";
        public Type ParamsType => typeof(NoParams);

        public void Build(IWorkflowBuilder builder) => builder.For<string>().Task<StepA>("a");
    }
}
