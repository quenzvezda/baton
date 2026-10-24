using Baton.Abstractions;
using Baton.Domain.Tests.Samples;
using Baton.Domain.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Domain.Tests.Workflows;

/// <summary>
/// WF-03: one failing test per startup-validation scenario. <see cref="WorkflowCompiler"/> is what
/// startup runs for every definition, so a throw here is a refused start.
/// </summary>
public sealed class WorkflowValidationTests
{
    [Fact]
    public void Cycle_stops_startup_naming_the_workflow_and_the_tasks_in_the_cycle()
    {
        var definition = InlineWorkflow.Of(wf =>
        {
            var start = wf.Task<StepA>("start");
            var a = wf.Task<StepA>("a").DependsOn(start);
            var b = wf.Task<StepB>("b").DependsOn(a);
            a.DependsOn(b);
            wf.Task<StepB>("after").DependsOn(b);
        });

        var ex = Invalid(definition);

        Assert.Contains("test-workflow@1: has a cycle between tasks a, b", ex.Errors);
    }

    [Fact]
    public void Duplicate_task_key_stops_startup()
    {
        var ex = Invalid(InlineWorkflow.Of(wf =>
        {
            wf.Task<StepA>("extract");
            wf.Task<StepB>("extract");
        }));

        Assert.Contains("test-workflow@1: duplicate task key 'extract'", ex.Errors);
    }

    [Theory]
    [InlineData("Extract")]
    [InlineData("extract_orders")]
    [InlineData("extract--orders")]
    [InlineData("-extract")]
    [InlineData("")]
    public void Task_key_that_is_not_kebab_case_stops_startup(string key)
    {
        var ex = Invalid(InlineWorkflow.Of(wf => wf.Task<StepA>(key)));

        Assert.Contains(ex.Errors, e => e.Contains($"task key '{key}' must be kebab-case", StringComparison.Ordinal));
    }

    [Fact]
    public void Task_key_longer_than_64_characters_stops_startup()
    {
        var key = new string('a', 65);

        var ex = Invalid(InlineWorkflow.Of(wf => wf.Task<StepA>(key)));

        Assert.Contains($"test-workflow@1: task key '{key}' is longer than 64 characters", ex.Errors);
    }

