using Baton.Web.Graph;
using Baton.Web.Tests.Infrastructure;

namespace Baton.Web.Tests.Graph;

/// <summary>WF-09, OBS-02: the C# DAG layout behind the catalog and run detail (ADR-0012).</summary>
public sealed class DagLayoutTests
{
    [Fact]
    public void Columns_follow_the_longest_path_from_a_root()
    {
        var layout = DagLayout.FromGraphJson(SampleData.Graph);

        var layers = layout.Nodes.ToDictionary(n => n.Key, n => n.Layer);
        Assert.Equal(0, layers["extract-orders"]);
        Assert.Equal(0, layers["extract-customers"]);
        Assert.Equal(1, layers["build-report"]);
        Assert.Equal(2, layers["publish-report"]);
        Assert.Equal(2, layers["notify-failure"]);
        Assert.Equal(6, layout.Edges.Count);
    }

    [Fact]
    public void Nodes_never_overlap_and_fit_the_canvas()
    {
        var layout = DagLayout.FromGraphJson(SampleData.Graph);

        foreach (var a in layout.Nodes)
        {
            Assert.InRange(a.X + DagLayout.NodeWidth, 0, layout.Width);
            Assert.InRange(a.Y + DagLayout.NodeHeight, 0, layout.Height);
            foreach (var b in layout.Nodes.Where(b => b != a))
            {
                var apart = a.X + DagLayout.NodeWidth <= b.X || b.X + DagLayout.NodeWidth <= a.X
                    || a.Y + DagLayout.NodeHeight <= b.Y || b.Y + DagLayout.NodeHeight <= a.Y;
                Assert.True(apart, $"{a.Key} overlaps {b.Key}");
            }
        }
    }

    [Fact]
    public void Edges_run_from_the_right_of_a_dependency_to_the_left_of_its_dependent()
    {
        var layout = DagLayout.Create([("a", []), ("b", ["a"])]);

        var edge = Assert.Single(layout.Edges);
        Assert.Equal(("a", "b"), (edge.From, edge.To));
        Assert.StartsWith($"M {DagLayout.Margin + DagLayout.NodeWidth:0.#} ", edge.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_dependencies_and_empty_graphs_are_tolerated()
    {
        Assert.Empty(DagLayout.Create([]).Nodes);
        var layout = DagLayout.Create([("a", ["missing"])]);
        Assert.Equal(0, Assert.Single(layout.Nodes).Layer);
        Assert.Empty(layout.Edges);
    }
}
