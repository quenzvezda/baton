using System.Reflection;
using System.Text.Json;
using Baton.Abstractions;
using Baton.Agent.Protocol.V1;
using Baton.Domain.Runs;
using Baton.Domain.Workflows;
using Google.Protobuf;

namespace Baton.Agent.Execution;

/// <summary>How a handler run ended, ready to report with <c>Complete</c>.</summary>
public sealed record TaskResult(TaskOutcome Outcome, ByteString Output, TaskError? Error)
{
    public static TaskResult Succeeded(byte[] output) => new(TaskOutcome.Succeeded, ByteString.CopyFrom(output), null);

    public static TaskResult TimedOut(TimeSpan timeout) =>
        new(TaskOutcome.TimedOut, ByteString.Empty, new TaskError { Type = "Timeout", Message = $"The task ran longer than its timeout of {timeout}.", Retryable = true });

    public static TaskResult Cancelled(string reason) =>
        new(TaskOutcome.Cancelled, ByteString.Empty, new TaskError { Type = "Cancelled", Message = reason, Retryable = true });

    public static TaskResult Failed(Exception exception, bool retryable)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(TaskOutcome.Failed, ByteString.Empty, new TaskError
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.ToString(),
            Retryable = retryable,
        });
    }
}

/// <summary>
/// Runs one attempt's handler (design spec §8 "Agent"): resolves it from the build's definitions by graph
/// hash and task key, in its own DI scope, with the task's timeout (WF-05). It never throws: success,
/// timeout, cancellation, <see cref="NonRetryableException"/> (WF-08), any other exception and output over
/// 256 KB (WF-07) all become a <see cref="TaskResult"/>.
/// </summary>
public sealed class TaskExecutor(IServiceProvider services, WorkflowSet workflows)
{
    private static readonly MethodInfo InvokeMethod =
        typeof(TaskExecutor).GetMethod(nameof(InvokeAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly Dictionary<string, CompiledWorkflow> _byHash = workflows.Workflows
        .GroupBy(w => w.Hash, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <param name="logger">Becomes <c>TaskContext.Logger</c>.</param>
    public async Task<TaskResult> ExecuteAsync(RunningTask task, ILogger logger, CancellationToken hostStopping)
    {
        ArgumentNullException.ThrowIfNull(task);
        var lease = task.Lease;
        var timeout = lease.Timeout?.ToTimeSpan() ?? TaskDefaults.Timeout;

        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(task.Stopping, timer.Token, hostStopping);
        try
        {
            if (!_byHash.TryGetValue(lease.DefinitionHash, out var workflow))
                throw new NonRetryableException($"Definition {lease.DefinitionHash} is not in this agent's build.");
            var node = workflow.Graph.Tasks.FirstOrDefault(t => t.Key == lease.TaskKey)
                ?? throw new NonRetryableException($"{workflow.Id} has no task '{lease.TaskKey}'.");
            var handlerInterface = node.HandlerType.GetInterfaces().First(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ITaskHandler<,>)
                && i.GenericTypeArguments[0] == workflow.Graph.ParamsType);
            var outputType = handlerInterface.GenericTypeArguments[1];

            await using var scope = services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService(node.HandlerType);
            var invoke = (Task<object?>)InvokeMethod
                .MakeGenericMethod(workflow.Graph.ParamsType, outputType)
                .Invoke(null, [handler, lease, logger, linked.Token])!;
            var output = await invoke;

            return TaskResult.Succeeded(TaskOutput.Serialize(output, outputType, lease.TaskKey));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (task.CancelRequested)
                return TaskResult.Cancelled("Cancellation was requested.");
            if (timer.IsCancellationRequested)
                return TaskResult.TimedOut(timeout);
            return TaskResult.Cancelled(task.Lost ? "The lease was lost." : "The agent is stopping.");
        }
        catch (NonRetryableException ex)
        {
            return TaskResult.Failed(ex, retryable: false);
        }
        catch (Exception ex)
        {
            return TaskResult.Failed(ex, retryable: true);
        }
    }

    private static async Task<object?> InvokeAsync<TParams, TOutput>(
        ITaskHandler<TParams, TOutput> handler, TaskLease lease, ILogger logger, CancellationToken ct)
    {
        TParams parameters;
        Dictionary<string, JsonElement> upstream;
        try
        {
            parameters = JsonSerializer.Deserialize<TParams>(lease.ParamsJson.Span, WorkflowJson.Options)!;
            upstream = lease.UpstreamOutputsJson.ToDictionary(
                o => o.Key, o => JsonSerializer.Deserialize<JsonElement>(o.Value.Span), StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            // The same input fails the same way on every attempt.
            throw new NonRetryableException($"The task's input cannot be read: {ex.Message}", ex);
        }

        var context = new TaskContext<TParams>
        {
            RunId = Guid.Parse(lease.WorkflowRunId),
            TaskKey = lease.TaskKey,
            Attempt = lease.Attempt,
            Params = parameters,
            Logger = logger,
            UpstreamOutputs = upstream,
            SerializerOptions = WorkflowJson.Options,
        };
        return await handler.ExecuteAsync(context, ct);
    }
}