    [Fact]
    public void Unregistered_handler_stops_startup()
    {
        var definition = InlineWorkflow.Of(wf =>
        {
            var a = wf.Task<StepA>("a");
            wf.Task<StepB>("b").DependsOn(a);
        });

        var ex = Invalid(definition, new Registered(typeof(StepA)));

        var error = Assert.Single(ex.Errors);
        Assert.StartsWith("test-workflow@1: handler 'Baton.Domain.Tests.Workflows.StepB' is not registered", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Handler_expecting_other_params_stops_startup()
    {
        var ex = Invalid(InlineWorkflow.Of(wf => wf.Task<StringParamsHandler>("a")));

        var error = Assert.Single(ex.Errors);
        Assert.Contains("task 'a': handler", error, StringComparison.Ordinal);
        Assert.Contains("expects params 'System.String'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Handler_type_that_is_not_a_task_handler_stops_startup()
    {
        var ex = Invalid(InlineWorkflow.Of(wf => wf.Task<NotAHandler>("a")));

        Assert.Contains(ex.Errors, e => e.Contains("does not implement ITaskHandler", StringComparison.Ordinal));
    }

    [Fact]
    public void Params_that_cannot_be_serialized_stop_startup()
    {
        var definition = new InlineWorkflow<UnserializableParams>(wf => wf.Task<UnserializableHandler>("a"), key: "bad-params");

        var ex = Invalid(definition);

        var error = Assert.Single(ex.Errors);
        Assert.StartsWith("bad-params@1: params type", error, StringComparison.Ordinal);
        Assert.Contains("cannot be serialized", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Abstract_params_stop_startup()
    {
        var definition = new InlineWorkflow<AbstractParams>(wf => wf.Task<AbstractParamsHandler>("a"), key: "abstract-params");

        var ex = Invalid(definition);

        Assert.Contains(ex.Errors, e => e.Contains("is abstract and cannot be deserialized", StringComparison.Ordinal));
    }

    [Fact]
    public void More_than_ten_attempts_stops_startup()
    {
        var ex = Invalid(InlineWorkflow.Of(wf => wf.Task<StepA>("a").Retry(11, TimeSpan.FromSeconds(1))));

        Assert.Contains("test-workflow@1: task 'a': 11 attempts exceeds the maximum of 10", ex.Errors);
    }

    [Fact]
    public void Timeout_longer_than_24_hours_stops_startup()
    {
        var ex = Invalid(InlineWorkflow.Of(wf => wf.Task<StepA>("a").Timeout(TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)))));

        Assert.Contains("test-workflow@1: task 'a': timeout 1.00:00:01 exceeds the maximum of 1.00:00:00", ex.Errors);
    }

    [Fact]
    public void Options_exactly_at_the_bounds_are_accepted()
    {
        var compiled = WorkflowCompiler.Compile(InlineWorkflow.Of(wf => wf.Task<StepA>("a")
            .Retry(10, TimeSpan.FromSeconds(1))
            .Timeout(TimeSpan.FromHours(24))));

        Assert.Equal("test-workflow@1", compiled.Id);
    }

    [Fact]
    public void Graph_without_a_root_stops_startup()
    {
        var ex = Invalid(InlineWorkflow.Of(wf =>
        {
            var a = wf.Task<StepA>("a");
            var b = wf.Task<StepB>("b").DependsOn(a);
            a.DependsOn(b);
        }));

        Assert.Contains("test-workflow@1: has no root: every task depends on another task", ex.Errors);
    }

    [Fact]
    public void Workflow_without_tasks_stops_startup()
    {
        var ex = Invalid(InlineWorkflow.Of(_ => { }));

        Assert.Contains("test-workflow@1: declares no tasks", ex.Errors);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var ex = Invalid(InlineWorkflow.Of(wf =>
        {
            wf.Task<StepA>("a").Timeout(TimeSpan.Zero);
            wf.Task<StepA>("a").PriorityOffset(101);
        }));

        Assert.Equal(3, ex.Errors.Count);
        Assert.Contains("test-workflow@1: duplicate task key 'a'", ex.Errors);
        Assert.Contains("test-workflow@1: task 'a': timeout 00:00:00 must be positive", ex.Errors);
        Assert.Contains("test-workflow@1: task 'a': priority offset 101 is outside ±100", ex.Errors);
    }

    [Fact]
    public void Exception_thrown_while_building_stops_startup()
    {
        var ex = Invalid(InlineWorkflow.Of(_ => throw new InvalidOperationException("boom")));

        Assert.Equal(["test-workflow@1: Build threw InvalidOperationException: boom"], ex.Errors);
    }

    [Fact]
    public void Spec_example_passes_every_check()
    {
        var registered = new Registered(
            typeof(ExtractOrders), typeof(ExtractCustomers), typeof(BuildReport), typeof(PublishReport), typeof(NotifyOnFailure));

        var compiled = WorkflowCompiler.Compile(new NightlySalesReport(), registered);

        Assert.Equal("nightly-sales-report@2", compiled.Id);
        Assert.Equal(64, compiled.Hash.Length);
    }

    [Fact]
    public void Workflow_set_reports_the_problems_of_all_definitions_together()
    {
        IWorkflowDefinition[] definitions =
        [
            InlineWorkflow.Of(wf => wf.Task<StepA>("Bad"), key: "first"),
            InlineWorkflow.Of(wf => wf.Task<StepA>("a").Retry(20, TimeSpan.Zero), key: "second"),
            InlineWorkflow.Of(wf => wf.Task<StepA>("a"), key: "twice"),
            InlineWorkflow.Of(wf => wf.Task<StepB>("b"), key: "twice"),
        ];

        var ex = Assert.Throws<WorkflowDefinitionException>(
            () => new WorkflowSet(definitions, new Registered(typeof(StepA), typeof(StepB))));

        Assert.Contains(ex.Errors, e => e.StartsWith("first@1: task key 'Bad'", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("second@1: task 'a': 20 attempts", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("twice@1: declared by more than one definition", StringComparison.Ordinal));
        Assert.Contains("second@1", ex.Message, StringComparison.Ordinal);
    }

    private static WorkflowDefinitionException Invalid(IWorkflowDefinition definition, IServiceProviderIsService? services = null) =>
        Assert.Throws<WorkflowDefinitionException>(() => WorkflowCompiler.Compile(definition, services));

    private sealed class Registered(params Type[] types) : IServiceProviderIsService
    {
        public bool IsService(Type serviceType) => types.Contains(serviceType);
    }

    private sealed class StringParamsHandler : ITaskHandler<string, int>
    {
        public Task<int> ExecuteAsync(TaskContext<string> ctx, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class NotAHandler;

    private sealed record UnserializableParams(Type Kind);

    private sealed class UnserializableHandler : ITaskHandler<UnserializableParams, int>
    {
        public Task<int> ExecuteAsync(TaskContext<UnserializableParams> ctx, CancellationToken ct) => Task.FromResult(0);
    }

    private abstract record AbstractParams(string Name);

    private sealed class AbstractParamsHandler : ITaskHandler<AbstractParams, int>
    {
        public Task<int> ExecuteAsync(TaskContext<AbstractParams> ctx, CancellationToken ct) => Task.FromResult(0);
    }
}
