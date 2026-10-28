using System.Globalization;
using System.Text.Json;

namespace Baton.Web.Graph;

/// <summary>A task placed on the canvas: its layer (column) and order within it, and its top-left corner.</summary>
public sealed record DagNode(string Key, int Layer, int Order, double X, double Y);

/// <summary>A dependency drawn from <see cref="From"/> to <see cref="To"/> as an SVG path.</summary>
public sealed record DagEdge(string From, string To, string Path);

/// <summary>
/// Layered left-to-right layout of a workflow DAG (ADR-0012: computed in C#, drawn as SVG). A task's column is its
/// longest path from a root; inside a column tasks are ordered by the mean position of their dependencies, which
/// keeps most edges short and uncrossed for workflow-sized graphs.
/// </summary>
public sealed class DagLayout
{
    public const double NodeWidth = 184;
    public const double NodeHeight = 54;
    public const double ColumnGap = 64;
    public const double RowGap = 22;
    public const double Margin = 8;

    private DagLayout(IReadOnlyList<DagNode> nodes, IReadOnlyList<DagEdge> edges, double width, double height)
    {
        Nodes = nodes;
        Edges = edges;
        Width = width;
        Height = height;
    }

    public IReadOnlyList<DagNode> Nodes { get; }
    public IReadOnlyList<DagEdge> Edges { get; }
    public double Width { get; }
    public double Height { get; }

    /// <summary>Lays out the <c>tasks[].key</c> / <c>tasks[].dependsOn</c> of a canonical graph (WF-09).</summary>
    public static DagLayout FromGraphJson(JsonElement graph)
    {
        var tasks = new List<(string Key, IReadOnlyList<string> DependsOn)>();
        if (graph.ValueKind == JsonValueKind.Object && graph.TryGetProperty("tasks", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var task in array.EnumerateArray())
            {
                var key = task.GetProperty("key").GetString() ?? "";
                var dependsOn = task.TryGetProperty("dependsOn", out var deps) && deps.ValueKind == JsonValueKind.Array
                    ? deps.EnumerateArray().Select(d => d.GetString() ?? "").ToArray()
                    : [];
                tasks.Add((key, dependsOn));
            }
        }
        return Create(tasks);
    }

    /// <summary>Dependencies on unknown tasks are ignored; a cycle (never registered, WF-03) is broken arbitrarily.</summary>
    public static DagLayout Create(IReadOnlyList<(string Key, IReadOnlyList<string> DependsOn)> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var keys = tasks.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var dependsOn = tasks.ToDictionary(t => t.Key, t => t.DependsOn.Where(keys.Contains).Distinct(StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);

        var layer = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        int LayerOf(string key)
        {
            if (layer.TryGetValue(key, out var known))
                return known;
            if (!visiting.Add(key))
                return 0;
            var value = dependsOn[key].Count == 0 ? 0 : dependsOn[key].Max(LayerOf) + 1;
            visiting.Remove(key);
            return layer[key] = value;
        }

        foreach (var task in tasks)
            LayerOf(task.Key);

        var columns = layer.GroupBy(p => p.Value)
            .OrderBy(g => g.Key)
            .Select(g => g.Select(p => p.Key).Order(StringComparer.Ordinal).ToList())
            .ToList();

        // Downward sweep: order each column by the mean row of its tasks' dependencies.
        var row = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var ordered = column
                .Select((key, index) => (Key: key, Weight: dependsOn[key].Count == 0 ? index : dependsOn[key].Average(d => row[d])))
                .OrderBy(t => t.Weight)
                .ThenBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => t.Key)
                .ToList();
            column.Clear();
            column.AddRange(ordered);
            for (var i = 0; i < column.Count; i++)
                row[column[i]] = i;
        }

        var tallest = columns.Count == 0 ? 0 : columns.Max(c => c.Count);
        var height = Margin * 2 + (tallest == 0 ? 0 : tallest * NodeHeight + (tallest - 1) * RowGap);
        var width = Margin * 2 + (columns.Count == 0 ? 0 : columns.Count * NodeWidth + (columns.Count - 1) * ColumnGap);

        var nodes = new List<DagNode>();
        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            var columnHeight = column.Count * NodeHeight + (column.Count - 1) * RowGap;
            var top = Margin + ((height - Margin * 2) - columnHeight) / 2;
            for (var i = 0; i < column.Count; i++)
                nodes.Add(new DagNode(column[i], c, i, Margin + c * (NodeWidth + ColumnGap), top + i * (NodeHeight + RowGap)));
        }

        var byKey = nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
        var edges = new List<DagEdge>();
        foreach (var node in nodes)
        {
            foreach (var dependency in dependsOn[node.Key].Order(StringComparer.Ordinal))
                edges.Add(new DagEdge(dependency, node.Key, EdgePath(byKey[dependency], node)));
        }

        return new DagLayout(nodes, edges, width, height);
    }

    private static string EdgePath(DagNode from, DagNode to)
    {
        var x1 = from.X + NodeWidth;
        var y1 = from.Y + NodeHeight / 2;
        var x2 = to.X;
        var y2 = to.Y + NodeHeight / 2;
        var bend = Math.Max(24, (x2 - x1) / 2);
        return string.Create(CultureInfo.InvariantCulture,
            $"M {x1:0.#} {y1:0.#} C {x1 + bend:0.#} {y1:0.#}, {x2 - bend:0.#} {y2:0.#}, {x2:0.#} {y2:0.#}");
    }
}
