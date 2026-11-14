using Baton.Web.Api;
using Baton.Web.Auth;
using Baton.Web.Components.Pages.Automations;
using Baton.Web.Components.Pages.Hosts;
using Baton.Web.Tests.Infrastructure;

namespace Baton.Web.Tests.Pages;

/// <summary>
/// M6 console (tasks 6.1–6.3): misfire and placement fields and the fire-time preview in the automation editor
/// (AUT-08, AUT-10, ORC-12), and group pause on the hosts page (ORC-13).
/// </summary>
public sealed class SchedulingTests
{
    private static IRenderedComponent<AutomationEditor> NewEditor(ConsoleContext ctx, Guid? id = null) =>
        ctx.Render<AutomationEditor>(p =>
        {
            p.Add(e => e.PreviewDebounce, TimeSpan.Zero);
            if (id is not null)
                p.Add(e => e.Id, id);
        });

    private static void AddCronTrigger(IRenderedComponent<AutomationEditor> cut) =>
        cut.FindAll("button").Single(b => b.TextContent == "Add cron trigger").Click();

    [Fact]
    public void Catch_up_limit_appears_only_for_CatchUp()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        var cut = NewEditor(ctx);
        AddCronTrigger(cut);
        Assert.Equal("fire_once", cut.Find("#misfire-0").GetAttribute("value"));
        Assert.Empty(cut.FindAll("#catch-up-0"));

        cut.Find("#misfire-0").Change("catch_up");
        Assert.Equal("3", cut.Find("#catch-up-0").GetAttribute("value"));

        cut.Find("#misfire-0").Change("skip");
        Assert.Empty(cut.FindAll("#catch-up-0"));
    }

    [Fact]
    public void Placement_is_saved_and_shown_again()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        var cut = NewEditor(ctx);
        cut.Find("#name").Change("Nightly EU");
        cut.Find("#workflow").Change(SampleData.WorkflowKey);
        cut.Find("#group").Change(SampleData.Group.Id.ToString());
        cut.Find("#param-region").Change("EU");
        AddCronTrigger(cut);
        cut.Find("#cron-0").Input("0 2 * * *");
        cut.Find("#misfire-0").Change("catch_up");
        cut.Find("#catch-up-0").Change("5");
        cut.Find("#placement").Change("pinned_run");

        cut.Find("form.editor").Submit();

        var request = Assert.Single(ctx.Api.SavedRequests);
        Assert.Equal("pinned_run", request.PlacementMode);
        var trigger = Assert.Single(request.Triggers!);
        Assert.Equal(("catch_up", (int?)5), (trigger.MisfirePolicy, trigger.CatchUpLimit));

        var reopened = NewEditor(ctx, ctx.Api.Automations.Single().Id);
        Assert.Equal("pinned_run", reopened.Find("#placement").GetAttribute("value"));
        Assert.Equal("5", reopened.Find("#catch-up-0").GetAttribute("value"));
    }

    [Fact]
    public void Preview_lists_the_next_5_fire_times()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        var cut = NewEditor(ctx);
        AddCronTrigger(cut);

        cut.Find("#cron-0").Input("0 2 * * *");
        cut.Find("#tz-0").Input("Asia/Singapore");

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-preview] li").Count));
        Assert.Contains("0 2 * * *|Asia/Singapore", ctx.Api.Previewed);
        Assert.Contains("02:00", cut.Find("[data-preview] li").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_expression_shows_the_message_instead_of_times()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        ctx.Api.Preview = (expression, _) => expression.StartsWith("61", StringComparison.Ordinal)
            ? throw new ApiException(400, "One or more validation errors occurred.",
                new Dictionary<string, string[]> { ["cronExpression"] = ["Not a 5-field cron expression: minute 61."] })
            : new FakeBatonApi().Preview(expression, "UTC");
        var cut = NewEditor(ctx);
        AddCronTrigger(cut);
        cut.Find("#cron-0").Input("0 2 * * *");
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-preview] li").Count));

        cut.Find("#cron-0").Input("61 * * * *");

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-preview] li"));
            Assert.Equal("Not a 5-field cron expression: minute 61.", cut.Find("[data-trigger='0'] .field-error").TextContent);
        });
    }

    [Fact]
    public void Operator_pauses_a_group_from_the_hosts_page()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        ctx.Api.HostGroups.Add(SampleData.Group);
        var cut = ctx.Render<HostList>();
        var row = cut.Find($"tr[data-group='{SampleData.Group.Id}']");
        Assert.Equal("dispatching", row.QuerySelector(".badge")!.TextContent);

        row.QuerySelector("button")!.Click();

        Assert.True(ctx.Api.HostGroups.Single().DispatchPaused);
        row = cut.Find($"tr[data-group='{SampleData.Group.Id}']");
        Assert.Equal("paused", row.QuerySelector(".badge")!.TextContent);
        Assert.Equal("Resume", row.QuerySelector("button")!.TextContent);
    }

    [Fact]
    public void Viewer_sees_paused_state_without_actions()
    {
        using var ctx = new ConsoleContext(Roles.Viewer);
        ctx.Api.HostGroups.Add(SampleData.Group with { DispatchPaused = true });

        var cut = ctx.Render<HostList>();

        var row = cut.Find($"tr[data-group='{SampleData.Group.Id}']");
        Assert.Equal("paused", row.QuerySelector(".badge")!.TextContent);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent is "Pause" or "Resume");
    }
}
