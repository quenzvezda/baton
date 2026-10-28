using Baton.Web.Api;
using Baton.Web.Auth;
using Baton.Web.Components.Layout;
using Baton.Web.Components.Pages.Automations;
using Baton.Web.Components.Pages.Hosts;
using Baton.Web.Tests.Infrastructure;
using Microsoft.AspNetCore.Components;

namespace Baton.Web.Tests.Pages;

/// <summary>HST-06 hosts page (M5 task 3.7) and role-aware actions across pages (task 3.8, NFR-Security).</summary>
public sealed class HostAndRoleTests
{
    [Fact]
    public void Hosts_page_shows_status_slots_current_tasks_and_heartbeat()
    {
        using var ctx = new ConsoleContext();
        var host = AddHost(ctx.Api);

        var cut = ctx.Render<HostList>();

        var row = cut.Find($"tr[data-host='{host.Id}']");
        Assert.Contains("agent-1", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("online", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 / 4", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("build-report", row.TextContent, StringComparison.Ordinal);
        Assert.Equal($"runs/{host.CurrentTasks[0].WorkflowRunId}", row.QuerySelector("a")!.GetAttribute("href"));
        Assert.NotNull(row.QuerySelector("time"));
    }

    [Fact]
    public void Operator_drains_a_host_from_the_page_and_sees_it_draining()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        var host = AddHost(ctx.Api);
        var cut = ctx.Render<HostList>();

        cut.Find($"tr[data-host='{host.Id}'] button").Click();

        Assert.Equal([host.Id], ctx.Api.Drained);
        var row = cut.Find($"tr[data-host='{host.Id}']");
        Assert.Equal("draining", row.QuerySelector(".badge")!.TextContent);
        Assert.Equal("Undrain", row.QuerySelector("button")!.TextContent);
    }

    [Fact]
    public void Viewer_sees_no_drain_action()
    {
        using var ctx = new ConsoleContext(Roles.Viewer);
        AddHost(ctx.Api);

        var cut = ctx.Render<HostList>();

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent is "Drain" or "Undrain");
    }

    [Fact]
    public void Viewer_sees_no_mutating_actions_on_automations()
    {
        using var ctx = new ConsoleContext(Roles.Viewer);
        ctx.Api.Automations.Add(new Automation(Guid.CreateVersion7(), "Nightly EU", null, true, "ops", SampleData.WorkflowKey, null,
            SampleData.Parse("{}"), SampleData.Group.Id, [], 50, 1, "skip", null, DateTimeOffset.UtcNow, 1, []));

        var cut = ctx.Render<AutomationList>();

        Assert.Empty(cut.FindAll("a[href='automations/new']"));
        Assert.Empty(cut.FindAll("td.actions button"));
    }

    [Fact]
    public void Operator_can_create_run_and_toggle_automations()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        ctx.Api.Automations.Add(new Automation(Guid.CreateVersion7(), "Nightly EU", null, true, "ops", SampleData.WorkflowKey, null,
            SampleData.Parse("{}"), SampleData.Group.Id, [], 50, 1, "skip", null, DateTimeOffset.UtcNow, 1, []));
        var cut = ctx.Render<AutomationList>();

        Assert.NotNull(cut.Find("a[href='automations/new']"));
        cut.FindAll("td.actions button").Single(b => b.TextContent == "Disable").Click();

        Assert.False(ctx.Api.Automations[0].Enabled);
        Assert.Contains(cut.FindAll("td.actions button"), b => b.TextContent == "Enable");
    }

    [Fact]
    public void Person_without_a_baton_role_is_told_so_instead_of_seeing_pages()
    {
        using var ctx = new ConsoleContext();
        ctx.Api.User = null;

        var cut = ctx.Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "secret page"))));

        Assert.Equal("No access", cut.Find("h1").TextContent);
        Assert.DoesNotContain("secret page", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_shows_the_signed_in_person_and_role()
    {
        using var ctx = new ConsoleContext(Roles.Operator);

        var cut = ctx.Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddContent(0, "page"))));

        Assert.Equal("Test Operator", cut.Find(".user-name").TextContent);
        Assert.Equal("Operator", cut.Find(".user-role").TextContent);
        Assert.Contains("page", cut.Find("main").TextContent, StringComparison.Ordinal);
    }

    private static HostInfo AddHost(FakeBatonApi api)
    {
        var host = new HostInfo(Guid.CreateVersion7(), "agent-1", SampleData.Group.Id, SampleData.Group.Name, "online", false, ["large-memory"], 4, 1,
            [new HostTask(Guid.CreateVersion7(), Guid.CreateVersion7(), "build-report", 1, "running")], "1.0.0", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(-1));
        api.Hosts.Add(host);
        return host;
    }
}
