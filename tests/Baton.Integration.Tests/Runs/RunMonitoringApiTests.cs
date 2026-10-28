using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Runs;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Runs;

/// <summary>OBS-01, OBS-02: runs query and run detail over the API (M5 tasks 2.1, 2.2).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class RunMonitoringApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private ControlPlaneHost _api = null!;
    private HttpClient Http { get; set; } = null!;

    public async Task InitializeAsync()
    {
        _api = await ControlPlaneHost.StartAsync(pg);
        Http = _api.Client(BatonRoles.Viewer);
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Paging_through_10k_runs_returns_every_run_once_newest_first()
    {
        var s = await RunScenario.CreateAsync(pg);
        // Three runs share each created_at, so the id tie-break decides the order inside a page and across pages.
        await SeedRunsAsync(s, 10_000, statusOf: "CASE WHEN i % 10 = 0 THEN 'failed' ELSE 'succeeded' END",
            createdAt: "now() - (i / 3) * interval '1 second'");

        var seen = new List<(DateTimeOffset At, Guid Id)>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Http.GetFromJsonAsync<JsonElement>(Url($"/api/runs?automationId={s.AutomationId}&limit=200"
                + (cursor is null ? "" : $"&cursor={cursor}")));
            seen.AddRange(page.GetProperty("items").EnumerateArray()
                .Select(r => (r.GetProperty("createdAt").GetDateTimeOffset(), r.GetProperty("id").GetGuid())));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(50, pages);
        Assert.Equal(10_000, seen.Count);
        Assert.Equal(10_000, seen.Select(r => r.Id).Distinct().Count());
        var expected = seen.OrderByDescending(r => r.At).ThenByDescending(r => r.Id, Comparer<Guid>.Create(PgCompare)).ToList();
        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task First_page_of_all_runs_returns_within_500_ms()
    {
        var s = await RunScenario.CreateAsync(pg);
        await SeedRunsAsync(s, 10_000, statusOf: "'succeeded'", createdAt: "now() - i * interval '1 minute'");
        await HostData.ExecuteAsync(pg, "ANALYZE workflow_runs");
        await Http.GetAsync(Url("/api/runs?limit=1")); // warm-up: JIT, connection pool

        var stopwatch = Stopwatch.StartNew();
        var page = await Http.GetFromJsonAsync<JsonElement>(Url("/api/runs"));
        stopwatch.Stop();

        Assert.Equal(50, page.GetProperty("items").GetArrayLength());
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"First page took {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task Filter_failed_runs_of_one_automation_in_the_last_7_days()
    {
        var s = await RunScenario.CreateAsync(pg);
        var other = await s.AddAutomationAsync();
        var recentFailed = await InsertRunAsync(s, s.AutomationId, "failed", TimeSpan.FromDays(1));
        var olderFailed = await InsertRunAsync(s, s.AutomationId, "failed", TimeSpan.FromDays(2));
        await InsertRunAsync(s, s.AutomationId, "failed", TimeSpan.FromDays(8));
        await InsertRunAsync(s, s.AutomationId, "succeeded", TimeSpan.FromDays(1));
        await InsertRunAsync(s, other, "failed", TimeSpan.FromDays(1));
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-7).ToString("O"));

        var page = await Http.GetFromJsonAsync<JsonElement>(Url($"/api/runs?automationId={s.AutomationId}&status=Failed&from={from}&limit=1"));
        var next = await Http.GetFromJsonAsync<JsonElement>(Url(
            $"/api/runs?automationId={s.AutomationId}&status=failed&from={from}&limit=1&cursor={page.GetProperty("nextCursor").GetString()}"));

        var first = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(recentFailed, first.GetProperty("id").GetGuid());
        Assert.Equal("failed", first.GetProperty("status").GetString());
        Assert.Equal(s.WorkflowKey, first.GetProperty("workflowKey").GetString());
        Assert.Equal(s.Dispatch.GroupName, first.GetProperty("hostGroupName").GetString());
        Assert.Equal(olderFailed, Assert.Single(next.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Filters_by_workflow_and_host_group()
    {
        var s = await RunScenario.CreateAsync(pg);
        var run = await InsertRunAsync(s, s.AutomationId, "running", TimeSpan.FromMinutes(1));

        var byWorkflow = await Http.GetFromJsonAsync<JsonElement>(Url($"/api/runs?workflowKey={s.WorkflowKey}"));
        var byGroup = await Http.GetFromJsonAsync<JsonElement>(Url($"/api/runs?hostGroupId={s.GroupId}&status=running&status=pending"));

        Assert.Equal(run, Assert.Single(byWorkflow.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(run, Assert.Single(byGroup.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("status=exploded", "status")]
    [InlineData("cursor=not-a-cursor", "cursor")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z", "to")]
    public async Task Invalid_query_is_rejected_naming_the_parameter(string query, string field)
    {
        var response = await Http.GetAsync(Url($"/api/runs?{query}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Detail_of_a_retried_task_shows_both_attempts_the_first_error_and_the_final_output()
    {
        var s = await RunScenario.CreateAsync(pg, wf =>
        {
            var extract = wf.Task<Noop>("extract");
            wf.Task<Noop>("build-report").DependsOn(extract).Retry(2, TimeSpan.Zero);
        });
        var hostA = await s.Dispatch.AddHostAsync();
        var hostB = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        Assert.True(await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(hostA)));
        Assert.True(await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(hostA), AttemptOutcome.Failed));
        await s.Dispatch.Promoter.PromoteAsync(CancellationToken.None);
        Assert.True(await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(hostB), output: "{\"rows\":42}"));

        var detail = await Http.GetFromJsonAsync<JsonElement>(Url($"/api/runs/{runId}"));

        Assert.Equal("succeeded", detail.GetProperty("status").GetString());
        Assert.Equal(s.WorkflowKey, detail.GetProperty("workflowKey").GetString());
        Assert.Equal(["build-report", "extract"], detail.GetProperty("graph").GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("key").GetString()).Order(StringComparer.Ordinal));
        var build = detail.GetProperty("tasks").EnumerateArray().Single(t => t.GetProperty("key").GetString() == "build-report");
        Assert.Equal("succeeded", build.GetProperty("status").GetString());
        Assert.Equal(42, build.GetProperty("output").GetProperty("rows").GetInt32());
        var attempts = build.GetProperty("attempts").EnumerateArray().ToList();
        Assert.Equal([1, 2], attempts.Select(a => a.GetProperty("attempt").GetInt32()));
        Assert.Equal([hostA, hostB], attempts.Select(a => a.GetProperty("hostId").GetGuid()));
        Assert.Equal(["failed", "succeeded"], attempts.Select(a => a.GetProperty("outcome").GetString()));
        Assert.Equal("boom", attempts[0].GetProperty("error").GetProperty("message").GetString());
        Assert.All(attempts, a => Assert.NotEqual(JsonValueKind.Null, a.GetProperty("finishedAt").ValueKind));
        Assert.NotNull(attempts[0].GetProperty("hostName").GetString());
    }

    [Fact]
    public async Task Detail_of_an_unknown_or_purged_run_is_not_found()
    {
        var response = await Http.GetAsync(Url($"/api/runs/{Guid.CreateVersion7()}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_effective_roles()
    {
        using var operatorClient = _api.Client(BatonRoles.Operator);

        var me = await operatorClient.GetFromJsonAsync<JsonElement>(Url("/api/me"));

        Assert.Equal("test-user", me.GetProperty("name").GetString());
        Assert.Equal([BatonRoles.Viewer, BatonRoles.Operator], me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    private Uri Url(string path) => new(_api.BaseAddress, path);

    /// <summary>Postgres orders uuids by their bytes in RFC order; <see cref="Guid.CompareTo(Guid)"/> does not.</summary>
    private static int PgCompare(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString("N"), b.ToString("N"));

    private static Task SeedRunsAsync(RunScenario s, int count, string statusOf, string createdAt) =>
        HostData.ExecuteAsync(s.Pg, $$"""
            INSERT INTO workflow_runs (id, automation_id, definition_id, status, priority, host_group_id, parameters,
                                       trigger_kind, idempotency_key, created_at)
            SELECT gen_random_uuid(), @automation, @definition, {{statusOf}}, 50, @group, '{}'::jsonb, 'manual',
                   'seed:' || gen_random_uuid(), {{createdAt}}
            FROM generate_series(1, {{count}}) AS i
            """, ("automation", s.AutomationId), ("definition", s.Dispatch.DefinitionId), ("group", s.GroupId));

    private static async Task<Guid> InsertRunAsync(RunScenario s, Guid automationId, string status, TimeSpan age)
    {
        var id = Guid.CreateVersion7();
        await HostData.ExecuteAsync(s.Pg, """
            INSERT INTO workflow_runs (id, automation_id, definition_id, status, priority, host_group_id, parameters,
                                       trigger_kind, idempotency_key, created_at)
            VALUES (@id, @automation, @definition, @status, 50, @group, '{}', 'manual', 'seed:' || @id::text, now() - @age)
            """, ("id", id), ("automation", automationId), ("definition", s.Dispatch.DefinitionId), ("status", status),
            ("group", s.GroupId), ("age", age));
        return id;
    }
}
