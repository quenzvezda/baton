using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Baton.Abstractions;
using Baton.Domain.Runs;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Orchestrator.Agents;
using Baton.Orchestrator.Health;
using Baton.Orchestrator.Notifications;
using Baton.Persistence;
using Baton.Persistence.Notifications;
using Baton.Persistence.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Baton.Integration.Tests.Notifications;

/// <summary>One request a <see cref="WebhookReceiver"/> got.</summary>
internal sealed record ReceivedWebhook(string Path, string? DeliveryId, string? Signature, byte[] Body, string? UserAgent);

/// <summary>
/// An HTTP receiver on a free loopback port. <c>/hook</c> answers the status <see cref="StatusFor"/> picks for the
/// n-th request (1-based); <c>/moved</c> redirects to <c>/other</c>; <c>/slow</c> answers after 15 s.
/// </summary>
internal sealed class WebhookReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _count;

    private WebhookReceiver(WebApplication app)
    {
        _app = app;
        BaseAddress = new Uri(app.Urls.First());
    }

    public Uri BaseAddress { get; }

    public ConcurrentQueue<ReceivedWebhook> Requests { get; } = new();

    public Func<int, int> StatusFor { get; set; } = _ => StatusCodes.Status200OK;

    public string Url(string path = "hook") => new Uri(BaseAddress, path).ToString();

    public static async Task<WebhookReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        WebhookReceiver? receiver = null;
        app.MapPost("/{path}", async (string path, HttpContext http) =>
        {
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body);
            receiver!.Requests.Enqueue(new ReceivedWebhook(path, http.Request.Headers["X-Delivery-Id"], http.Request.Headers["X-Signature"],
                body.ToArray(), http.Request.Headers.UserAgent));
            switch (path)
            {
                case "moved":
                    return Results.Redirect("/other");
                case "slow":
                    await Task.Delay(TimeSpan.FromSeconds(15), http.RequestAborted);
                    return Results.Ok();
                default:
                    return Results.StatusCode(receiver.StatusFor(Interlocked.Increment(ref receiver._count)));
            }
        });
        await app.StartAsync();
        receiver = new WebhookReceiver(app);
        return receiver;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>
/// The orchestrator's notification delivery wired as in production (<c>AddNotifications</c>) against the shared
/// Postgres, plus helpers to create channels and failed runs.
/// </summary>
internal sealed class NotificationRig : IAsyncDisposable
{
    private NotificationRig(PostgresFixture pg, ServiceProvider services)
    {
        Pg = pg;
        Services = services;
    }

    public PostgresFixture Pg { get; }
    public ServiceProvider Services { get; }
    public NotificationSecrets Secrets => Services.GetRequiredService<NotificationSecrets>();
    public NotificationOutbox Outbox => Services.GetRequiredService<NotificationOutbox>();
    public IMeterFactory MeterFactory => Services.GetRequiredService<IMeterFactory>();

    public NotificationLoop Loop() => Services.GetServices<IHostedService>().OfType<NotificationLoop>().Single();

    public static NotificationRig Create(PostgresFixture pg, Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Baton:Notifications:AllowInsecureWebhooks"] = "true",
            ["Baton:Notifications:ConsoleBaseUrl"] = "https://baton.example.test",
        }.Concat(settings ?? [])).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBatonPersistence(pg.ConnectionString);
        services.AddBatonDataProtection(configuration);
        services.AddOrchestratorMetrics();
        services.AddNotifications(configuration);
        return new NotificationRig(pg, services.BuildServiceProvider());
    }

    /// <summary>A webhook channel of <paramref name="automationId"/>; returns its id and signing secret.</summary>
    public async Task<(Guid Id, string Secret)> AddWebhookAsync(Guid automationId, string url, params string[] on)
    {
        var (secret, stored) = Secrets.IssueSecret();
        var id = Guid.CreateVersion7();
        await HostData.ExecuteAsync(Pg, """
            INSERT INTO notification_channels (id, automation_id, kind, events, webhook_url, webhook_url_display, webhook_secret)
            VALUES (@id, @automation, 'webhook', @on, @url, 'http://127.0.0.1', @secret)
            """, ("id", id), ("automation", automationId), ("on", on.Length == 0 ? ["failed"] : on),
            ("url", Secrets.ProtectUrl(url)), ("secret", stored));
        return (id, secret);
    }

    public async Task<Guid> AddEmailAsync(Guid automationId, string[] to, params string[] on)
    {
        var id = Guid.CreateVersion7();
        await HostData.ExecuteAsync(Pg, """
            INSERT INTO notification_channels (id, automation_id, kind, events, email_to) VALUES (@id, @automation, 'email', @on, @to)
            """, ("id", id), ("automation", automationId), ("on", on.Length == 0 ? ["failed"] : on), ("to", to));
        return id;
    }

    /// <summary>A run of a one-task workflow whose task <c>build-report</c> fails without retry.</summary>
    public static async Task<(RunScenario S, Guid RunId)> FailingRunAsync(PostgresFixture pg, Func<RunScenario, Task>? beforeFailing = null)
    {
        var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("build-report"));
        var host = await s.Dispatch.AddHostAsync();
        if (beforeFailing is not null)
            await beforeFailing(s);
        var runId = await s.StartRunAsync();
        await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(host), AttemptOutcome.Failed, retryable: false);
        Assert.Equal("failed", await s.RunStatusAsync(runId));
        return (s, runId);
    }

    public Task<long> DeliveriesAsync(Guid runId) =>
        HostData.ScalarAsync<long>(Pg, "SELECT count(*) FROM notification_deliveries WHERE workflow_run_id = @id", ("id", runId));

    public Task<string> StatusAsync(Guid runId) =>
        HostData.ScalarAsync<string>(Pg, "SELECT string_agg(status, ',' ORDER BY id) FROM notification_deliveries WHERE workflow_run_id = @id", ("id", runId));

    /// <summary>Makes every pending delivery of the run due now, as if its backoff had passed.</summary>
    public Task MakeDueAsync(Guid runId) =>
        HostData.ExecuteAsync(Pg, "UPDATE notification_deliveries SET next_attempt_at = now() WHERE workflow_run_id = @id AND status = 'pending'",
            ("id", runId));

    public async ValueTask DisposeAsync() => await Services.DisposeAsync();
}
