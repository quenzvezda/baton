using Baton.Web.Api;
using Baton.Web.Auth;
using Baton.Web.Components.Pages.Runs;
using Baton.Web.Tests.Infrastructure;

namespace Baton.Web.Tests.Pages;

/// <summary>OBS-01, OBS-02, NFR-Security: runs list, run detail and its role-aware actions (M5 tasks 3.4, 3.5, 3.8).</summary>
public sealed class RunPagesTests
{
    [Fact]
    public void Filtering_the_runs_list_to_failed_lists_only_failed_runs()
    {
        using var ctx = new ConsoleContext();
        SampleData.AddWorkflow(ctx.Api);
        var failed = AddRun(ctx.Api, "failed");
        AddRun(ctx.Api, "succeeded");
        AddRun(ctx.Api, "running");
        var cut = ctx.Render<RunList>();
        Assert.Equal(3, cut.FindAll("tbody tr").Count);

        cut.Find("input[data-status=failed]").Change(true);
        cut.Find("form.filters").Submit();

        Assert.Equal(["failed"], ctx.Api.RunQueries[^1].Statuses);
        var row = Assert.Single(cut.FindAll("tbody tr"));
        Assert.Equal(failed.ToString(), row.GetAttribute("data-run"));
        Assert.Equal("failed", row.QuerySelector(".badge")!.TextContent);
    }

    [Fact]
    public void Runs_list_sends_the_date_filters_with_the_to_day_included()
    {
        using var ctx = new ConsoleContext();
        var cut = ctx.Render<RunList>();

        cut.Find("#f-from").Change("2026-10-01");
        cut.Find("#f-to").Change("2026-10-07");
        cut.Find("form.filters").Submit();

        var query = ctx.Api.RunQueries[^1];
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), query.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero), query.To);
    }

    [Fact]
    public void Failed_run_shows_the_failed_task_with_its_error_and_skipped_tasks_as_skipped()
    {
        using var ctx = new ConsoleContext();
        var runId = Guid.CreateVersion7();
        ctx.Api.RunDetails[runId] = SampleData.FailedRun(runId);

        var cut = ctx.Render<RunDetailPage>(p => p.Add(c => c.Id, runId));

        var failed = cut.Find("g[data-task=extract-customers]");
        Assert.Contains("state-failed", failed.ClassList);
        Assert.Contains("failed", failed.TextContent, StringComparison.Ordinal);
        foreach (var skipped in new[] { "build-report", "publish-report" })
        {
            var node = cut.Find($"g[data-task={skipped}]");
            Assert.Contains("state-skipped", node.ClassList);
            Assert.Contains("skipped", node.TextContent, StringComparison.Ordinal);
        }

        // The failed task opens by itself, with every attempt, its host and the error.
        var panel = cut.Find("section.task-panel");
        Assert.Equal("extract-customers", panel.GetAttribute("data-task"));
        Assert.Equal(["1", "2"], panel.QuerySelectorAll("tr[data-attempt]").Select(r => r.GetAttribute("data-attempt")));
        Assert.Contains("agent-2", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("TimeoutException: warehouse did not answer", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("IOException: reset", panel.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Selecting_a_task_in_the_dag_shows_its_output()
    {
        using var ctx = new ConsoleContext();
        var runId = Guid.CreateVersion7();
        ctx.Api.RunDetails[runId] = SampleData.FailedRun(runId);
        var cut = ctx.Render<RunDetailPage>(p => p.Add(c => c.Id, runId));

        cut.Find("g[data-task=extract-orders]").Click();

        Assert.Equal("extract-orders", cut.Find("section.task-panel").GetAttribute("data-task"));
        Assert.Contains("\"count\": 12", cut.Find("section.task-panel pre.json").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Purged_run_is_reported_as_not_available()
    {
        using var ctx = new ConsoleContext();

        var cut = ctx.Render<RunDetailPage>(p => p.Add(c => c.Id, Guid.CreateVersion7()));

        Assert.Equal("Run not available", cut.Find("h1").TextContent);
    }

    [Fact]
    public void Viewer_sees_no_cancel_action_on_a_running_run()
    {
        using var ctx = new ConsoleContext(Roles.Viewer);
        var runId = Guid.CreateVersion7();
        ctx.Api.RunDetails[runId] = SampleData.FailedRun(runId) with { Status = "running" };

        var cut = ctx.Render<RunDetailPage>(p => p.Add(c => c.Id, runId));

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Cancel", StringComparison.Ordinal));
    }

    [Fact]
    public void Operator_can_cancel_a_running_run()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        var runId = Guid.CreateVersion7();
        ctx.Api.RunDetails[runId] = SampleData.FailedRun(runId) with { Status = "running" };
        var cut = ctx.Render<RunDetailPage>(p => p.Add(c => c.Id, runId));

        cut.Find("button.danger").Click();

        Assert.Equal([runId], ctx.Api.Cancelled);
        Assert.Equal("cancelling", cut.Find("h1 .badge").TextContent);
        Assert.Empty(cut.FindAll("button.danger"));
    }

    private static Guid AddRun(FakeBatonApi api, string status)
    {
        var id = Guid.CreateVersion7();
        api.Runs.Add(new RunSummary(id, null, "Nightly EU", SampleData.WorkflowKey, 2, status, 50, SampleData.Group.Id, SampleData.Group.Name,
            "cron", DateTimeOffset.UtcNow, null, null));
        return id;
    }
}
