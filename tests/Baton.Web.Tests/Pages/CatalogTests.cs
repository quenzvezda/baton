using Baton.Web.Components.Pages.Workflows;
using Baton.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Web.Tests.Pages;

/// <summary>WF-09: workflow catalog page (M5 task 3.2).</summary>
public sealed class CatalogTests
{
    [Fact]
    public void Catalog_lists_every_workflow_with_its_latest_version()
    {
        using var ctx = new ConsoleContext();
        SampleData.AddWorkflow(ctx.Api);

        var cut = ctx.Render<WorkflowCatalog>();

        var row = Assert.Single(cut.FindAll("tbody tr"));
        Assert.Contains("Nightly sales report", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("v2", row.TextContent, StringComparison.Ordinal);
        Assert.Equal("workflows/nightly-sales-report", row.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void Detail_renders_the_example_dag_and_its_parameters()
    {
        using var ctx = new ConsoleContext();
        SampleData.AddWorkflow(ctx.Api);

        var cut = ctx.Render<WorkflowDetail>(p => p.Add(c => c.Key, SampleData.WorkflowKey));

        var nodes = cut.FindAll("g.dag-node").Select(n => n.GetAttribute("data-task")).Order(StringComparer.Ordinal);
        Assert.Equal(["build-report", "extract-customers", "extract-orders", "notify-failure", "publish-report"], nodes);
        Assert.Equal(6, cut.FindAll("path.dag-edge").Count);
        Assert.Contains(cut.FindAll("path.dag-edge"), e => e.GetAttribute("data-from") == "build-report" && e.GetAttribute("data-to") == "publish-report");
        // Roots are left of the tasks that depend on them.
        Assert.True(X(cut, "extract-orders") < X(cut, "build-report") && X(cut, "build-report") < X(cut, "publish-report"));

        var region = cut.Find("tr[data-param=region]");
        Assert.Contains("required", region.TextContent, StringComparison.Ordinal);
        Assert.Contains("optional", cut.Find("tr[data-param=reportDate]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Latest_version_is_shown_and_earlier_versions_can_be_selected()
    {
        using var ctx = new ConsoleContext();
        SampleData.AddWorkflow(ctx.Api);
        var navigation = ctx.Services.GetRequiredService<NavigationManager>();

        var cut = ctx.Render<WorkflowDetail>(p => p.Add(c => c.Key, SampleData.WorkflowKey));
        var versions = cut.Find("select[aria-label=Version]");

        Assert.Equal("2", versions.GetAttribute("value"));
        Assert.Equal(["2", "1"], versions.QuerySelectorAll("option").Select(o => o.GetAttribute("value")));

        versions.Change("1");
        Assert.EndsWith("workflows/nightly-sales-report?version=1", navigation.Uri, StringComparison.Ordinal);

        var v1 = ctx.Render<WorkflowDetail>(p => p.Add(c => c.Key, SampleData.WorkflowKey)); // ?version=1 from the URI
        Assert.Equal(["build-report"], v1.FindAll("g.dag-node").Select(n => n.GetAttribute("data-task")));
    }

    [Fact]
    public void Unknown_workflow_says_so()
    {
        using var ctx = new ConsoleContext();

        var cut = ctx.Render<WorkflowDetail>(p => p.Add(c => c.Key, "nope"));

        Assert.Contains("No workflow is registered under this key", cut.Markup, StringComparison.Ordinal);
    }

    private static double X(IRenderedComponent<WorkflowDetail> cut, string task)
    {
        var transform = cut.Find($"g[data-task={task}]").GetAttribute("transform")!; // translate(x y)
        return double.Parse(transform["translate(".Length..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
    }
}
