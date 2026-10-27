using Baton.Abstractions;

namespace Baton.Domain.Runs;

/// <summary>A Waiting task's counters as stored in <c>task_runs</c>.</summary>
public sealed record WaitingTask(string Key, TriggerRule TriggerRule, int UpstreamRemaining, int UpstreamFailed, int UpstreamUnsuccessful);

/// <summary>
/// What happens to one Waiting task: its new counters and, once every upstream task is final, whether it
/// becomes Queued or Skipped (<see cref="NewStatus"/>; <c>null</c> while it keeps waiting).
/// </summary>
public sealed record TaskTransition(string Key, int UpstreamRemaining, int UpstreamFailed, int UpstreamUnsuccessful, TaskRunStatus? NewStatus);

/// <summary>
/// ORC-03: advances the DAG when a task becomes final. Each downstream task counts the finished parent;
/// a task whose last parent finished is Queued or Skipped by its trigger rule, and a Skipped task is a
/// finished, unsuccessful parent of its own downstream tasks, so skips propagate in the same step.
/// </summary>
public static class DagAdvance
{
    /// <param name="graph">The run's graph.</param>
    /// <param name="waiting">The run's Waiting tasks by key; tasks in any other state are left alone.</param>
    /// <param name="finishedKey">The task that just became final.</param>
    /// <param name="outcome">Its final status.</param>
    /// <returns>One transition per Waiting task whose counters changed, in the order they were reached.</returns>
    public static IReadOnlyList<TaskTransition> Advance(
        DispatchGraph graph, IReadOnlyDictionary<string, WaitingTask> waiting, string finishedKey, TaskRunStatus outcome)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(waiting);
        if (!outcome.IsFinal())
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Only a final task advances the DAG.");

        var state = new Dictionary<string, WaitingTask>(StringComparer.Ordinal);
        var order = new List<string>();
        var decided = new Dictionary<string, TaskRunStatus>(StringComparer.Ordinal);
        var finished = new Queue<(string Key, TaskRunStatus Outcome)>();
        finished.Enqueue((finishedKey, outcome));

        while (finished.TryDequeue(out var parent))
        {
            var failed = parent.Outcome == TaskRunStatus.Failed ? 1 : 0;
            var unsuccessful = parent.Outcome.IsUnsuccessful() ? 1 : 0;

            foreach (var childKey in graph.Downstream(parent.Key))
            {
                if (!state.TryGetValue(childKey, out var child))
                {
                    if (!waiting.TryGetValue(childKey, out child))
                        continue;
                    order.Add(childKey);
                }

                if (child.UpstreamRemaining == 0)
                    throw new InvalidOperationException($"Task '{childKey}' has no upstream task left to finish.");

                child = child with
                {
                    UpstreamRemaining = child.UpstreamRemaining - 1,
                    UpstreamFailed = child.UpstreamFailed + failed,
                    UpstreamUnsuccessful = child.UpstreamUnsuccessful + unsuccessful,
                };
                state[childKey] = child;

                if (child.UpstreamRemaining > 0)
                    continue;

                var decision = TriggerRules.Decide(child.TriggerRule, child.UpstreamFailed, child.UpstreamUnsuccessful);
                if (decision == TriggerDecision.Run)
                {
                    decided[childKey] = TaskRunStatus.Queued;
                }
                else
                {
                    decided[childKey] = TaskRunStatus.Skipped;
                    finished.Enqueue((childKey, TaskRunStatus.Skipped));
                }
            }
        }

        return [.. order.Select(key =>
        {
            var task = state[key];
            return new TaskTransition(key, task.UpstreamRemaining, task.UpstreamFailed, task.UpstreamUnsuccessful,
                decided.TryGetValue(key, out var status) ? status : null);
        })];
    }
}
