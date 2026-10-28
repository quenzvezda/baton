using System.Text.Json;
using Baton.Web.Api;
using Baton.Web.Auth;
using Baton.Web.Components.Pages.Automations;
using Baton.Web.Tests.Infrastructure;

namespace Baton.Web.Tests.Pages;

/// <summary>AUT-03: automation editor with a form generated from the params schema (M5 task 3.3).</summary>
public sealed class AutomationEditorTests
{
    [Fact]
    public void Selecting_the_workflow_shows_its_parameter_form_with_region_required()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        var cut = ctx.Render<AutomationEditor>();

        cut.Find("#workflow").Change(SampleData.WorkflowKey);

        Assert.Equal(["region", "reportDate", "recipients"], cut.FindAll("[data-param]").Select(f => f.GetAttribute("data-param")));
        Assert.NotNull(cut.Find("[data-param=region] label .required"));
        Assert.Empty(cut.FindAll("[data-param=reportDate] label .required"));
        Assert.Equal("date", cut.Find("#param-reportDate").GetAttribute("type"));
        Assert.Equal("textarea", cut.Find("#param-recipients").TagName.ToLowerInvariant());
    }

    [Fact]
    public void Missing_required_parameter_is_reported_without_calling_the_api()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        var cut = ctx.Render<AutomationEditor>();
        cut.Find("#name").Change("Nightly EU");
        cut.Find("#workflow").Change(SampleData.WorkflowKey);
        cut.Find("#group").Change(SampleData.Group.Id.ToString());

        cut.Find("form.editor").Submit();

        Assert.Equal("Required.", cut.Find("[data-param=region] .field-error").TextContent);
        Assert.Empty(ctx.Api.SavedRequests);
    }

    [Fact]
    public void Valid_form_is_sent_as_typed_json_parameters()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        var cut = ctx.Render<AutomationEditor>();
        cut.Find("#name").Change("Nightly EU");
        cut.Find("#workflow").Change(SampleData.WorkflowKey);
        cut.Find("#group").Change(SampleData.Group.Id.ToString());
        cut.Find("#param-region").Change("EU");
        cut.Find("#param-recipients").Change("a@example.com\n\nb@example.com");

        cut.Find("form.editor").Submit();

        var request = Assert.Single(ctx.Api.SavedRequests);
        Assert.Equal(SampleData.WorkflowKey, request.WorkflowKey);
        Assert.Equal(SampleData.Group.Id, request.HostGroupId);
        var parameters = request.Parameters!.Value;
        Assert.Equal("EU", parameters.GetProperty("region").GetString());
        Assert.Equal(JsonValueKind.Null, parameters.GetProperty("reportDate").ValueKind);
        Assert.Equal(["a@example.com", "b@example.com"], parameters.GetProperty("recipients").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public void Manual_trigger_can_be_added_once_and_is_sent_with_its_kind()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        var cut = ctx.Render<AutomationEditor>();
        cut.Find("#name").Change("Ad hoc");
        cut.Find("#workflow").Change(SampleData.WorkflowKey);
        cut.Find("#group").Change(SampleData.Group.Id.ToString());
        cut.Find("#param-region").Change("EU");

        cut.FindAll("button").Single(b => b.TextContent == "Add manual trigger").Click();
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent == "Add manual trigger");
        cut.Find("form.editor").Submit();

        Assert.Equal(["manual"], Assert.Single(ctx.Api.SavedRequests).Triggers!.Select(t => t.Kind));
    }

    [Fact]
    public void Server_error_is_shown_next_to_its_field_and_other_inputs_are_kept()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        ctx.Api.RejectNextSave = new ApiException(400, "One or more validation errors occurred.", new Dictionary<string, string[]>
        {
            ["parameters.region"] = ["Value should match one of the values specified by the enum"],
            ["name"] = ["An automation with this name exists."],
        });
        var cut = ctx.Render<AutomationEditor>();
        cut.Find("#name").Change("Nightly EU");
        cut.Find("#workflow").Change(SampleData.WorkflowKey);
        cut.Find("#group").Change(SampleData.Group.Id.ToString());
        cut.Find("#param-region").Change("Mars");
        cut.Find("#param-recipients").Change("a@example.com");

        cut.Find("form.editor").Submit();

        Assert.Equal("Value should match one of the values specified by the enum", cut.Find("[data-param=region] .field-error").TextContent);
        Assert.Equal("An automation with this name exists.", cut.Find("#name + .field-error").TextContent);
        Assert.Equal("Mars", cut.Find("#param-region").GetAttribute("value"));
        Assert.Equal("a@example.com", cut.Find("#param-recipients").GetAttribute("value") ?? cut.Find("#param-recipients").TextContent);
        Assert.Equal("Nightly EU", cut.Find("#name").GetAttribute("value"));
    }

    [Fact]
    public void Viewer_sees_the_automation_read_only()
    {
        using var ctx = new ConsoleContext(Roles.Viewer);
        SampleData.AddWorkflow(ctx.Api);
        var automation = new Automation(Guid.CreateVersion7(), "Nightly EU", null, true, "ops", SampleData.WorkflowKey, null,
            SampleData.Parse("""{"region":"EU","reportDate":null,"recipients":["a@example.com"]}"""), SampleData.Group.Id, [], 50, 1, "skip",
            null, DateTimeOffset.UtcNow, 3, []);
        ctx.Api.Automations.Add(automation);

        var cut = ctx.Render<AutomationEditor>(p => p.Add(c => c.Id, automation.Id));

        Assert.Equal("EU", cut.Find("#param-region").GetAttribute("value"));
        Assert.All(cut.FindAll("fieldset"), f => Assert.True(f.HasAttribute("disabled")));
        Assert.Empty(cut.FindAll("button[type=submit]"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent is "Run now" or "Disable" or "Delete…");
    }
}
