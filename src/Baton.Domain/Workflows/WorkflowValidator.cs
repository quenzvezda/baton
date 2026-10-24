using System.Text.RegularExpressions;

namespace Baton.Domain.Workflows;

/// <summary>
/// Startup checks on a built graph (WF-03): keys, structure (Kahn's topological sort), option bounds and
/// handler types. Pure: handler registration in DI and the params schema are checked by
/// <see cref="WorkflowCompiler"/>.
/// </summary>
public static partial class WorkflowValidator
{
    /// <summary>Every problem found, without the <c>key@version</c> prefix; empty when the graph is valid.</summary>
    public static IReadOnlyList<string> Validate(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var errors = new List<string>();
        CheckKey(graph.Key, "workflow key", errors);
        if (graph.Version < 1)
            errors.Add($"version {graph.Version} must be 1 or greater");

        if (graph.Tasks.Count == 0)
        {
            errors.Add("declares no tasks");
            return errors;
        }

        CheckTaskKeys(graph, errors);
        CheckStructure(graph, errors);
        foreach (var task in graph.Tasks)
        {
            CheckOptions(task, errors);
            CheckHandler(task, graph.ParamsType, errors);
        }

        return errors;
    }

    private static void CheckKey(string key, string what, List<string> errors)
    {
        if (key.Length > PlatformBounds.MaxKeyLength)
            errors.Add($"{what} '{key}' is longer than {PlatformBounds.MaxKeyLength} characters");
        else if (!KebabCase().IsMatch(key))
            errors.Add($"{what} '{key}' must be kebab-case (lowercase letters and digits separated by single hyphens)");
    }

    private static void CheckTaskKeys(WorkflowGraph graph, List<string> errors)
    {
        foreach (var task in graph.Tasks)
            CheckKey(task.Key, "task key", errors);

        foreach (var duplicate in graph.Tasks.GroupBy(t => t.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
            errors.Add($"duplicate task key '{duplicate.Key}'");
    }

    /// <summary>Unknown dependencies, a missing root, and cycles via Kahn's algorithm.</summary>
    private static void CheckStructure(WorkflowGraph graph, List<string> errors)
    {
        var keys = graph.Tasks.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var task in graph.Tasks)
        {
            foreach (var upstream in task.DependsOn.Where(d => !keys.Contains(d)).Distinct(StringComparer.Ordinal))
                errors.Add($"task '{task.Key}' depends on unknown task '{upstream}'");
        }

        if (graph.Tasks.All(t => t.DependsOn.Count > 0))
            errors.Add("has no root: every task depends on another task");

        // Duplicate keys are reported above; the sort uses the first declaration of each key.
        var nodes = graph.Tasks.DistinctBy(t => t.Key, StringComparer.Ordinal).ToList();
        var upstreamOf = nodes.ToDictionary(
            t => t.Key,
            t => t.DependsOn.Where(keys.Contains).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var remaining = upstreamOf.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
        var ready = new Queue<string>(remaining.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        while (ready.TryDequeue(out var done))
        {
            remaining.Remove(done);
            foreach (var (key, upstream) in upstreamOf)
            {
                if (upstream.Contains(done) && remaining.ContainsKey(key) && --remaining[key] == 0)
                    ready.Enqueue(key);
            }
        }

        if (remaining.Count > 0)
        {
            // Kahn leaves the cycle plus everything downstream of it; peel off the downstream tasks
            // (nothing left depends on them) so the error names only the tasks in the cycle.
            var inCycle = remaining.Keys.ToHashSet(StringComparer.Ordinal);
            List<string> downstreamOnly;
            do
            {
                downstreamOnly = [.. inCycle.Where(k => !inCycle.Any(other => upstreamOf[other].Contains(k)))];
                inCycle.ExceptWith(downstreamOnly);
            }
            while (downstreamOnly.Count > 0);

            var cycle = string.Join(", ", nodes.Select(t => t.Key).Where(inCycle.Contains));
            errors.Add($"has a cycle between tasks {cycle}");
        }
    }

    private static void CheckOptions(TaskNode task, List<string> errors)
    {
        var retry = task.Retry;
        if (retry.MaxAttempts < 1)
            errors.Add($"task '{task.Key}': {retry.MaxAttempts} attempts; at least 1 is required");
        if (retry.MaxAttempts > PlatformBounds.MaxAttempts)
            errors.Add($"task '{task.Key}': {retry.MaxAttempts} attempts exceeds the maximum of {PlatformBounds.MaxAttempts}");
        if (retry.InitialDelay < TimeSpan.Zero)
            errors.Add($"task '{task.Key}': retry initial delay {retry.InitialDelay} is negative");
        if (retry.Multiplier < 1.0 || !double.IsFinite(retry.Multiplier))
            errors.Add($"task '{task.Key}': retry multiplier {retry.Multiplier} must be at least 1");
        if (retry.MaxDelay < retry.InitialDelay)
            errors.Add($"task '{task.Key}': retry max delay {retry.MaxDelay} is shorter than the initial delay {retry.InitialDelay}");

        if (task.Timeout <= TimeSpan.Zero)
            errors.Add($"task '{task.Key}': timeout {task.Timeout} must be positive");
        if (task.Timeout > PlatformBounds.MaxTimeout)
            errors.Add($"task '{task.Key}': timeout {task.Timeout} exceeds the maximum of {PlatformBounds.MaxTimeout}");

        if (Math.Abs(task.PriorityOffset) > PlatformBounds.MaxPriorityOffset)
            errors.Add($"task '{task.Key}': priority offset {task.PriorityOffset} is outside ±{PlatformBounds.MaxPriorityOffset}");
        if (task.RequiredLabels.Any(string.IsNullOrWhiteSpace))
            errors.Add($"task '{task.Key}': required labels must not be empty");
        if (!Enum.IsDefined(task.TriggerRule))
            errors.Add($"task '{task.Key}': unknown trigger rule {(int)task.TriggerRule}");
    }

    private static void CheckHandler(TaskNode task, Type paramsType, List<string> errors)
    {
        var handler = task.HandlerType;
        if (handler.IsAbstract || handler.IsGenericTypeDefinition)
        {
            errors.Add($"task '{task.Key}': handler '{handler}' must be a concrete, non-generic class");
            return;
        }

        var interfaces = TaskHandlerTypes.HandlerInterfaces(handler).ToList();
        if (interfaces.Count == 0)
        {
            errors.Add($"task '{task.Key}': handler '{handler}' does not implement ITaskHandler<TParams, TOutput>");
            return;
        }

        var matching = interfaces.Count(i => i.GetGenericArguments()[0] == paramsType);
        if (matching == 0)
        {
            var expected = string.Join(", ", interfaces.Select(i => i.GetGenericArguments()[0]).Distinct());
            errors.Add($"task '{task.Key}': handler '{handler}' expects params '{expected}' but the workflow's params are '{paramsType}'");
        }
        else if (matching > 1)
        {
            errors.Add($"task '{task.Key}': handler '{handler}' implements ITaskHandler for '{paramsType}' more than once");
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabCase();
}
