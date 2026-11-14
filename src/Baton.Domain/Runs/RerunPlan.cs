namespace Baton.Domain.Runs;

/// <summary>
/// A task that runs again in a re-run (WF-10): its initial state when the run is admitted (Waiting, Queued, or
/// Skipped when its trigger rule no longer fires) and its upstream counters, as stored in <c>task_runs</c>.
/// </summary>
public sealed record RerunTask(
    string Key, TaskRunStatus Status, int UpstreamRemaining, int UpstreamFailed, int UpstreamUnsuccessful);

/// <summary>
/// WF-10 (add-operability-and-insight design.md decision 8): which tasks of a finished run run again, and how
/// they start. The reset set is every Failed or Cancelled task plus every Skipped task with an upstream task in
/// the set, transitively; Succeeded tasks keep their output and every other Skipped task stays Skipped. A reset
/// task counts only its reset upstream tasks as remaining; its kept Skipped upstream tasks count as finished and
/// unsuccessful. A reset task with nothing left to wait for is decided by its trigger rule at once, and a Skip
/// propagates downstream exactly as <see cref="DagAdvance"/> does at run time.
/// </summary>
public static class RerunPlan
{
    /// <param name="graph">The run's graph.</param>
    /// <param name="statuses">The final status of every task of the run.</param>
    /// <returns>The reset tasks in dependency order; kept tasks are not listed.</returns>
    public static IReadOnlyList<RerunTask> Plan(DispatchGraph graph, IReadOnlyDictionary<string, TaskRunStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(statuses);

        var order = TopologicalOrder(graph);
        foreach (var key in order)
        {
            if (!statuses.TryGetValue(key, out var status))
                throw new ArgumentException($"No status for task '{key}'.", nameof(statuses));
            if (!status.IsFinal())
                throw new ArgumentException($"Task '{key}' is {status}; a run is re-run only once every task is final.", nameof(statuses));
        }

        var reset = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in order)
        {
            var status = statuses[key];
            if (status is TaskRunStatus.Failed or TaskRunStatus.Cancelled
                || (status == TaskRunStatus.Skipped && graph[key].DependsOn.Any(reset.Contains)))
            {
                reset.Add(key);
            }
        }

        var planned = new Dictionary<string, RerunTask>(StringComparer.Ordinal);
        var skipped = new Queue<string>();
        foreach (var key in order.Where(reset.Contains))
        {
            var task = graph[key];
            var remaining = task.DependsOn.Count(reset.Contains);
            var failed = task.DependsOn.Count(u => !reset.Contains(u) && statuses[u] == TaskRunStatus.Failed);
            var unsuccessful = task.DependsOn.Count(u => !reset.Contains(u) && statuses[u].IsUnsuccessful());
            var initial = remaining > 0 ? TaskRunStatus.Waiting : Decide(task.TriggerRule, failed, unsuccessful);
            planned[key] = new RerunTask(key, initial, remaining, failed, unsuccessful);
            if (initial == TaskRunStatus.Skipped)
                skipped.Enqueue(key);
        }

        // A reset task skipped at once is a finished, unsuccessful parent of its reset children (DagAdvance).
        while (skipped.TryDequeue(out var parent))
        {
            foreach (var childKey in graph.Downstream(parent))
            {
                if (!planned.TryGetValue(childKey, out var child) || child.Status != TaskRunStatus.Waiting)
                    continue;

                child = child with
                {
                    UpstreamRemaining = child.UpstreamRemaining - 1,
                    UpstreamUnsuccessful = child.UpstreamUnsuccessful + 1,
                };
                if (child.UpstreamRemaining == 0)
                {
                    child = child with { Status = Decide(graph[childKey].TriggerRule, child.UpstreamFailed, child.UpstreamUnsuccessful) };
                    if (child.Status == TaskRunStatus.Skipped)
                        skipped.Enqueue(childKey);
                }

                planned[childKey] = child;
            }
        }

        return [.. order.Where(planned.ContainsKey).Select(key => planned[key])];
    }

    private static TaskRunStatus Decide(Abstractions.TriggerRule rule, int failed, int unsuccessful) =>
        TriggerRules.Decide(rule, failed, unsuccessful) == TriggerDecision.Run ? TaskRunStatus.Queued : TaskRunStatus.Skipped;

    /// <summary>Kahn's order, ties broken by key so the plan is deterministic.</summary>
    private static List<string> TopologicalOrder(DispatchGraph graph)
    {
        var indegree = graph.Tasks.ToDictionary(t => t.Key, t => t.DependsOn.Count, StringComparer.Ordinal);
        var ready = new PriorityQueue<string, string>(StringComparer.Ordinal);
        foreach (var (key, count) in indegree)
        {
            if (count == 0)
                ready.Enqueue(key, key);
        }

        var order = new List<string>(indegree.Count);
        while (ready.TryDequeue(out var key, out _))
        {
            order.Add(key);
            foreach (var child in graph.Downstream(key))
            {
                if (--indegree[child] == 0)
                    ready.Enqueue(child, child);
            }
        }

        if (order.Count != indegree.Count)
            throw new ArgumentException("The graph has a cycle.", nameof(graph));
        return order;
    }
}
