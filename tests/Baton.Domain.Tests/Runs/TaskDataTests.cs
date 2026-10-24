using System.Text.Json;
using Baton.Abstractions;
using Baton.Domain.Runs;
using Baton.Domain.Workflows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Baton.Domain.Tests.Runs;

/// <summary>WF-07, WF-08, NFR-ExecutionGuarantee: what a task receives and returns.</summary>
public sealed class TaskDataTests
{
    private static readonly Guid RunId = Guid.Parse("0192f0c4-7a3e-7c1d-9b2a-5e4f3d2c1b0a");

    [Fact]
    public void Ancestor_output_is_readable()
    {
        var ctx = Context("build-report", ("extract-orders", new Orders(42)));

        Assert.Equal(new Orders(42), ctx.GetOutput<Orders>("extract-orders"));
    }

    [Fact]
    public void Reading_a_non_ancestor_output_fails_naming_both_tasks()
    {
        var ctx = Context("extract-customers", ("other-ancestor", new Orders(1)));

        var ex = Assert.Throws<InvalidOperationException>(() => ctx.GetOutput<Orders>("extract-orders"));

        Assert.Contains("'extract-customers'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'extract-orders'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_at_the_limit_is_accepted()
    {
        var utf8 = TaskOutput.Serialize(Padded(TaskOutput.MaxBytes), typeof(string), "build-report");

        Assert.Equal(TaskOutput.MaxBytes, utf8.Length);
    }

    [Fact]
    public void Oversized_output_fails_the_attempt_as_non_retryable()
    {
        var ex = Assert.Throws<NonRetryableException>(
            () => TaskOutput.Serialize(Padded(TaskOutput.MaxBytes + 1), typeof(string), "build-report"));

        Assert.Contains("Output of task 'build-report' is too large: 262145 bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outputs_use_the_workflow_serializer_options()
    {
        var utf8 = TaskOutput.Serialize(new Orders(3), typeof(Orders), "extract-orders");

        Assert.Equal("""{"count":3}""", System.Text.Encoding.UTF8.GetString(utf8));
    }

    [Fact]
    public void Every_attempt_gets_the_same_idempotency_key()
    {
        var first = Context("build-report", attempt: 1);
        var second = Context("build-report", attempt: 2);

        Assert.Equal($"{RunId}:build-report", first.IdempotencyKey);
        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void Idempotency_key_differs_per_task()
    {
        Assert.NotEqual(Context("extract-orders").IdempotencyKey, Context("extract-customers").IdempotencyKey);
    }

    /// <summary>A JSON string literal of exactly <paramref name="utf8Bytes"/> bytes once serialized (quotes included).</summary>
    private static string Padded(int utf8Bytes) => new('x', utf8Bytes - 2);

    private static TaskContext<string> Context(string taskKey, params (string Key, object Output)[] upstream) =>
        Context(taskKey, attempt: 1, upstream);

    private static TaskContext<string> Context(string taskKey, int attempt, params (string Key, object Output)[] upstream) => new()
    {
        RunId = RunId,
        TaskKey = taskKey,
        Attempt = attempt,
        Params = "params",
        Logger = NullLogger.Instance,
        UpstreamOutputs = upstream.ToDictionary(
            u => u.Key,
            u => JsonSerializer.SerializeToElement(u.Output, u.Output.GetType(), WorkflowJson.Options)),
        SerializerOptions = WorkflowJson.Options,
    };

    private sealed record Orders(int Count);
}
