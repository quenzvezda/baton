using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Baton.Web.Auth;

namespace Baton.Web.Api;

/// <summary>
/// Typed client for the Control Plane API. It sets the signed-in person's access token on every request itself:
/// typed clients are resolved in the circuit's scope, message handlers are not (ADR-0012).
/// </summary>
public sealed class BatonApiClient(HttpClient http, AccessTokenProvider tokens) : IBatonApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Bound for every request but a log tail, which streams while the task runs.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public Task<CurrentUser> GetMeAsync(CancellationToken ct = default) => GetAsync<CurrentUser>("api/me", ct);

    public Task<IReadOnlyList<WorkflowSummary>> ListWorkflowsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<WorkflowSummary>>("api/workflows", ct);

    public Task<WorkflowVersionDetail?> GetWorkflowVersionAsync(string key, int version, CancellationToken ct = default) =>
        FindAsync<WorkflowVersionDetail>($"api/workflows/{Uri.EscapeDataString(key)}/versions/{version}", ct);

    public Task<IReadOnlyList<HostGroup>> ListHostGroupsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<HostGroup>>("api/host-groups", ct);

    public Task<IReadOnlyList<HostInfo>> ListHostsAsync(CancellationToken ct = default) => GetAsync<IReadOnlyList<HostInfo>>("api/hosts", ct);

    public Task DrainHostAsync(Guid hostId, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"api/hosts/{hostId}/drain", ct);

    public Task UndrainHostAsync(Guid hostId, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"api/hosts/{hostId}/undrain", ct);

    public Task<IReadOnlyList<Automation>> ListAutomationsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<Automation>>("api/automations", ct);

    public Task<Automation?> GetAutomationAsync(Guid id, CancellationToken ct = default) => FindAsync<Automation>($"api/automations/{id}", ct);

    public async Task<Automation> CreateAutomationAsync(AutomationRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/automations") { Content = JsonContent.Create(request, options: Json) };
        return await ReadAsync<Automation>(await SendAsync(message, ct), ct);
    }

    public async Task<Automation> UpdateAutomationAsync(Guid id, int version, AutomationRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"api/automations/{id}") { Content = JsonContent.Create(request, options: Json) };
        message.Headers.IfMatch.Add(ETag(version));
        return await ReadAsync<Automation>(await SendAsync(message, ct), ct);
    }

    public async Task<Automation> SetAutomationEnabledAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"api/automations/{id}/{(enabled ? "enable" : "disable")}");
        return await ReadAsync<Automation>(await SendAsync(message, ct), ct);
    }

    public async Task DeleteAutomationAsync(Guid id, int version, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, $"api/automations/{id}");
        message.Headers.IfMatch.Add(ETag(version));
        using var response = await SendAsync(message, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<RunAccepted> RunNowAsync(Guid automationId, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"api/automations/{automationId}/runs");
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await ReadAsync<RunAccepted>(await SendAsync(message, ct), ct);
    }

    public Task<RunPage> ListRunsAsync(RunQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parameters = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                parameters.Add($"{name}={Uri.EscapeDataString(value)}");
        }

        Add("automationId", query.AutomationId?.ToString());
        Add("workflowKey", query.WorkflowKey);
        foreach (var status in query.Statuses ?? [])
            Add("status", status);
        Add("hostGroupId", query.HostGroupId?.ToString());
        Add("from", query.From?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add("to", query.To?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add("cursor", query.Cursor);
        Add("limit", query.Limit?.ToString(CultureInfo.InvariantCulture));
        return GetAsync<RunPage>(parameters.Count == 0 ? "api/runs" : $"api/runs?{string.Join('&', parameters)}", ct);
    }

    public Task<RunDetail?> GetRunAsync(Guid id, CancellationToken ct = default) => FindAsync<RunDetail>($"api/runs/{id}", ct);

    public async Task<CancelAccepted> CancelRunAsync(Guid id, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"api/runs/{id}/cancel");
        return await ReadAsync<CancelAccepted>(await SendAsync(message, ct), ct);
    }

    public Task<LogPage?> GetLogsAsync(Guid runId, string taskKey, int? attempt, int afterSeq = 0, CancellationToken ct = default)
    {
        var query = attempt is { } a ? $"attempt={a}&afterSeq={afterSeq}" : $"afterSeq={afterSeq}";
        return FindAsync<LogPage>($"{LogsPath(runId, taskKey)}?{query}", ct);
    }

    public async IAsyncEnumerable<LogLine> FollowLogsAsync(
        Guid runId, string taskKey, int attempt, int afterSeq, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{LogsPath(runId, taskKey)}?attempt={attempt}&afterSeq={afterSeq}");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await SendAsync(message, ct, HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct))
        {
            if (item.EventType == "end")
                yield break;
            if (item.EventType == "line" && JsonSerializer.Deserialize<LogLine>(item.Data, Json) is { } line)
                yield return line;
        }
    }

    private static string LogsPath(Guid runId, string taskKey) => $"api/runs/{runId}/tasks/{Uri.EscapeDataString(taskKey)}/logs";

    private static EntityTagHeaderValue ETag(int version) => new($"\"{version.ToString(CultureInfo.InvariantCulture)}\"");

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        return await ReadAsync<T>(await SendAsync(message, ct), ct);
    }

    private async Task<T?> FindAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        var response = await SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        return await ReadAsync<T>(response, ct);
    }

    private async Task SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, path);
        using var response = await SendAsync(message, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage message, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        if (await tokens.GetAccessTokenAsync(ct) is { } token)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (completion == HttpCompletionOption.ResponseHeadersRead)
            return await http.SendAsync(message, completion, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        return await http.SendAsync(message, completion, timeout.Token);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            await EnsureSuccessAsync(response, ct);
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new ApiException((int)response.StatusCode, "The API returned an empty body.");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var status = (int)response.StatusCode;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired. Sign in again.",
            HttpStatusCode.Forbidden => "Your role does not allow this.",
            HttpStatusCode.PreconditionFailed => "Someone else changed this in the meantime. Reload and try again.",
            _ => $"The API answered {status}.",
        };
        Dictionary<string, string[]>? errors = null;

        if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
        {
            try
            {
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = problem.RootElement;
                var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
                var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
                if (title is not null || detail is not null)
                    message = new StringBuilder().AppendJoin(": ", new[] { title, detail }.Where(s => !string.IsNullOrEmpty(s))).ToString();
                if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
                    errors = e.Deserialize<Dictionary<string, string[]>>(Json);
            }
            catch (JsonException)
            {
                // Not a problem document; keep the generic message.
            }
        }

        throw new ApiException(status, message, errors);
    }
}
