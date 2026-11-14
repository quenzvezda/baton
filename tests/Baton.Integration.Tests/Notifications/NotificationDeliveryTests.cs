using System.Diagnostics.Metrics;
using System.Text.Json;
using Baton.Domain.Automations;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Orchestrator.Health;
using Baton.Orchestrator.Notifications;
using Baton.Persistence.Queue;
using Microsoft.AspNetCore.Http;

namespace Baton.Integration.Tests.Notifications;

/// <summary>OBS-05 enqueue, delivery, retries and webhooks; OQ-08 (M7 tasks 5.3–5.5, 5.7).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class NotificationDeliveryTests(PostgresFixture pg)
{
    // 5.3 — enqueue in the statement that ends the run.

    [Fact]
    public async Task Failed_run_schedules_one_delivery_per_matching_channel()
    {
        await using var rig = NotificationRig.Create(pg);
        var (_, runId) = await NotificationRig.FailingRunAsync(pg, async s =>
        {
            await rig.AddEmailAsync(s.AutomationId, ["ops@example.com"], "failed");
            await rig.AddWebhookAsync(s.AutomationId, "https://hooks.example.com/x", "timed_out");
            await rig.AddWebhookAsync(s.AutomationId, "https://hooks.example.com/y", "failed", "timed_out");
        });

        Assert.Equal(2, await rig.DeliveriesAsync(runId));
        Assert.Equal("pending,pending", await rig.StatusAsync(runId));
    }

    [Fact]
    public async Task Timed_out_run_notifies_channels_on_timed_out()
    {
        await using var rig = NotificationRig.Create(pg);
        var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"), runTimeout: TimeSpan.FromHours(1));
        await rig.AddEmailAsync(s.AutomationId, ["ops@example.com"], "failed");
        await rig.AddEmailAsync(s.AutomationId, ["ops@example.com"], "timed_out");
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.Dispatch.ClaimOneAsync(host);
        await HostData.ExecuteAsync(pg, "UPDATE workflow_runs SET deadline_at = now() - interval '1 second' WHERE id = @id", ("id", runId));
        await s.Dispatch.Reaper.ReapAsync(CancellationToken.None);
        await s.Dispatch.CompleteAsync(lease, Baton.Domain.Runs.AttemptOutcome.Cancelled);

        Assert.Equal("timed_out", await s.RunStatusAsync(runId));
        Assert.Equal(1, await rig.DeliveriesAsync(runId));
        Assert.Equal("timed_out", await HostData.ScalarAsync<string>(pg,
            "SELECT event FROM notification_deliveries WHERE workflow_run_id = @id", ("id", runId)));
    }

    [Fact]
    public async Task Succeeded_and_cancelled_runs_schedule_nothing()
    {
        await using var rig = NotificationRig.Create(pg);
        var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"), policy: "queue", maxRuns: 5);
        await rig.AddEmailAsync(s.AutomationId, ["ops@example.com"], "failed", "timed_out");
        var host = await s.Dispatch.AddHostAsync();
        var succeeded = await s.StartRunAsync();
        await s.CompleteRunAsync(succeeded, host);
        var cancelled = await s.StartRunAsync();
        await s.Cancellation.CancelAsync(cancelled, RunCancelReason.User, CancellationToken.None);

        Assert.Equal("succeeded", await s.RunStatusAsync(succeeded));
        Assert.Equal("cancelled", await s.RunStatusAsync(cancelled));
        Assert.Equal(0, await rig.DeliveriesAsync(succeeded));
        Assert.Equal(0, await rig.DeliveriesAsync(cancelled));
    }

    [Fact]
    public async Task Deleted_automation_schedules_nothing()
    {
        await using var rig = NotificationRig.Create(pg);
        var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        await rig.AddEmailAsync(s.AutomationId, ["ops@example.com"]);
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();

        // Deleting is soft and lets active runs finish (AUT-01); the deleted automation's channels stay quiet.
        await HostData.ExecuteAsync(pg, "UPDATE automations SET deleted_at = now() WHERE id = @id", ("id", s.AutomationId));
        await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(host), Baton.Domain.Runs.AttemptOutcome.Failed, retryable: false);

        Assert.Equal("failed", await s.RunStatusAsync(runId));
        Assert.Equal(0, await rig.DeliveriesAsync(runId));
    }

    [Fact]
    public async Task Rolled_back_finish_schedules_nothing()
    {
        await using var rig = NotificationRig.Create(pg);
        var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        await rig.AddEmailAsync(s.AutomationId, ["ops@example.com"]);
        var runId = await s.StartRunAsync();
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET status = 'failed', finished_at = now() WHERE workflow_run_id = @id", ("id", runId));

        await using (var connection = await pg.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await RunFinalizer.TryFinalizeAsync(connection, transaction, runId, CancellationToken.None);
            await using var inside = new Npgsql.NpgsqlCommand("SELECT count(*) FROM notification_deliveries WHERE workflow_run_id = @id", connection, transaction);
            inside.Parameters.AddWithValue("id", runId);
            Assert.Equal(1L, await inside.ExecuteScalarAsync());
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await rig.DeliveriesAsync(runId));
        Assert.Equal("running", await s.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Rerun_that_fails_again_schedules_new_deliveries()
    {
        await using var rig = NotificationRig.Create(pg);
        var (s, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddEmailAsync(s.AutomationId, ["ops@example.com"]));
        var host = await s.Dispatch.AddHostAsync();

        await new RunRerun(pg.DataSource).RequestAsync(runId, "ana", CancellationToken.None);
        await s.AdmitAsync();
        await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(host), Baton.Domain.Runs.AttemptOutcome.Failed, retryable: false);

        Assert.Equal(2, await rig.DeliveriesAsync(runId));
    }

    // 5.4 — the delivery loop.

    [Fact]
    public async Task Delivery_retries_after_503_and_is_recorded_sent()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.StatusFor = n => n == 1 ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK;
        await using var rig = NotificationRig.Create(pg);
        var (_, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddWebhookAsync(s.AutomationId, receiver.Url()));
        var loop = rig.Loop();

        await loop.TickAsync(CancellationToken.None);
        Assert.Equal("pending", await rig.StatusAsync(runId));
        var wait = await HostData.ScalarAsync<double>(pg,
            "SELECT extract(epoch FROM next_attempt_at - now()) FROM notification_deliveries WHERE workflow_run_id = @id", ("id", runId));
        Assert.InRange(wait, 50, 70);
        await loop.TickAsync(CancellationToken.None);
        Assert.Single(receiver.Requests);

        await rig.MakeDueAsync(runId);
        await loop.TickAsync(CancellationToken.None);

        Assert.Equal("sent", await rig.StatusAsync(runId));
        Assert.Equal(2, receiver.Requests.Count);
        Assert.Contains(RunEventTypes.NotificationSent, await EventTypesAsync(runId));
    }

    [Fact]
    public async Task Delivery_fails_after_5_attempts_and_the_run_stays_failed()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.StatusFor = _ => StatusCodes.Status503ServiceUnavailable;
        await using var rig = NotificationRig.Create(pg);
        var (s, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddWebhookAsync(s.AutomationId, receiver.Url()));
        var loop = rig.Loop();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await rig.MakeDueAsync(runId);
            await loop.TickAsync(CancellationToken.None);
        }

        Assert.Equal(5, receiver.Requests.Count);
        Assert.Equal("failed", await rig.StatusAsync(runId));
        Assert.Equal("failed", await s.RunStatusAsync(runId));
        var data = await HostData.ScalarAsync<string>(pg,
            "SELECT data::text FROM run_events WHERE workflow_run_id = @id AND type = 'notification.failed'", ("id", runId));
        using var json = JsonDocument.Parse(data);
        Assert.Equal(5, json.RootElement.GetProperty("attempts").GetInt32());
        Assert.Contains("503", json.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_retryable_4xx_fails_at_once()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.StatusFor = _ => StatusCodes.Status410Gone;
        await using var rig = NotificationRig.Create(pg);
        var (_, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddWebhookAsync(s.AutomationId, receiver.Url()));

        await rig.Loop().TickAsync(CancellationToken.None);

        Assert.Equal("failed", await rig.StatusAsync(runId));
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task Crashed_sender_delivery_is_retried_after_the_lock_expires()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        await using var rig = NotificationRig.Create(pg);
        var (_, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddWebhookAsync(s.AutomationId, receiver.Url()));
        var claimed = Assert.Single(await rig.Outbox.ClaimDueAsync(100, CancellationToken.None), d => d.RunId == runId);

        await rig.Loop().TickAsync(CancellationToken.None);
        Assert.Empty(receiver.Requests);

        await HostData.ExecuteAsync(pg, "UPDATE notification_deliveries SET locked_until = now() - interval '1 second' WHERE id = @id", ("id", claimed.Id));
        await rig.Loop().TickAsync(CancellationToken.None);

        Assert.Equal("sent", await rig.StatusAsync(runId));
        Assert.Equal(2, await HostData.ScalarAsync<int>(pg, "SELECT attempts FROM notification_deliveries WHERE id = @id", ("id", claimed.Id)));
        Assert.False(await rig.Outbox.RecordFailedAsync(claimed, "late result of the crashed attempt", CancellationToken.None));
        Assert.Equal("sent", await rig.StatusAsync(runId));
    }

    [Fact]
    public async Task Two_loops_send_each_delivery_once()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        await using var rig = NotificationRig.Create(pg);
        await using var other = NotificationRig.Create(pg);
        var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        await rig.AddWebhookAsync(s.AutomationId, receiver.Url());
        var runId = await s.StartRunAsync();
        await HostData.ExecuteAsync(pg, """
            INSERT INTO notification_deliveries (workflow_run_id, channel_id, event)
            SELECT @run, (SELECT id FROM notification_channels WHERE automation_id = @automation), 'failed'
            FROM generate_series(1, 50)
            """, ("run", runId), ("automation", s.AutomationId));
        var loops = new[] { rig.Loop(), other.Loop() };

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Task.WhenAll(loops.Select(loop => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && await rig.StatusAsync(runId) is var status && status.Contains("pending", StringComparison.Ordinal))
                await loop.TickAsync(CancellationToken.None);
        })));

        Assert.Equal(50, receiver.Requests.Count);
        Assert.Equal(50, receiver.Requests.Select(r => r.DeliveryId).Distinct().Count());
        Assert.Equal(50, await HostData.ScalarAsync<long>(pg,
            "SELECT count(*) FROM notification_deliveries WHERE workflow_run_id = @id AND status = 'sent' AND attempts = 1", ("id", runId)));
        await s.CompleteRunAsync(runId, await s.Dispatch.AddHostAsync());
    }

    // 5.5 — webhooks.

    [Fact]
    public async Task Webhook_signature_verifies_with_the_channel_secret()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        await using var rig = NotificationRig.Create(pg);
        var secret = "";
        await NotificationRig.FailingRunAsync(pg, async s => secret = (await rig.AddWebhookAsync(s.AutomationId, receiver.Url())).Secret);

        await rig.Loop().TickAsync(CancellationToken.None);

        var request = Assert.Single(receiver.Requests);
        Assert.StartsWith("ntf_", request.DeliveryId, StringComparison.Ordinal);
        Assert.True(WebhookSignature.Verify(secret, request.Body, request.DeliveryId, request.Signature, DateTimeOffset.UtcNow));
        Assert.False(WebhookSignature.Verify(secret + "x", request.Body, request.DeliveryId, request.Signature, DateTimeOffset.UtcNow));
        Assert.StartsWith("Baton/", request.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_carries_the_same_delivery_id()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.StatusFor = n => n == 1 ? StatusCodes.Status500InternalServerError : StatusCodes.Status204NoContent;
        await using var rig = NotificationRig.Create(pg);
        var (_, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddWebhookAsync(s.AutomationId, receiver.Url()));

        await rig.Loop().TickAsync(CancellationToken.None);
        await rig.MakeDueAsync(runId);
        await rig.Loop().TickAsync(CancellationToken.None);

        Assert.Equal(2, receiver.Requests.Count);
        Assert.Single(receiver.Requests.Select(r => r.DeliveryId).Distinct());
        Assert.Equal("sent", await rig.StatusAsync(runId));
    }

    [Fact]
    public async Task Redirect_is_not_followed()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        await using var rig = NotificationRig.Create(pg);
        var (_, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddWebhookAsync(s.AutomationId, receiver.Url("moved")));

        await rig.Loop().TickAsync(CancellationToken.None);

        Assert.Equal(["moved"], receiver.Requests.Select(r => r.Path));
        Assert.Equal("failed", await rig.StatusAsync(runId));
    }

    [Fact]
    public async Task Body_lists_failed_tasks_and_console_link()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        await using var rig = NotificationRig.Create(pg);
        var (s, runId) = await NotificationRig.FailingRunAsync(pg, s => rig.AddWebhookAsync(s.AutomationId, receiver.Url()));

        await rig.Loop().TickAsync(CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(receiver.Requests).Body);
        var root = body.RootElement;
        Assert.Equal("run.failed", root.GetProperty("type").GetString());
        var run = root.GetProperty("run");
        Assert.Equal(runId, run.GetProperty("id").GetGuid());
        Assert.Equal(s.AutomationId, run.GetProperty("automationId").GetGuid());
        Assert.Equal($"{s.WorkflowKey}@1", run.GetProperty("workflow").GetString());
        var task = Assert.Single(run.GetProperty("failedTasks").EnumerateArray());
        Assert.Equal("build-report", task.GetProperty("key").GetString());
        Assert.Equal("boom", task.GetProperty("message").GetString());
        Assert.Equal($"https://baton.example.test/runs/{runId}", run.GetProperty("consoleUrl").GetString());
    }

    // 5.7 — metrics and timeline.

    [Fact]
    public async Task Sent_and_failed_notifications_are_counted_by_kind()
    {
        await using var receiver = await WebhookReceiver.StartAsync();
        receiver.StatusFor = n => n == 1 ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest;
        await using var rig = NotificationRig.Create(pg);
        var counts = new Dictionary<(string Name, string? Kind), long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Scope == rig.MeterFactory && instrument.Name.StartsWith("baton.notifications.", StringComparison.Ordinal))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? kind = null;
            foreach (var tag in tags)
            {
                if (tag.Key == OrchestratorMetrics.NotificationKindTag)
                    kind = tag.Value as string;
            }

            lock (counts)
                counts[(instrument.Name, kind)] = counts.GetValueOrDefault((instrument.Name, kind)) + value;
        });
        listener.Start();

        var (_, first) = await NotificationRig.FailingRunAsync(pg, s => rig.AddWebhookAsync(s.AutomationId, receiver.Url()));
        await rig.Loop().TickAsync(CancellationToken.None);
        var (_, second) = await NotificationRig.FailingRunAsync(pg, s => rig.AddEmailAsync(s.AutomationId, ["ops@example.com"]));
        var (_, third) = await NotificationRig.FailingRunAsync(pg, s => rig.AddWebhookAsync(s.AutomationId, receiver.Url()));
        await rig.Loop().TickAsync(CancellationToken.None);

        Assert.Equal(1, counts.GetValueOrDefault(("baton.notifications.sent", "webhook")));
        Assert.Equal(1, counts.GetValueOrDefault(("baton.notifications.failed", "webhook")));
        Assert.Equal(1, counts.GetValueOrDefault(("baton.notifications.failed", "email")));
        Assert.Contains(RunEventTypes.NotificationSent, await EventTypesAsync(first));
        Assert.Contains(RunEventTypes.NotificationFailed, await EventTypesAsync(second));
        Assert.Contains(RunEventTypes.NotificationFailed, await EventTypesAsync(third));
    }

    private async Task<List<string>> EventTypesAsync(Guid runId)
    {
        var types = new List<string>();
        await using var cmd = pg.DataSource.CreateCommand("SELECT type FROM run_events WHERE workflow_run_id = @id ORDER BY id");
        cmd.Parameters.AddWithValue("id", runId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            types.Add(reader.GetString(0));
        return types;
    }
}
