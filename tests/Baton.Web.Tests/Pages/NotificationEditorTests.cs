using Baton.Web.Api;
using Baton.Web.Auth;
using Baton.Web.Components.Pages.Automations;
using Baton.Web.Tests.Infrastructure;

namespace Baton.Web.Tests.Pages;

/// <summary>OBS-05: notification channels in the automation editor (M7 task 7.3).</summary>
public sealed class NotificationEditorTests
{
    private const string HookUrl = "https://hooks.example.com/services/T000/B000/token-in-the-path";

    [Fact]
    public void Webhook_channel_secret_is_shown_once_after_save()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        var cut = ctx.Render<AutomationEditor>();
        FillRequired(cut);
        cut.FindAll("button").Single(b => b.TextContent == "Add webhook channel").Click();
        cut.Find("#channel-url-0").Change(HookUrl);

        cut.Find("form.editor").Submit();

        var sent = Assert.Single(Assert.Single(ctx.Api.SavedRequests).Notifications!);
        Assert.Equal(("webhook", HookUrl, (Guid?)null), (sent.Kind, sent.Url, sent.Id));
        Assert.Equal(["failed"], sent.On);
        var secret = Assert.Single(cut.FindAll("[data-signing-secret]"));
        Assert.StartsWith("whsec_test_", secret.QuerySelector("code")!.TextContent, StringComparison.Ordinal);
        // The saved channel shows only the host; the URL input is empty again.
        Assert.True(string.IsNullOrEmpty(cut.Find("#channel-url-0").GetAttribute("value")));
        Assert.Contains("https://hooks.example.com", cut.Find("[data-channel='0'] [data-current-host]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("token-in-the-path", cut.Markup, StringComparison.Ordinal);

        // Saving again keeps the channel and its URL, and no second secret is issued.
        cut.Find("form.editor").Submit();
        var kept = Assert.Single(ctx.Api.SavedRequests[^1].Notifications!);
        Assert.NotNull(kept.Id);
        Assert.Null(kept.Url);
        Assert.Single(cut.FindAll("[data-signing-secret]"));

        // Reopened, the secret is gone for good.
        var reopened = ctx.Render<AutomationEditor>(p => p.Add(c => c.Id, ctx.Api.Automations.Single().Id));
        Assert.Empty(reopened.FindAll("[data-signing-secret]"));
        Assert.DoesNotContain("whsec_", reopened.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Reopened_channel_shows_only_the_host()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        var channelId = Guid.CreateVersion7();
        var automation = Saved() with
        {
            Notifications = [new NotificationInfo(channelId, "webhook", ["failed", "timed_out"], null, "https://hooks.example.com")],
        };
        ctx.Api.Automations.Add(automation);

        var cut = ctx.Render<AutomationEditor>(p => p.Add(c => c.Id, automation.Id));

        var channel = cut.Find("[data-channel='0']");
        Assert.True(string.IsNullOrEmpty(cut.Find("#channel-url-0").GetAttribute("value")));
        Assert.Contains("https://hooks.example.com", channel.QuerySelector("[data-current-host]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("leave empty to keep", channel.TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("#channel-failed-0").HasAttribute("checked"));
        Assert.True(cut.Find("#channel-timed-out-0").HasAttribute("checked"));
        Assert.Empty(cut.FindAll("[data-signing-secret]"));

        cut.Find("form.editor").Submit();

        var sent = Assert.Single(Assert.Single(ctx.Api.SavedRequests).Notifications!);
        Assert.Equal((channelId, "webhook", (string?)null), (sent.Id!.Value, sent.Kind, sent.Url));
        Assert.Equal(["failed", "timed_out"], sent.On);
    }

    [Fact]
    public void Channel_error_is_shown_next_to_the_channel()
    {
        using var ctx = new ConsoleContext(Roles.Operator);
        SampleData.AddWorkflow(ctx.Api);
        ctx.Api.RejectNextSave = new ApiException(400, "One or more validation errors occurred.", new Dictionary<string, string[]>
        {
            ["notifications[1].recipients"] = ["Add 1 to 20 recipients."],
        });
        var cut = ctx.Render<AutomationEditor>();
        FillRequired(cut);
        cut.FindAll("button").Single(b => b.TextContent == "Add email channel").Click();
        cut.Find("#channel-recipients-0").Change("ops@example.com,\n oncall@example.com");
        cut.FindAll("button").Single(b => b.TextContent == "Add email channel").Click();

        cut.Find("form.editor").Submit();

        var sent = Assert.Single(ctx.Api.SavedRequests).Notifications!;
        Assert.Equal(["ops@example.com", "oncall@example.com"], sent[0].Recipients);
        Assert.Empty(sent[1].Recipients!);
        Assert.Equal("Add 1 to 20 recipients.", cut.Find("[data-channel='1'] .field-error").TextContent);
        Assert.Empty(cut.FindAll("[data-channel='0'] .field-error"));
        var kept = cut.Find("#channel-recipients-0");
        Assert.Contains("ops@example.com", kept.GetAttribute("value") ?? kept.TextContent, StringComparison.Ordinal);
    }

    private static void FillRequired(IRenderedComponent<AutomationEditor> cut)
    {
        cut.Find("#name").Change("Nightly EU");
        cut.Find("#workflow").Change(SampleData.WorkflowKey);
        cut.Find("#group").Change(SampleData.Group.Id.ToString());
        cut.Find("#param-region").Change("EU");
        cut.Find("#param-recipients").Change("a@example.com");
    }

    private static Automation Saved() => new(Guid.CreateVersion7(), "Nightly EU", null, true, "ops", SampleData.WorkflowKey, null,
        SampleData.Parse("""{"region":"EU","reportDate":null,"recipients":["a@example.com"]}"""), SampleData.Group.Id, [], 50, 1, "skip",
        null, DateTimeOffset.UtcNow, 3, []);
}
