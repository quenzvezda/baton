using Baton.Agent.Execution;
using Baton.Agent.Protocol.V1;
using Microsoft.Extensions.Logging.Abstractions;

namespace Baton.Agent.Tests;

/// <summary>Task 4.2: every way a handler run ends becomes the right report (WF-05, WF-07, WF-08).</summary>
public sealed class TaskExecutorTests : IAsyncLifetime
{
    private AgentHarness _agent = null!;

    public Task InitializeAsync()
    {
        _agent = AgentHarness.Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _agent.DisposeAsync();

    [Fact]
    public async Task Success_reports_the_serialized_output()
    {
        var result = await RunAsync(_agent.Lease("echo", "eu"));

        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        Assert.Equal("\"eu#1\"", result.Output.ToStringUtf8());
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Handler_reads_ancestor_outputs()
    {
        var result = await RunAsync(_agent.Lease("read-upstream", upstream: ("echo", "\"from-echo\"")));

        Assert.Equal("\"from-echo!\"", result.Output.ToStringUtf8());
    }

    [Fact]
    public async Task Exception_is_a_retryable_failure()
    {
        var result = await RunAsync(_agent.Lease("fail"));

        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.True(result.Error!.Retryable);
        Assert.Equal("System.InvalidOperationException", result.Error.Type);
        Assert.Equal("transient trouble", result.Error.Message);
    }

    [Fact]
    public async Task Non_retryable_exception_is_reported_as_not_retryable()
    {
        var result = await RunAsync(_agent.Lease("fail-fatally"));

        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.False(result.Error!.Retryable);
    }

    [Fact]
    public async Task Running_past_the_timeout_is_timed_out()
    {
        var result = await RunAsync(_agent.Lease("wait", timeout: TimeSpan.FromMilliseconds(100)));

        Assert.Equal(TaskOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task Requested_cancellation_is_cancelled()
    {
        using var task = new RunningTask(_agent.Lease("wait"));
        var run = _agent.Executor.ExecuteAsync(task, NullLogger.Instance, CancellationToken.None);

        task.RequestCancel();

        Assert.Equal(TaskOutcome.Cancelled, (await run).Outcome);
    }

    [Fact]
    public async Task Output_over_256_KB_is_a_non_retryable_failure()
    {
        var result = await RunAsync(_agent.Lease("too-much"));

        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.False(result.Error!.Retryable);
        Assert.Contains("too large", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_params_are_a_non_retryable_failure()
    {
        var lease = _agent.Lease("echo");
        lease.ParamsJson = Google.Protobuf.ByteString.CopyFromUtf8("{not json");

        var result = await RunAsync(lease);

        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.False(result.Error!.Retryable);
    }

    [Fact]
    public async Task Definition_missing_from_this_build_is_a_non_retryable_failure()
    {
        var lease = _agent.Lease("echo");
        lease.DefinitionHash = new string('0', 64);

        var result = await RunAsync(lease);

        Assert.Equal(TaskOutcome.Failed, result.Outcome);
        Assert.False(result.Error!.Retryable);
    }

    private async Task<TaskResult> RunAsync(TaskLease lease)
    {
        using var task = new RunningTask(lease);
        return await _agent.Executor.ExecuteAsync(task, NullLogger.Instance, CancellationToken.None);
    }
}
