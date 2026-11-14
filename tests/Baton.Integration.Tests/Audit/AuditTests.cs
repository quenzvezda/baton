using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Baton.ControlPlane.Api.Audit;
using Baton.ControlPlane.Api.Automations;
using Baton.ControlPlane.Api.Hosts;
using Baton.ControlPlane.Api.Security;
using Baton.ControlPlane.Api.Workflows;
using Baton.Integration.Tests.Automations;
using Baton.Integration.Tests.Infrastructure;
using Baton.Orchestrator.Loops;
using Microsoft.Extensions.Logging.Abstractions;

namespace Baton.Integration.Tests.Audit;

/// <summary>AUT-11: configuration changes are audited, without secrets, readable and kept a year (M7 group 2).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AuditTests(PostgresFixture pg)
{
    // 2.1 — the entry commits with the change, and only with it.

    [Fact]
    public async Task Entry_commits_with_the_change()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, _) = await api.CreateAsync(api.Request());

        var entries = await HistoryAsync(api, AuditEntities.Automation, created.Id.ToString());

        var entry = Assert.Single(entries);
        Assert.Equal(AuditActions.Create, entry.Action);
        Assert.Equal(created.Name, entry.After!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Rejected_change_writes_no_entry()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, etag) = await api.CreateAsync(api.Request());

        // 412: a stale version.
        using (var stale = Put($"/api/automations/{created.Id}", api.Request(created.Name) with { Priority = 70 }, "\"99\""))
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await api.Operator.SendAsync(stale)).StatusCode);

        // 400: an invalid priority.
        using (var invalid = Put($"/api/automations/{created.Id}", api.Request(created.Name) with { Priority = 500 }, etag))
            Assert.Equal(HttpStatusCode.BadRequest, (await api.Operator.SendAsync(invalid)).StatusCode);

        Assert.Single(await HistoryAsync(api, AuditEntities.Automation, created.Id.ToString()));
    }

    // 2.2 — automations.

    [Fact]
    public async Task Automation_update_records_before_and_after()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, etag) = await api.CreateAsync(api.Request() with { Priority = 50 });
        using var ana = api.Host.ClientWithToken(Token(BatonRoles.Operator, "ana"));

        using (var update = Put($"/api/automations/{created.Id}", api.Request(created.Name) with { Priority = 60 }, etag))
            Assert.Equal(HttpStatusCode.OK, (await ana.SendAsync(update)).StatusCode);

        var entry = (await HistoryAsync(api, AuditEntities.Automation, created.Id.ToString()))[0];
        Assert.Equal(AuditActions.Update, entry.Action);
        Assert.Equal("ana", entry.Actor.Name);
        Assert.Equal("test-user", entry.Actor.Subject);
        Assert.Equal(50, entry.Before!.Value.GetProperty("priority").GetInt32());
        Assert.Equal(60, entry.After!.Value.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Automation_create_has_no_before()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, _) = await api.CreateAsync(api.Request());

        var entry = Assert.Single(await HistoryAsync(api, AuditEntities.Automation, created.Id.ToString()));
        Assert.Null(entry.Before);
        Assert.NotNull(entry.After);
    }

    [Fact]
    public async Task Deleted_automation_history_stays_readable()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, _) = await api.CreateAsync(api.Request());

        Assert.Equal(HttpStatusCode.NoContent, (await api.Operator.DeleteAsync($"/api/automations/{created.Id}")).StatusCode);

        var entries = await HistoryAsync(api, AuditEntities.Automation, created.Id.ToString());
        Assert.Equal([AuditActions.Delete, AuditActions.Create], entries.Select(e => e.Action));
        Assert.Equal(created.Name, entries[0].Before!.Value.GetProperty("name").GetString());
        Assert.Null(entries[0].After);
    }

    [Fact]
    public async Task Secret_rotation_records_no_secret()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, _) = await api.CreateAsync(api.Request(triggers: [new TriggerRequest(null, "webhook")]));
        var trigger = Assert.Single(created.Triggers);

        var rotated = await api.Operator.PostAsync($"/api/automations/{created.Id}/triggers/{trigger.Id}/rotate-secret", null);
        var secret = (await rotated.Content.ReadFromJsonAsync<SecretRotated>())!.WebhookSecret;

        var entries = await HistoryAsync(api, AuditEntities.Automation, created.Id.ToString());
        Assert.Equal(AuditActions.RotateSecret, entries[0].Action);
        Assert.Equal(trigger.Id, entries[0].After!.Value.GetProperty("triggerId").GetGuid());
        var raw = await RawAuditAsync(created.Id.ToString());
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Triggers[0].WebhookSecret!, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("whsec_", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enable_and_disable_are_audited_only_when_they_change_something()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, _) = await api.CreateAsync(api.Request());

        await api.Operator.PostAsync($"/api/automations/{created.Id}/disable", null);
        await api.Operator.PostAsync($"/api/automations/{created.Id}/disable", null);
        await api.Operator.PostAsync($"/api/automations/{created.Id}/enable", null);

        var entries = await HistoryAsync(api, AuditEntities.Automation, created.Id.ToString());
        Assert.Equal([AuditActions.Enable, AuditActions.Disable, AuditActions.Create], entries.Select(e => e.Action));
    }

    // 2.3 — host groups, agent credentials, workflow limits.

    [Fact]
    public async Task Group_pause_is_audited_with_its_actor()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        using var ana = api.Host.ClientWithToken(Token(BatonRoles.Operator, "ana"));

        Assert.Equal(HttpStatusCode.OK, (await ana.PostAsync($"/api/host-groups/{api.GroupId}/pause", null)).StatusCode);

        var entry = Assert.Single(await HistoryAsync(api, AuditEntities.HostGroup, api.GroupId.ToString()));
        Assert.Equal(AuditActions.Pause, entry.Action);
        Assert.Equal("ana", entry.Actor.Name);
        Assert.False(entry.Before!.Value.GetProperty("dispatchPaused").GetBoolean());
        Assert.True(entry.After!.Value.GetProperty("dispatchPaused").GetBoolean());
    }

    [Fact]
    public async Task Group_limit_change_records_before_and_after()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        using var admin = api.Host.Client(BatonRoles.Admin);
        var created = await admin.PostAsJsonAsync("/api/host-groups", new HostGroupRequest(HostData.NewName("grp"), 2, null, null));
        var group = (await created.Content.ReadFromJsonAsync<HostGroupResponse>())!;

        var updated = await admin.PutAsJsonAsync($"/api/host-groups/{group.Id}", new HostGroupRequest(group.Name, 5, 10, null));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var entries = await HistoryAsync(api, AuditEntities.HostGroup, group.Id.ToString());
        Assert.Equal([AuditActions.Update, AuditActions.Create], entries.Select(e => e.Action));
        Assert.Equal(2, entries[0].Before!.Value.GetProperty("maxConcurrentRuns").GetInt32());
        Assert.Equal(5, entries[0].After!.Value.GetProperty("maxConcurrentRuns").GetInt32());
        Assert.Equal(10, entries[0].After!.Value.GetProperty("maxConcurrentTasks").GetInt32());
    }

    [Fact]
    public async Task Agent_token_revocation_records_no_token()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        using var admin = api.Host.Client(BatonRoles.Admin);
        var issued = (await (await admin.PostAsJsonAsync($"/api/host-groups/{api.GroupId}/agent-tokens", new AgentTokenRequest("etl-agents")))
            .Content.ReadFromJsonAsync<AgentTokenIssued>())!;

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/host-groups/{api.GroupId}/agent-tokens/{issued.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/host-groups/{api.GroupId}/agent-tokens/{issued.Id}")).StatusCode);

        var entries = await HistoryAsync(api, AuditEntities.AgentCredential, issued.Id.ToString());
        Assert.Equal([AuditActions.Revoke, AuditActions.Issue], entries.Select(e => e.Action));
        Assert.Equal("etl-agents", entries[0].After!.Value.GetProperty("name").GetString());
        Assert.NotEqual(JsonValueKind.Null, entries[0].After!.Value.GetProperty("revokedAt").ValueKind);
        var raw = await RawAuditAsync(issued.Id.ToString());
        Assert.DoesNotContain(issued.Token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("baton_", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workflow_limit_set_is_audited()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        using var admin = api.Host.Client(BatonRoles.Admin);
        var before = await HistoryAsync(api, AuditEntities.WorkflowLimit, "nightly-sales-report");

        await admin.PutAsJsonAsync("/api/workflows/nightly-sales-report/limit", new WorkflowLimitRequest(3));
        await admin.PutAsJsonAsync("/api/workflows/nightly-sales-report/limit", new WorkflowLimitRequest(3));
        await admin.DeleteAsync("/api/workflows/nightly-sales-report/limit");

        var entries = (await HistoryAsync(api, AuditEntities.WorkflowLimit, "nightly-sales-report")).SkipLast(before.Length).ToList();
        Assert.Equal([AuditActions.Remove, AuditActions.Set], entries.Select(e => e.Action));
        Assert.Equal(3, entries[1].After!.Value.GetProperty("maxConcurrentRuns").GetInt32());
        Assert.Null(entries[0].After);
    }

    // 2.4 — redaction.

    [Fact]
    public void Audited_dtos_contain_no_secret_values()
    {
        var trigger = new TriggerResponse(Guid.NewGuid(), "webhook", null, null, true, null, null, "/api/webhooks/x",
            "whsec_abc", null, null);
        var token = new AgentTokenIssued(Guid.NewGuid(), "etl", "baton_xyz", DateTimeOffset.UnixEpoch);

        var triggerJson = AuditWriter.Snapshot(trigger)!;
        var tokenJson = AuditWriter.Snapshot(token)!;

        Assert.DoesNotContain("webhookSecret", triggerJson, StringComparison.Ordinal);
        Assert.Contains("webhookUrl", triggerJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"token\"", tokenJson, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => AuditWriter.Snapshot(new { note = "whsec_leaked" }));
        Assert.Throws<InvalidOperationException>(() => AuditWriter.Snapshot(new { note = "baton_leaked" }));
    }

    // 2.5 — reading.

    [Fact]
    public async Task History_of_an_automation_is_newest_first()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, etag) = await api.CreateAsync(api.Request());
        etag = await UpdatePriorityAsync(api, created, etag, 60);
        await UpdatePriorityAsync(api, created, etag, 70);

        var entries = await HistoryAsync(api, AuditEntities.Automation, created.Id.ToString());

        Assert.Equal([AuditActions.Update, AuditActions.Update, AuditActions.Create], entries.Select(e => e.Action));
        Assert.Equal(70, entries[0].After!.Value.GetProperty("priority").GetInt32());
        Assert.True(entries[0].At >= entries[1].At && entries[1].At >= entries[2].At);
    }

    [Fact]
    public async Task Audit_cursor_pages_without_gaps_or_duplicates()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        var (created, etag) = await api.CreateAsync(api.Request());
        for (var priority = 51; priority <= 55; priority++)
            etag = await UpdatePriorityAsync(api, created, etag, priority);

        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var url = $"/api/audit?entityType=automation&entityId={created.Id}&limit=2" + (cursor is null ? "" : $"&cursor={cursor}");
            var page = (await api.Operator.GetFromJsonAsync<AuditPage>(url))!;
            Assert.True(page.Items.Length <= 2);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(6, seen.Count);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.Equal(seen.OrderByDescending(id => id), seen);
    }

    [Fact]
    public async Task Viewer_reads_the_audit_log()
    {
        await using var api = await AutomationApi.StartAsync(pg);
        using var viewer = api.Host.Client(BatonRoles.Viewer);
        using var anonymous = api.Host.Client();

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/audit?entityType=host_group&entityId={api.GroupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/audit?entityType=host_group")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync("/api/audit?entityType=runs")).StatusCode);
    }

    // 2.6 — retention.

    [Fact]
    public async Task Audit_entries_older_than_a_year_are_deleted()
    {
        var entity = Guid.NewGuid().ToString();
        await InsertEntryAsync(entity, daysAgo: 366);
        await InsertEntryAsync(entity, daysAgo: 364);

        await RunMaintenanceAsync();

        var ages = await AgesAsync(entity);
        Assert.Equal([364], ages);
    }

    [Fact]
    public async Task Audit_outlives_run_history()
    {
        var entity = Guid.NewGuid().ToString();
        await InsertEntryAsync(entity, daysAgo: 120);

        await RunMaintenanceAsync();

        Assert.Equal([120], await AgesAsync(entity));
    }

    private static async Task<string> UpdatePriorityAsync(AutomationApi api, AutomationResponse created, string etag, int priority)
    {
        using var update = Put($"/api/automations/{created.Id}", api.Request(created.Name) with { Priority = priority }, etag);
        var response = await api.Operator.SendAsync(update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag;
    }

    private static HttpRequestMessage Put(string url, AutomationRequest body, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
        return request;
    }

    private static async Task<AuditEntryResponse[]> HistoryAsync(AutomationApi api, string entityType, string entityId)
    {
        var page = await api.Operator.GetFromJsonAsync<AuditPage>($"/api/audit?entityType={entityType}&entityId={Uri.EscapeDataString(entityId)}");
        return page!.Items;
    }

    private Task<string> RawAuditAsync(string entityId) =>
        HostData.ScalarAsync<string>(pg, "SELECT coalesce(string_agg(coalesce(before::text, '') || coalesce(after::text, ''), ' '), '') FROM audit_log WHERE entity_id = @id",
            ("id", entityId));

    private static string Token(string role, string name) =>
        TestTokens.CreateWithClaims(TestTokens.SigningKey, new Dictionary<string, object>
        {
            [TestTokens.RoleClaimType] = new[] { TestTokens.ClaimValue(role) },
            ["name"] = name,
        });

    private Task InsertEntryAsync(string entityId, int daysAgo) =>
        HostData.ExecuteAsync(pg, """
            INSERT INTO audit_log (at, actor_subject, actor_name, entity_type, entity_id, action, after)
            VALUES (now() - make_interval(days => @days), 'sub', 'ana', 'automation', @id, 'update', '{}')
            """, ("days", daysAgo), ("id", entityId));

    private async Task<List<int>> AgesAsync(string entityId)
    {
        var ages = new List<int>();
        await using var cmd = pg.DataSource.CreateCommand(
            "SELECT extract(day FROM now() - at)::int FROM audit_log WHERE entity_id = @id ORDER BY at");
        cmd.Parameters.AddWithValue("id", entityId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ages.Add(reader.GetInt32(0));
        return ages;
    }

    private async Task RunMaintenanceAsync()
    {
        var loop = new PartitionMaintenanceLoop(pg.DataSource, TestMetrics.Create(), NullLogger<PartitionMaintenanceLoop>.Instance);
        Assert.True(await loop.RunOnceAsync(CancellationToken.None));
    }
}
