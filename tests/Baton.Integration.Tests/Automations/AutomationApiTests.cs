using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Baton.ControlPlane.Api.Automations;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Automations;
using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Queue;
using Microsoft.AspNetCore.Http;

namespace Baton.Integration.Tests.Automations;

/// <summary>AUT-01..AUT-06: the automations API (M4 tasks 1.1–1.4) and Run now (2.4).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AutomationApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly int[] NumericRecipients = [1];

    private AutomationApi _api = null!;

    public async Task InitializeAsync() => _api = await AutomationApi.StartAsync(pg);

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Create_stores_an_enabled_automation_and_returns_it_with_its_triggers()
    {
        var name = HostData.NewName("APAC nightly sales report");

        var (automation, etag) = await _api.CreateAsync(_api.Request(name) with { MaxConcurrentRuns = 2 });

        Assert.Equal(name, automation.Name);
        Assert.True(automation.Enabled);
        Assert.Equal("test-user", automation.Owner); // the caller, by default
        Assert.Equal("\"0\"", etag);
        var trigger = Assert.Single(automation.Triggers);
        Assert.Equal(("cron", "0 2 * * *", "Asia/Singapore"), (trigger.Kind, trigger.CronExpression, trigger.TimeZone));
        Assert.Equal(TimeSpan.Zero, trigger.NextFireAt!.Value.Offset);
        Assert.Equal(18, trigger.NextFireAt.Value.Hour); // 02:00 in Singapore
        Assert.Equal(2, await HostData.ScalarAsync<int>(pg, "SELECT max FROM concurrency_limits WHERE key = @key",
            ("key", AutomationRules.AutomationLimitKey(automation.Id))));
    }

    [Fact]
    public async Task Disabling_stops_its_triggers_until_it_is_enabled_again()
    {
        var (automation, _) = await _api.CreateAsync(_api.Request());
        var disabled = await _api.Operator.PostAsync($"/api/automations/{automation.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        await HostData.ExecuteAsync(pg, "UPDATE automation_triggers SET next_fire_at = now() - interval '1 second' WHERE automation_id = @id",
            ("id", automation.Id));

        await new CronTriggers(pg.DataSource).FireDueAsync(CancellationToken.None);

        Assert.Equal(0L, await RunCountAsync(automation.Id));

        var enabled = await (await _api.Operator.PostAsync($"/api/automations/{automation.Id}/enable", null))
            .Content.ReadFromJsonAsync<AutomationResponse>();
        Assert.True(enabled!.Enabled);
        Assert.True(enabled.Triggers[0].NextFireAt > DateTimeOffset.UtcNow); // missed times do not fire
    }

    [Fact]
    public async Task Clone_creates_a_disabled_copy_with_a_new_name_and_new_webhook_secrets()
    {
        var (source, _) = await _api.CreateAsync(_api.Request(triggers:
            [new TriggerRequest(null, "cron", "0 2 * * *", "Asia/Singapore"), new TriggerRequest(null, "webhook")]) with { Priority = 70 });
        var name = HostData.NewName("clone");

        var response = await _api.Operator.PostAsJsonAsync($"/api/automations/{source.Id}/clone", new CloneRequest(name));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var clone = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
        Assert.NotEqual(source.Id, clone.Id);
        Assert.Equal(name, clone.Name);
        Assert.False(clone.Enabled);
        Assert.Equal((source.WorkflowKey, source.HostGroupId, source.Priority), (clone.WorkflowKey, clone.HostGroupId, clone.Priority));
        Assert.True(JsonElement.DeepEquals(source.Parameters, clone.Parameters));
        Assert.Equal(["cron", "webhook"], clone.Triggers.Select(t => t.Kind).Order());
        var sourceSecret = source.Triggers.Single(t => t.Kind == "webhook").WebhookSecret;
        var cloneSecret = clone.Triggers.Single(t => t.Kind == "webhook").WebhookSecret;
        Assert.NotNull(cloneSecret);
        Assert.NotEqual(sourceSecret, cloneSecret);
    }

    [Fact]
    public async Task Deleting_an_automation_with_a_running_run_lets_the_run_finish()
    {
        var s = await RunScenario.CreateAsync(pg);
        var (automation, _) = await _api.CreateAsync(_api.Request() with
        {
            WorkflowKey = s.WorkflowKey,
            HostGroupId = s.GroupId,
            Parameters = null,
        });
        var host = await s.Dispatch.AddHostAsync();
        var runId = await _api.RunNowAsync(automation.Id);
        await s.AdmitAsync();
        Assert.Equal("running", await s.RunStatusAsync(runId));

        var deleted = await _api.Operator.DeleteAsync($"/api/automations/{automation.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.Operator.GetAsync($"/api/automations/{automation.Id}")).StatusCode);
        await s.CompleteRunAsync(runId, host);
        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
        Assert.Equal(automation.Id, await HostData.ScalarAsync<Guid>(pg, "SELECT automation_id FROM workflow_runs WHERE id = @id", ("id", runId)));
    }

    [Fact]
    public async Task The_name_of_a_deleted_automation_can_be_reused()
    {
        var (first, _) = await _api.CreateAsync(_api.Request());
        await _api.Operator.DeleteAsync($"/api/automations/{first.Id}");

        var (second, _) = await _api.CreateAsync(_api.Request(first.Name));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await _api.Operator.PostAsJsonAsync("/api/automations", _api.Request(first.Name))).StatusCode);
    }

    [Fact]
    public async Task Second_save_from_a_stale_read_is_rejected_and_the_first_change_is_kept()
    {
        var (created, etag) = await _api.CreateAsync(_api.Request());

        var first = await PutAsync(created.Id, _api.Request(created.Name) with { Priority = 60 }, etag);
        var second = await PutAsync(created.Id, _api.Request(created.Name) with { Priority = 90 }, etag);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("\"1\"", first.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, second.StatusCode);
        var current = await _api.Operator.GetFromJsonAsync<AutomationResponse>($"/api/automations/{created.Id}");
        Assert.Equal(60, current!.Priority);
    }

    [Fact]
    public async Task Update_without_if_match_is_rejected()
    {
        var (created, _) = await _api.CreateAsync(_api.Request());

        var response = await _api.Operator.PutAsJsonAsync($"/api/automations/{created.Id}", _api.Request(created.Name));

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task Update_keeps_listed_triggers_adds_new_ones_and_removes_the_rest()
    {
        var (created, etag) = await _api.CreateAsync(_api.Request(triggers:
            [new TriggerRequest(null, "cron", "0 2 * * *", "Asia/Singapore"), new TriggerRequest(null, "webhook")]));
        var cron = created.Triggers.Single(t => t.Kind == "cron");

        var response = await PutAsync(created.Id, _api.Request(created.Name, triggers:
            [new TriggerRequest(cron.Id, "cron", "0 3 * * *", "Asia/Singapore"), new TriggerRequest(null, "manual")]), etag);

        var updated = (await response.Content.ReadFromJsonAsync<AutomationResponse>())!;
        Assert.Equal(["cron", "manual"], updated.Triggers.Select(t => t.Kind).Order());
        var kept = updated.Triggers.Single(t => t.Kind == "cron");
        Assert.Equal(cron.Id, kept.Id);
        Assert.Equal(19, kept.NextFireAt!.Value.Hour); // 03:00 in Singapore
    }

    [Fact]
    public async Task Missing_required_parameter_is_rejected_naming_the_field()
    {
        var request = _api.Request() with
        {
            Parameters = JsonSerializer.SerializeToElement(new { reportDate = (string?)null, recipients = Array.Empty<string>() }),
        };

        var errors = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations", request));

        Assert.Equal(["parameters.region"], errors.Keys);
    }

    [Fact]
    public async Task Wrongly_typed_parameters_name_each_field()
    {
        var request = _api.Request() with
        {
            Parameters = JsonSerializer.SerializeToElement(new { region = 5, reportDate = "yesterday", recipients = NumericRecipients }),
        };

        var errors = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations", request));

        Assert.Equal(["parameters.recipients[0]", "parameters.region", "parameters.reportDate"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(120)]
    [InlineData(-1)]
    public async Task Priority_out_of_range_is_rejected(int priority)
    {
        var errors = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations", _api.Request() with { Priority = priority }));

        Assert.Equal(["priority"], errors.Keys);
    }

    [Fact]
    public async Task Unknown_group_workflow_and_version_are_rejected()
    {
        var unknownGroup = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations",
            _api.Request() with { HostGroupId = Guid.CreateVersion7() }));
        var unknownWorkflow = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations",
            _api.Request() with { WorkflowKey = "no-such-workflow" }));
        var unknownVersion = await ValidationErrorsAsync(await _api.Operator.PostAsJsonAsync("/api/automations",
            _api.Request() with { WorkflowVersion = 99 }));

        Assert.Equal(["hostGroupId"], unknownGroup.Keys);
        Assert.Equal(["workflowKey"], unknownWorkflow.Keys);
        Assert.Equal(["workflowVersion"], unknownVersion.Keys);
    }

    [Fact]
    public async Task Viewers_read_but_cannot_change_automations()
    {
        var (created, _) = await _api.CreateAsync(_api.Request());
        using var viewer = _api.Host.Client(BatonRoles.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/automations/{created.Id}")).StatusCode);
        Assert.Contains(await viewer.GetFromJsonAsync<AutomationResponse[]>("/api/automations") ?? [], a => a.Id == created.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/automations", _api.Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/automations/{created.Id}/runs", null)).StatusCode);
    }

    [Fact]
    public async Task Run_now_creates_one_run_immediately()
    {
        var (automation, _) = await _api.CreateAsync(_api.Request());

        var runId = await _api.RunNowAsync(automation.Id);

        Assert.Equal("pending", await HostData.ScalarAsync<string>(pg, "SELECT status FROM workflow_runs WHERE id = @id", ("id", runId)));
        Assert.Equal("manual", await HostData.ScalarAsync<string>(pg, "SELECT trigger_kind FROM workflow_runs WHERE id = @id", ("id", runId)));
        Assert.Equal(1L, await RunCountAsync(automation.Id));
    }

    [Fact]
    public async Task Run_now_with_the_same_idempotency_key_returns_the_same_run()
    {
        var (automation, _) = await _api.CreateAsync(_api.Request(policy: "allow"));

        var first = await RunNowWithKeyAsync(automation.Id, "click-1");
        var second = await RunNowWithKeyAsync(automation.Id, "click-1");

        Assert.Equal(first, second);
        Assert.Equal(1L, await RunCountAsync(automation.Id));
    }

    [Fact]
    public async Task Run_now_on_a_disabled_automation_is_a_conflict()
    {
        var (automation, _) = await _api.CreateAsync(_api.Request() with { Enabled = false });

        var response = await _api.Operator.PostAsync($"/api/automations/{automation.Id}/runs", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0L, await RunCountAsync(automation.Id));
    }

    [Fact]
    public async Task Run_now_under_skip_at_the_limit_is_a_conflict()
    {
        var (automation, _) = await _api.CreateAsync(_api.Request(policy: "skip"));
        await _api.RunNowAsync(automation.Id);

        var response = await _api.Operator.PostAsync($"/api/automations/{automation.Id}/runs", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1L, await RunCountAsync(automation.Id));
    }

    [Fact]
    public async Task Cancel_api_cancels_a_run_and_rejects_a_finished_one()
    {
        var (automation, _) = await _api.CreateAsync(_api.Request(policy: "allow", maxRuns: 5));
        var pending = await _api.RunNowAsync(automation.Id);
        var finished = await _api.RunNowAsync(automation.Id);
        await HostData.ExecuteAsync(pg, "UPDATE workflow_runs SET status = 'succeeded', finished_at = now() WHERE id = @id", ("id", finished));
        using var viewer = _api.Host.Client(BatonRoles.Viewer);

        var cancelled = await _api.Operator.PostAsync($"/api/runs/{pending}/cancel", null);
        var conflict = await _api.Operator.PostAsync($"/api/runs/{finished}/cancel", null);
        var unknown = await _api.Operator.PostAsync($"/api/runs/{Guid.CreateVersion7()}/cancel", null);
        var forbidden = await viewer.PostAsync($"/api/runs/{pending}/cancel", null);

        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        Assert.Equal("cancelled", (await cancelled.Content.ReadFromJsonAsync<Baton.ControlPlane.Api.Runs.CancelAccepted>())!.Status);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private async Task<HttpResponseMessage> PutAsync(Guid id, AutomationRequest request, string etag)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"/api/automations/{id}") { Content = JsonContent.Create(request) };
        message.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
        return await _api.Operator.SendAsync(message);
    }

    private async Task<Guid> RunNowWithKeyAsync(Guid automationId, string key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/automations/{automationId}/runs");
        message.Headers.Add("Idempotency-Key", key);
        var response = await _api.Operator.SendAsync(message);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RunAccepted>())!.RunId;
    }

    private static async Task<IDictionary<string, string[]>> ValidationErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>())!.Errors;
    }

    private Task<long> RunCountAsync(Guid automationId) =>
        HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM workflow_runs WHERE automation_id = @id", ("id", automationId));
}
