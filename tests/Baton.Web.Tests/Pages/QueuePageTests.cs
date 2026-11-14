using Baton.Web.Api;
using Baton.Web.Components.Pages.Queue;
using Baton.Web.Tests.Infrastructure;

namespace Baton.Web.Tests.Pages;

/// <summary>OBS-04: the queue page (M7 task 7.1, design.md decision 25).</summary>
public sealed class QueuePageTests
{
    private static readonly Guid Etl = Guid.CreateVersion7();
    private static readonly Guid Reports = Guid.CreateVersion7();

    [Fact]
    public void Queue_page_lists_groups_with_backlog()
    {
        using var ctx = new ConsoleContext();
        ctx.Api.QueueStats =
        [
            new QueueGroupStats(Etl, "etl", true, 12, (7 * 60) + 12, 3, 4, 2),
            new QueueGroupStats(Reports, "reports", false, 0, null, 0, 0, 0),
        ];

        var cut = ctx.Render<QueuePage>();

        var etl = cut.Find($"tr[data-group='{Etl}']");
        Assert.Contains("etl", etl.TextContent, StringComparison.Ordinal);
        Assert.Equal("paused", etl.QuerySelector(".badge")!.TextContent);
        Assert.Equal("12", etl.QuerySelector("[data-queued]")!.TextContent);
        Assert.Equal("7 min", etl.QuerySelector("[data-oldest-wait]")!.TextContent);
        Assert.Equal("3", etl.QuerySelector("[data-pending-runs]")!.TextContent);
        Assert.Equal("4", etl.QuerySelector("[data-active-leases]")!.TextContent);
        Assert.Equal("2", etl.QuerySelector("[data-lease-expirations]")!.TextContent);
        Assert.Equal(
            [$"runs?hostGroupId={Etl}", $"host-groups/{Etl}/history"],
            etl.QuerySelectorAll("a").Select(a => a.GetAttribute("href")));

        var reports = cut.Find($"tr[data-group='{Reports}']");
        Assert.Null(reports.QuerySelector(".badge"));
        Assert.Equal("—", reports.QuerySelector("[data-oldest-wait]")!.TextContent);
    }

    [Fact]
    public void Queue_page_refreshes_without_reload()
    {
        using var ctx = new ConsoleContext();
        ctx.Api.QueueStats = [new QueueGroupStats(Etl, "etl", false, 12, 420, 0, 1, 0)];
        var cut = ctx.Render<QueuePage>(p => p.Add(c => c.RefreshInterval, TimeSpan.FromMilliseconds(20)));
        Assert.Equal("12", cut.Find("[data-queued]").TextContent);

        ctx.Api.QueueStats = [new QueueGroupStats(Etl, "etl", false, 3, 30, 0, 1, 0)];

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("3", cut.Find("[data-queued]").TextContent);
            Assert.Equal("30 s", cut.Find("[data-oldest-wait]").TextContent);
        }, TimeSpan.FromSeconds(5));
        Assert.True(ctx.Api.QueueStatsReads > 1);
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(42.9, "42 s")]
    [InlineData(420.0, "7 min")]
    [InlineData(7500.0, "2 h 5 min")]
    [InlineData(7200.0, "2 h")]
    [InlineData(90000.0, "1 d 1 h")]
    public void Oldest_wait_is_shown_in_the_largest_unit(double? seconds, string expected) =>
        Assert.Equal(expected, QueuePage.Wait(seconds));
}
