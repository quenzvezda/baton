using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Baton.Integration.Tests.Infrastructure;
using Baton.Orchestrator.Notifications;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Baton.Integration.Tests.Notifications;

/// <summary>OBS-05 email channels through MailKit against Mailpit (ADR-0019, M7 task 5.6).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class EmailDeliveryTests(PostgresFixture pg) : IAsyncLifetime
{
    private const int SmtpPort = 1025;
    private const int ApiPort = 8025;

    private readonly IContainer _mailpit = new ContainerBuilder("axllent/mailpit:v1.27")
        .WithPortBinding(SmtpPort, assignRandomHostPort: true)
        .WithPortBinding(ApiPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ApiPort).ForPath("/livez")))
        .Build();

    private Uri _apiBase = null!;

    public async Task InitializeAsync()
    {
        await _mailpit.StartAsync();
        _apiBase = new Uri($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(ApiPort)}");
    }

    public async Task DisposeAsync() => await _mailpit.DisposeAsync();

    [Fact]
    public async Task Email_names_automation_outcome_and_failed_tasks()
    {
        await using var rig = NotificationRig.Create(pg, new()
        {
            ["Baton:Notifications:Smtp:Host"] = _mailpit.Hostname,
            ["Baton:Notifications:Smtp:Port"] = _mailpit.GetMappedPublicPort(SmtpPort).ToString(CultureInfo.InvariantCulture),
            ["Baton:Notifications:Smtp:Security"] = "None",
            ["Baton:Notifications:Smtp:From"] = "baton@example.test",
        });
        var (s, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddEmailAsync(s.AutomationId, ["sales-leads@example.com", "ops@example.com"]));
        var automation = await HostData.ScalarAsync<string>(pg, "SELECT name FROM automations WHERE id = @id", ("id", s.AutomationId));

        await rig.Loop().TickAsync(CancellationToken.None);

        Assert.Equal("sent", await rig.StatusAsync(runId));
        var message = await FindAsync(automation);
        Assert.Equal($"[Baton] {automation} failed", message.GetProperty("Subject").GetString());
        Assert.Equal(["ops@example.com", "sales-leads@example.com"],
            message.GetProperty("To").EnumerateArray().Select(t => t.GetProperty("Address").GetString()).Order(StringComparer.Ordinal));
        using var api = new HttpClient { BaseAddress = _apiBase };
        var full = await api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{message.GetProperty("ID").GetString()}");
        var text = full.GetProperty("Text").GetString()!;
        Assert.Contains(runId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("build-report (attempt 1): boom", text, StringComparison.Ordinal);
        Assert.Contains($"https://baton.example.test/runs/{runId}", text, StringComparison.Ordinal);
        Assert.Contains("<code>build-report</code>", full.GetProperty("HTML").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unconfigured_smtp_fails_email_deliveries_at_once()
    {
        await using var rig = NotificationRig.Create(pg);
        var (_, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddEmailAsync(s.AutomationId, ["ops@example.com"]));

        await rig.Loop().TickAsync(CancellationToken.None);

        Assert.Equal("failed", await rig.StatusAsync(runId));
        Assert.Equal(SmtpNotificationSender.NotConfigured, await HostData.ScalarAsync<string>(pg,
            "SELECT last_error FROM notification_deliveries WHERE workflow_run_id = @id", ("id", runId)));
    }

    [Fact]
    public async Task Unreachable_mail_server_is_retried()
    {
        await using var rig = NotificationRig.Create(pg, new()
        {
            ["Baton:Notifications:Smtp:Host"] = "127.0.0.1",
            ["Baton:Notifications:Smtp:Port"] = "1",
            ["Baton:Notifications:Smtp:Security"] = "None",
            ["Baton:Notifications:Smtp:From"] = "baton@example.test",
        });
        var (_, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddEmailAsync(s.AutomationId, ["ops@example.com"]));

        await rig.Loop().TickAsync(CancellationToken.None);

        Assert.Equal("pending", await rig.StatusAsync(runId));
        Assert.Equal(1, await HostData.ScalarAsync<int>(pg, "SELECT attempts FROM notification_deliveries WHERE workflow_run_id = @id", ("id", runId)));
    }

    private async Task<JsonElement> FindAsync(string automation)
    {
        using var api = new HttpClient { BaseAddress = _apiBase };
        for (var i = 0; i < 50; i++)
        {
            var page = await api.GetFromJsonAsync<JsonElement>("/api/v1/messages");
            foreach (var message in page.GetProperty("messages").EnumerateArray())
            {
                if (message.GetProperty("Subject").GetString()?.Contains(automation, StringComparison.Ordinal) == true)
                    return message;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException($"Mailpit received no message about {automation}.");
    }
}
