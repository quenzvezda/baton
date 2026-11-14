using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Baton.ControlPlane.Api.Audit;
using Baton.ControlPlane.Api.Automations;
using Baton.ControlPlane.Api.Security;
using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Integration.Tests.Automations;

/// <summary>OBS-05 "Notification channels per automation" and "Webhook channel secrets" (M7 task 5.2).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class NotificationChannelApiTests(PostgresFixture pg)
{
    private const string HookUrl = "https://hooks.example.com/services/T0/B0/abc";

    [Fact]
    public async Task Email_channel_round_trips()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var request = api.Request() with { Notifications = [new NotificationRequest(null, "email", ["failed"], ["sales-leads@example.com"])] };

        var (created, _) = await api.CreateAsync(request);
        var read = (await api.Operator.GetFromJsonAsync<AutomationResponse>($"/api/automations/{created.Id}"))!;

        var channel = Assert.Single(read.Notifications);
        Assert.Equal("email", channel.Kind);
        Assert.Equal(["failed"], channel.On);
        Assert.Equal(["sales-leads@example.com"], channel.Recipients!);
        Assert.Null(channel.Url);
        Assert.Null(channel.SigningSecret);
    }

    [Theory]
    [InlineData("webhook", new[] { "failed" }, null, "http://hooks.example.com/x", "notifications[0].url")]
    [InlineData("email", new[] { "failed" }, new string[0], null, "notifications[0].recipients")]
    [InlineData("email", new string[0], new[] { "ops@example.com" }, null, "notifications[0].on")]
    [InlineData("email", new[] { "succeeded" }, new[] { "ops@example.com" }, null, "notifications[0].on")]
    [InlineData("email", new[] { "failed" }, new[] { "Ops <ops@example.com>" }, null, "notifications[0].recipients")]
    [InlineData("sms", new[] { "failed" }, null, null, "notifications[0].kind")]
    [InlineData("webhook", new[] { "failed" }, null, null, "notifications[0].url")]
    public async Task Invalid_channel_is_rejected_with_a_field_message(string kind, string[] on, string[]? recipients, string? url, string field)
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var request = api.Request() with { Notifications = [new NotificationRequest(null, kind, on, recipients, url)] };

        var response = await api.Operator.PostAsJsonAsync("/api/automations", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Contains(field, problem.Errors.Keys);
    }

    [Fact]
    public async Task Read_shows_only_the_webhook_host()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var request = api.Request() with { Notifications = [new NotificationRequest(null, "webhook", ["failed", "timed_out"], Url: HookUrl)] };

        var (created, _) = await api.CreateAsync(request);
        using var viewer = api.Host.Client(BatonRoles.Viewer);
        var read = (await viewer.GetFromJsonAsync<AutomationResponse>($"/api/automations/{created.Id}"))!;

        Assert.StartsWith("whsec_", created.Notifications[0].SigningSecret, StringComparison.Ordinal);
        var channel = Assert.Single(read.Notifications);
        Assert.Equal("https://hooks.example.com", channel.Url);
        Assert.Null(channel.SigningSecret);
        var raw = await viewer.GetStringAsync($"/api/automations/{created.Id}");
        Assert.DoesNotContain("/services/", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("whsec_", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_without_url_keeps_the_stored_url()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, etag) = await api.CreateAsync(api.Request() with
        {
            Notifications = [new NotificationRequest(null, "webhook", ["failed"], Url: HookUrl)],
        });
        var channelId = created.Notifications[0].Id;
        var storedBefore = await StoredAsync(channelId);

        var updated = await PutAsync(api, created, etag, [new NotificationRequest(channelId, "webhook", ["failed", "timed_out"])]);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var channel = Assert.Single((await updated.Content.ReadFromJsonAsync<AutomationResponse>())!.Notifications);
        Assert.Equal(["failed", "timed_out"], channel.On);
        Assert.Null(channel.SigningSecret);
        var storedAfter = await StoredAsync(channelId);
        Assert.Equal(storedBefore.Url, storedAfter.Url);
        Assert.Equal(storedBefore.Secret, storedAfter.Secret);
        Assert.Equal(HookUrl, Secrets(api).RevealUrl(storedAfter.Url));
    }

    [Fact]
    public async Task Notification_url_change_is_audited_without_the_url()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, etag) = await api.CreateAsync(api.Request() with
        {
            Notifications = [new NotificationRequest(null, "webhook", ["failed"], Url: HookUrl)],
        });
        var channelId = created.Notifications[0].Id;

        var updated = await PutAsync(api, created, etag,
            [new NotificationRequest(channelId, "webhook", ["failed"], Url: "https://hooks.example.com/services/T0/B0/xyz")]);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var page = (await api.Operator.GetFromJsonAsync<AuditPage>($"/api/audit?entityType=automation&entityId={created.Id}"))!;
        var entry = page.Items[0];
        Assert.Equal(AuditActions.Update, entry.Action);
        Assert.Equal(channelId, entry.After!.Value.GetProperty("changedWebhookUrls")[0].GetGuid());
        var raw = await HostData.ScalarAsync<string>(pg,
            "SELECT string_agg(coalesce(before::text, '') || coalesce(after::text, ''), ' ') FROM audit_log WHERE entity_id = @id",
            ("id", created.Id.ToString()));
        Assert.DoesNotContain("/services/", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_without_notifications_keeps_the_channels_and_empty_list_removes_them()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, etag) = await api.CreateAsync(api.Request() with
        {
            Notifications = [new NotificationRequest(null, "email", ["failed"], ["ops@example.com"])],
        });

        using (var keep = new HttpRequestMessage(HttpMethod.Put, $"/api/automations/{created.Id}")
               { Content = JsonContent.Create(api.Request(created.Name) with { Priority = 70 }) })
        {
            keep.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
            var kept = await api.Operator.SendAsync(keep);
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
            Assert.Single((await kept.Content.ReadFromJsonAsync<AutomationResponse>())!.Notifications);
            etag = kept.Headers.ETag!.Tag;
        }

        var removed = await PutAsync(api, created, etag, []);

        Assert.Empty((await removed.Content.ReadFromJsonAsync<AutomationResponse>())!.Notifications);
    }

    [Fact]
    public async Task Clone_drops_webhook_channels()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, _) = await api.CreateAsync(api.Request() with
        {
            Notifications =
            [
                new NotificationRequest(null, "email", ["failed"], ["ops@example.com"]),
                new NotificationRequest(null, "webhook", ["failed"], Url: HookUrl),
            ],
        });

        var response = await api.Operator.PostAsJsonAsync($"/api/automations/{created.Id}/clone", new CloneRequest(HostData.NewName("clone")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues(AutomationEndpoints.DroppedWebhookChannelsHeader).Single());
        var clone = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
        var channel = Assert.Single(clone.Notifications);
        Assert.Equal("email", channel.Kind);
        Assert.NotEqual(created.Notifications[0].Id, channel.Id);
    }

    [Fact]
    public async Task Signing_secret_rotation_returns_a_new_secret_once()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, _) = await api.CreateAsync(api.Request() with
        {
            Notifications = [new NotificationRequest(null, "webhook", ["failed"], Url: HookUrl)],
        });
        var channel = created.Notifications[0];

        var rotated = await api.Operator.PostAsync($"/api/automations/{created.Id}/notifications/{channel.Id}/rotate-secret", null);

        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var secret = (await rotated.Content.ReadFromJsonAsync<SigningSecretRotated>())!.SigningSecret;
        Assert.NotEqual(channel.SigningSecret, secret);
        Assert.Equal(secret, Secrets(api).RevealSecret((await StoredAsync(channel.Id)).Secret));
    }

    private static async Task<HttpResponseMessage> PutAsync(
        AutomationApi api, AutomationResponse created, string etag, NotificationRequest[] notifications)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/automations/{created.Id}")
        {
            Content = JsonContent.Create(api.Request(created.Name) with { Notifications = notifications }),
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
        return await api.Operator.SendAsync(request);
    }

    private static NotificationSecrets Secrets(AutomationApi api) => api.Host.Services.GetRequiredService<NotificationSecrets>();

    private async Task<(byte[] Url, byte[] Secret)> StoredAsync(Guid channelId)
    {
        await using var cmd = pg.DataSource.CreateCommand("SELECT webhook_url, webhook_secret FROM notification_channels WHERE id = @id");
        cmd.Parameters.AddWithValue("id", channelId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1));
    }
}
