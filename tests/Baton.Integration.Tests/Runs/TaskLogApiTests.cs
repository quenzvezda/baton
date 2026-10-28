using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using Baton.ControlPlane.Api.Security;
using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Queue;
using Microsoft.Extensions.Logging;

namespace Baton.Integration.Tests.Runs;

/// <summary>OBS-03: reading an attempt's logs and following them live over SSE (M5 task 2.3).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class TaskLogApiTests(PostgresFixture pg) : IAsyncLifetime
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
    public async Task Logs_of_a_finished_attempt_are_returned_in_order_with_time_and_level()
    {
        var (s, runId, lease) = await StartTaskAsync();
        // Chunks arrive out of order; the read follows the sequence the handler wrote them in.
        await AppendAsync(s, lease, (3, LogLevel.Error, "third"));
        await AppendAsync(s, lease, (1, LogLevel.Information, "first"), (2, LogLevel.Warning, "second"));
        Assert.True(await s.Dispatch.CompleteAsync(lease));

        var page = await Http.GetFromJsonAsync<JsonElement>(Url($"/api/runs/{runId}/tasks/a/logs?attempt=1"));

        Assert.Equal(1, page.GetProperty("attempt").GetInt32());
        Assert.False(page.GetProperty("isLive").GetBoolean());
        var lines = page.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["first", "second", "third"], lines.Select(l => l.GetProperty("message").GetString()));
        Assert.Equal(["Information", "Warning", "Error"], lines.Select(l => l.GetProperty("level").GetString()));
        Assert.All(lines, l => Assert.True(l.GetProperty("loggedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5)));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextAfterSeq").ValueKind);
    }

    [Fact]
    public async Task Large_logs_are_read_in_pages()
    {
        var (s, runId, lease) = await StartTaskAsync();
        await AppendAsync(s, lease, [.. Enumerable.Range(1, 5).Select(i => (i, LogLevel.Information, $"line {i}"))]);

        var first = await Http.GetFromJsonAsync<JsonElement>(Url($"/api/runs/{runId}/tasks/a/logs?limit=2"));
        var second = await Http.GetFromJsonAsync<JsonElement>(Url(
            $"/api/runs/{runId}/tasks/a/logs?limit=2&afterSeq={first.GetProperty("nextAfterSeq").GetInt32()}"));

        Assert.True(first.GetProperty("isLive").GetBoolean());
        Assert.Equal([1, 2], first.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("seq").GetInt32()));
        Assert.Equal([3, 4], second.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("seq").GetInt32()));
    }

    [Fact]
    public async Task Unknown_task_or_attempt_is_not_found()
    {
        var (_, runId, _) = await StartTaskAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(Url($"/api/runs/{runId}/tasks/nope/logs"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(Url($"/api/runs/{runId}/tasks/a/logs?attempt=2"))).StatusCode);
    }

    [Fact]
    public async Task Following_a_running_attempt_receives_a_new_line_within_2_seconds_and_ends_when_it_finishes()
    {
        var (s, runId, lease) = await StartTaskAsync();
        await AppendAsync(s, lease, (1, LogLevel.Information, "before"));

        using var request = new HttpRequestMessage(HttpMethod.Get, Url($"/api/runs/{runId}/tasks/a/logs"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var events = SseParser.Create(stream).EnumerateAsync().GetAsyncEnumerator();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var existing = await NextAsync(events, timeout.Token);
        Assert.Equal(("line", "1"), (existing.EventType, existing.EventId));

        var written = Stopwatch.StartNew();
        await AppendAsync(s, lease, (2, LogLevel.Warning, "live"));
        var live = await NextAsync(events, timeout.Token);
        written.Stop();

        Assert.Equal("live", JsonDocument.Parse(live.Data).RootElement.GetProperty("message").GetString());
        Assert.Equal("2", live.EventId);
        Assert.True(written.Elapsed < TimeSpan.FromSeconds(2), $"Line arrived after {written.Elapsed.TotalMilliseconds:F0} ms.");

        Assert.True(await s.Dispatch.CompleteAsync(lease));
        Assert.Equal("end", (await NextAsync(events, timeout.Token)).EventType);
        Assert.False(await events.MoveNextAsync());
    }

    [Fact]
    public async Task Stream_resumes_after_the_last_event_id()
    {
        var (s, runId, lease) = await StartTaskAsync();
        await AppendAsync(s, lease, (1, LogLevel.Information, "one"), (2, LogLevel.Information, "two"), (3, LogLevel.Information, "three"));
        Assert.True(await s.Dispatch.CompleteAsync(lease));

        using var request = new HttpRequestMessage(HttpMethod.Get, Url($"/api/runs/{runId}/tasks/a/logs"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("Last-Event-ID", "2");
        using var response = await Http.SendAsync(request);
        var items = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(await response.Content.ReadAsStreamAsync()).EnumerateAsync())
            items.Add(item);

        Assert.Equal([("line", "3"), ("end", null)], items.Select(i => (i.EventType, i.EventId)));
    }

    private Uri Url(string path) => new(_api.BaseAddress, path);

    private async Task<(RunScenario S, Guid RunId, ClaimedTask Lease)> StartTaskAsync()
    {
        var s = await RunScenario.CreateAsync(pg);
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.Dispatch.ClaimOneAsync(host);
        Assert.True(await s.Dispatch.Leases.StartAsync(s.GroupId, lease.TaskRunId, lease.LeaseToken, CancellationToken.None));
        return (s, runId, lease);
    }

    private static async Task AppendAsync(RunScenario s, ClaimedTask lease, params (int Seq, LogLevel Level, string Message)[] lines) =>
        Assert.Equal(lines.Length, await s.Dispatch.Leases.AppendLogsAsync(s.GroupId, lease.TaskRunId, lease.Attempt, lease.LeaseToken,
            [.. lines.Select(l => new TaskLogLine(l.Seq, DateTime.UtcNow, (int)l.Level, l.Message))], CancellationToken.None));

    private static async Task<SseItem<string>> NextAsync(IAsyncEnumerator<SseItem<string>> events, CancellationToken ct)
    {
        var next = events.MoveNextAsync().AsTask();
        await next.WaitAsync(ct);
        Assert.True(next.Result, "The stream ended early.");
        return events.Current;
    }
}
