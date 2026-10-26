using System.Globalization;
using System.Text.Json;
using Baton.Abstractions;
using Baton.Domain.Retries;

namespace Baton.Domain.Runs;

/// <summary>One task of a <see cref="DispatchGraph"/>: what dispatch, retries and the DAG advance need.</summary>
public sealed record DispatchTask(
    string Key,
    IReadOnlyList<string> DependsOn,
    RetryPolicy Retry,
    TimeSpan Timeout,
    TriggerRule TriggerRule,
    IReadOnlyList<string> RequiredLabels,
    int PriorityOffset);

/// <summary>
/// A registered definition's graph as the orchestrator sees it, read back from the canonical JSON stored
/// in <c>workflow_definitions.graph</c> (design.md decision 5). No handler or params types: the
/// orchestrator never needs the definition's assembly.
/// </summary>
public sealed class DispatchGraph
{
    private readonly Dictionary<string, DispatchTask> _tasks;
    private readonly Dictionary<string, List<string>> _downstream;
    private readonly Dictionary<string, IReadOnlyList<string>> _ancestors = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public DispatchGraph(IEnumerable<DispatchTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        _tasks = tasks.ToDictionary(t => t.Key, StringComparer.Ordinal);
        _downstream = _tasks.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var task in _tasks.Values)
        {
            foreach (var upstream in task.DependsOn)
            {
                if (!_downstream.TryGetValue(upstream, out var children))
                    throw new ArgumentException($"Task '{task.Key}' depends on unknown task '{upstream}'.", nameof(tasks));
                children.Add(task.Key);
            }
        }
    }

    public IReadOnlyCollection<DispatchTask> Tasks => _tasks.Values;

    public DispatchTask this[string key] => _tasks.TryGetValue(key, out var task)
        ? task
        : throw new KeyNotFoundException($"The graph has no task '{key}'.");

    /// <summary>Tasks that depend directly on <paramref name="key"/>.</summary>
    public IReadOnlyList<string> Downstream(string key) => _downstream[key];

    /// <summary>Every task <paramref name="key"/> depends on, directly or not (WF-07: the outputs it may read).</summary>
    public IReadOnlyList<string> Ancestors(string key)
    {
        lock (_gate)
        {
            if (_ancestors.TryGetValue(key, out var cached))
                return cached;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(this[key].DependsOn);
        while (pending.TryPop(out var next))
        {
            if (found.Add(next))
            {
                foreach (var upstream in _tasks[next].DependsOn)
                    pending.Push(upstream);
            }
        }

        IReadOnlyList<string> ancestors = [.. found.Order(StringComparer.Ordinal)];
        lock (_gate)
            _ancestors[key] = ancestors;
        return ancestors;
    }

    /// <summary>Reads the canonical graph JSON (<see cref="Workflows.CanonicalGraph"/>), in any property order.</summary>
    public static DispatchGraph Parse(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);

        using var document = JsonDocument.Parse(canonicalJson);
        var tasks = document.RootElement.GetProperty("tasks").EnumerateArray().Select(t =>
        {
            var retry = t.GetProperty("retry");
            return new DispatchTask(
                t.GetProperty("key").GetString()!,
                Strings(t.GetProperty("dependsOn")),
                new RetryPolicy(
                    retry.GetProperty("maxAttempts").GetInt32(),
                    Duration(retry.GetProperty("initialDelay")),
                    retry.GetProperty("multiplier").GetDouble(),
                    Duration(retry.GetProperty("maxDelay"))),
                Duration(t.GetProperty("timeout")),
                Enum.Parse<TriggerRule>(t.GetProperty("triggerRule").GetString()!),
                Strings(t.GetProperty("requiredLabels")),
                t.GetProperty("priorityOffset").GetInt32());
        });

        return new DispatchGraph(tasks.ToList());
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static TimeSpan Duration(JsonElement value) =>
        TimeSpan.ParseExact(value.GetString()!, "c", CultureInfo.InvariantCulture);
}
