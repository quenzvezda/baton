using System.Globalization;
using System.Text.Json;
using Baton.Web.Api;
using Baton.Web.Components.Pages.History;
using Baton.Web.Tests.Infrastructure;

namespace Baton.Web.Tests.Pages;

/// <summary>AUT-11: the history view of an automation and of a host group (M7 task 7.4).</summary>
public sealed class HistoryTests
{
    private static readonly DateTimeOffset ChangedAt = new(2026, 11, 14, 9, 30, 5, TimeSpan.Zero);

    [Fact]
    public void Automation_history_shows_changed_fields()
    {
        using var ctx = new ConsoleContext();
        var id = Guid.CreateVersion7();
        ctx.Api.Automations.Add(new Automation(id, "Nightly EU", null, true, "ops", SampleData.WorkflowKey, null, SampleData.Parse("{}"),
            SampleData.Group.Id, [], 60, 1, "skip", null, ChangedAt, 4, []));
        var before = AutomationBody(id, 50, 3, ChangedAt.AddDays(-1));
        ctx.Api.Audit[("automation", id.ToString())] =
        [
            Entry(2, ChangedAt, "update", before, AutomationBody(id, 60, 4, ChangedAt)),
            Entry(1, ChangedAt.AddDays(-1), "create", null, before),
        ];

        var cut = ctx.Render<AutomationHistory>(p => p.Add(c => c.Id, id).Add(c => c.PageSize, 1));

        Assert.Contains("Nightly EU", cut.Find("h1").TextContent, StringComparison.Ordinal);
        var newest = Assert.Single(cut.FindAll("li[data-entry]"));
        Assert.Equal("update", newest.QuerySelector(".history-action")!.TextContent);
        Assert.Equal("Ada Operator", newest.QuerySelector(".history-actor")!.TextContent);
        Assert.Equal("2026-11-14 09:30:05 UTC", newest.QuerySelector("time")!.TextContent);
        // Only the field that changed: bookkeeping (version, updatedAt) and unchanged nested values are left out.
        var change = Assert.Single(newest.QuerySelectorAll("[data-field]"));
        Assert.Equal("priority", change.GetAttribute("data-field"));
        Assert.Equal("priority: 50 → 60", change.TextContent);

        cut.FindAll("button").Single(b => b.TextContent == "Load more").Click();

        Assert.Equal(["update", "create"], cut.FindAll("li[data-entry]").Select(e => e.GetAttribute("data-action")));
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void Group_history_shows_the_pause()
    {
        using var ctx = new ConsoleContext();
        var group = SampleData.Group with { Name = "etl", DispatchPaused = true };
        ctx.Api.HostGroups.Add(group);
        ctx.Api.Audit[("host_group", group.Id.ToString())] =
        [
            Entry(7, ChangedAt, "pause", GroupBody(group.Id, false), GroupBody(group.Id, true)),
            Entry(3, ChangedAt.AddDays(-2), "create", null, GroupBody(group.Id, false)),
        ];

        var cut = ctx.Render<HostGroupHistory>(p => p.Add(c => c.Id, group.Id));

        Assert.Contains("etl", cut.Find("h1").TextContent, StringComparison.Ordinal);
        var entries = cut.FindAll("li[data-entry]");
        Assert.Equal(["pause", "create"], entries.Select(e => e.GetAttribute("data-action")));
        var newest = entries[0];
        Assert.Equal("pause", newest.QuerySelector(".history-action")!.TextContent);
        Assert.Equal("Ada Operator", newest.QuerySelector(".history-actor")!.TextContent);
        Assert.Equal("2026-11-14 09:30:05 UTC", newest.QuerySelector("time")!.TextContent);
        Assert.Equal("dispatchPaused: false → true", Assert.Single(newest.QuerySelectorAll("[data-field]")).TextContent);
        Assert.Contains("Created as", entries[1].TextContent, StringComparison.Ordinal);
    }

    private static AuditEntry Entry(long id, DateTimeOffset at, string action, JsonElement? before, JsonElement? after) =>
        new(id, at, new AuditActor("sub-ada", "Ada Operator"), "automation", "", action, before, after);

    private static JsonElement AutomationBody(Guid id, int priority, int version, DateTimeOffset updatedAt) => SampleData.Parse($$"""
        {"id":"{{id}}","name":"Nightly EU","priority":{{priority}},"version":{{version}},
         "updatedAt":"{{updatedAt.ToString("O", CultureInfo.InvariantCulture)}}",
         "triggers":[{"id":"7f2c","kind":"cron","cronExpression":"0 2 * * *"}],"notifications":[]}
        """);

    private static JsonElement GroupBody(Guid id, bool paused) => SampleData.Parse($$"""
        {"id":"{{id}}","name":"etl","maxConcurrentRuns":null,"maxConcurrentTasks":8,"leaseSeconds":60,
         "dispatchPaused":{{(paused ? "true" : "false")}}}
        """);
}
