using Baton.Web.Api;
using Baton.Web.Components.Shared;
using Baton.Web.Tests.Infrastructure;

namespace Baton.Web.Tests.Pages;

/// <summary>OBS-03: log viewer with live follow, against a fake stream (M5 task 3.6).</summary>
public sealed class LogViewerTests
{
    private static readonly Guid RunId = Guid.CreateVersion7();

    [Fact]
    public void Logs_of_a_finished_attempt_are_listed_in_order()
    {
        using var ctx = new ConsoleContext();
        ctx.Api.Logs[(RunId, "build-report", 1)] = new LogPage(1, false, [Line(1, "Information", "start"), Line(2, "Error", "boom")], null);

        var cut = Render(ctx);

        Assert.Equal(["start", "boom"], cut.FindAll(".log-message").Select(m => m.TextContent));
        Assert.Contains("level-error", cut.FindAll(".log-line")[1].ClassList);
        Assert.Empty(cut.FindAll(".follow"));
    }

    [Fact]
    public async Task New_lines_of_a_running_attempt_appear_without_reloading()
    {
        using var ctx = new ConsoleContext();
        ctx.Api.Logs[(RunId, "build-report", 1)] = new LogPage(1, true, [Line(1, "Information", "before")], null);
        var finished = false;
        var cut = Render(ctx, () => finished = true);
        Assert.Equal("live", cut.Find(".logs-header .badge").TextContent);

        await ctx.Api.LiveLines.Writer.WriteAsync(Line(2, "Warning", "live line"));

        cut.WaitForAssertion(() => Assert.Equal(["before", "live line"], cut.FindAll(".log-message").Select(m => m.TextContent)));

        ctx.Api.LiveLines.Writer.Complete();
        cut.WaitForAssertion(() => Assert.True(finished));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".follow")));
    }

    [Fact]
    public async Task Pausing_follow_stops_new_lines()
    {
        using var ctx = new ConsoleContext();
        ctx.Api.Logs[(RunId, "build-report", 1)] = new LogPage(1, true, [], null);
        var cut = Render(ctx);

        cut.Find(".follow input").Change(false);
        await ctx.Api.LiveLines.Writer.WriteAsync(Line(1, "Information", "while paused"));

        Assert.Equal("paused", cut.Find(".logs-header .badge").TextContent);
        Assert.Empty(cut.FindAll(".log-message"));
    }

    private static IRenderedComponent<LogViewer> Render(ConsoleContext ctx, Action? onFinished = null) =>
        ctx.Render<LogViewer>(p => p
            .Add(c => c.RunId, RunId)
            .Add(c => c.TaskKey, "build-report")
            .Add(c => c.Attempt, 1)
            .Add(c => c.OnFinished, onFinished ?? (() => { })));

    private static LogLine Line(int seq, string level, string message) => new(seq, DateTimeOffset.UtcNow, level, message);
}
