using System.Collections.Concurrent;
using System.Diagnostics;
using Baton.Agent.Execution;
using Baton.Agent.Protocol.V1;
using Microsoft.Extensions.Logging;

namespace Baton.Agent.Tests;

/// <summary>OBS-06 "Trace per run" and "Handler logs in telemetry" on the agent side (M7 tasks 6.1–6.3).</summary>
public sealed class TaskTelemetryTests : IAsyncLifetime, IDisposable
{
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ActivityListener _listener;
    private AgentHarness _agent = null!;

    public TaskTelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TaskExecutor.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _spans.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public Task InitializeAsync()
    {
        _agent = AgentHarness.Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _agent.DisposeAsync();

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task Handler_attempt_is_a_span_in_the_run_trace()
    {
        var (traceId, parentId, traceParent) = NewTraceParent();
        var lease = _agent.Lease("echo");
        lease.TraceParent = traceParent;
        lease.Attempt = 2;

        var result = await RunAsync(lease, NullLogger());

        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        var span = Single(lease);
        Assert.Equal(TaskExecutor.ExecuteActivity, span.OperationName);
        Assert.Equal(traceId, span.TraceId);
        Assert.Equal(parentId, span.ParentSpanId);
        Assert.Equal(lease.WorkflowRunId, span.GetTagItem("baton.run.id"));
        Assert.Equal("echo", span.GetTagItem("baton.task.key"));
        Assert.Equal(2, span.GetTagItem("baton.attempt"));
        Assert.Equal("Succeeded", span.GetTagItem("baton.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task Failed_attempt_span_has_error_status_and_exception()
    {
        var lease = _agent.Lease("fail");
        lease.TraceParent = NewTraceParent().TraceParent;

        await RunAsync(lease, NullLogger());

        var span = Single(lease);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("transient trouble", span.StatusDescription);
        var exception = Assert.Single(span.Events, e => e.Name == "exception");
        Assert.Equal(typeof(InvalidOperationException).FullName, exception.Tags.Single(t => t.Key == "exception.type").Value);
        Assert.Equal("transient trouble", exception.Tags.Single(t => t.Key == "exception.message").Value);
    }

    [Fact]
    public async Task Old_agent_lease_without_trace_parent_still_runs()
    {
        var lease = _agent.Lease("echo");

        var result = await RunAsync(lease, NullLogger());

        Assert.Equal(TaskOutcome.Succeeded, result.Outcome);
        Assert.Equal(default, Single(lease).ParentSpanId);
    }

    [Fact]
    public async Task Handler_log_record_carries_run_task_attempt_and_span()
    {
        var lease = _agent.Lease("write-logs");
        lease.TraceParent = NewTraceParent().TraceParent;
        var logger = new CapturingLogger();

        await RunAsync(lease, new TaskLogBuffer(logger));

        var span = Single(lease);
        var record = logger.Records.First();
        Assert.Equal(span.SpanId, record.SpanId);
        Assert.Equal(span.TraceId, record.TraceId);
        Assert.Equal(lease.WorkflowRunId, record.Scope["baton.run.id"]);
        Assert.Equal("write-logs", record.Scope["baton.task.key"]);
        Assert.Equal(lease.Attempt, record.Scope["baton.attempt"]);
    }

    private Activity Single(TaskLease lease) =>
        Assert.Single(_spans, s => (string?)s.GetTagItem("baton.task_run.id") == lease.TaskRunId);

    private async Task<TaskResult> RunAsync(TaskLease lease, ILogger logger)
    {
        using var task = new RunningTask(lease);
        return await _agent.Executor.ExecuteAsync(task, logger, CancellationToken.None);
    }

    private static (ActivityTraceId TraceId, ActivitySpanId ParentId, string TraceParent) NewTraceParent()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var parentId = ActivitySpanId.CreateRandom();
        return (traceId, parentId, $"00-{traceId}-{parentId}-01");
    }

    private static Microsoft.Extensions.Logging.Abstractions.NullLogger NullLogger() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private sealed record Captured(string Message, ActivityTraceId TraceId, ActivitySpanId SpanId, IReadOnlyDictionary<string, object> Scope);

    /// <summary>Records each line with the current span and the merged dictionary scopes, as a logging provider sees them.</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly AsyncLocal<ImmutableStack> _scopes = new();

        public ConcurrentQueue<Captured> Records { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var previous = _scopes.Value ?? ImmutableStack.Empty;
            _scopes.Value = previous.Push(state);
            return new Pop(() => _scopes.Value = previous);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var item in (_scopes.Value ?? ImmutableStack.Empty).Items)
            {
                if (item is IEnumerable<KeyValuePair<string, object>> pairs)
                {
                    foreach (var (key, value) in pairs)
                        scope[key] = value;
                }
            }

            var current = Activity.Current;
            Records.Enqueue(new Captured(formatter(state, exception), current?.TraceId ?? default, current?.SpanId ?? default, scope));
        }

        private sealed class Pop(Action pop) : IDisposable
        {
            public void Dispose() => pop();
        }

        private sealed class ImmutableStack(IReadOnlyList<object> items)
        {
            public static readonly ImmutableStack Empty = new([]);

            public IReadOnlyList<object> Items { get; } = items;

            public ImmutableStack Push(object item) => new([.. Items, item]);
        }
    }
}
