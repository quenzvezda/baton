using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Baton.Persistence.Runs;

namespace Baton.Orchestrator.Notifications;

/// <summary>A failed task as a notification lists it.</summary>
public sealed record FailedTask(string Key, int Attempt, string? Message);

/// <summary>
/// What a run notification says (OBS-05 "Notification content", add-operability-and-insight design.md decision 20):
/// the automation, the run, its workflow version, its outcome and times, and the failed tasks with their errors.
/// Built from the run detail, so it reads what the console shows.
/// </summary>
public sealed record NotificationMessage(
    Guid AutomationId,
    string AutomationName,
    Guid RunId,
    string Workflow,
    string Outcome,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int Reruns,
    IReadOnlyList<FailedTask> FailedTasks,
    string? ConsoleUrl)
{
    public const int MaxTasks = 20;
    public const int MaxMessageLength = 1_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary><c>run.failed</c> or <c>run.timed_out</c>.</summary>
    public string Type => $"run.{Outcome}";

    public string OutcomeText => Outcome == "timed_out" ? "Timed out" : "Failed";

    public static NotificationMessage From(RunDetail run, string? consoleBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(run);

        var failed = run.Tasks
            .Where(t => t.Status == "failed")
            .Take(MaxTasks)
            .Select(t => new FailedTask(t.Key, t.Attempt, Truncate(ErrorMessage(t.Error))))
            .ToList();
        var console = string.IsNullOrWhiteSpace(consoleBaseUrl) ? null : $"{consoleBaseUrl.TrimEnd('/')}/runs/{run.Id}";
        return new NotificationMessage(
            run.AutomationId ?? Guid.Empty, run.AutomationName ?? "(deleted automation)", run.Id,
            $"{run.WorkflowKey}@{run.WorkflowVersion}", run.Status, run.StartedAt, run.FinishedAt, run.Reruns, failed, console);
    }

    public string Subject => $"[Baton] {AutomationName} {OutcomeText.ToLowerInvariant()}";

    /// <summary>The webhook body (design.md decision 20).</summary>
    public byte[] WebhookBody(string deliveryId) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        id = deliveryId,
        type = Type,
        occurredAt = FinishedAt,
        run = new
        {
            id = RunId,
            automationId = AutomationId,
            automationName = AutomationName,
            workflow = Workflow,
            outcome = Outcome,
            startedAt = StartedAt,
            finishedAt = FinishedAt,
            reruns = Reruns,
            failedTasks = FailedTasks,
            consoleUrl = ConsoleUrl,
        },
    }, Json);

    public string TextBody()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{AutomationName}: run {RunId} {OutcomeText.ToLowerInvariant()}.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Workflow: {Workflow}\n");
        text.Append(CultureInfo.InvariantCulture, $"Started:  {Format(StartedAt)}\n");
        text.Append(CultureInfo.InvariantCulture, $"Finished: {Format(FinishedAt)}\n");
        if (Reruns > 0)
            text.Append(CultureInfo.InvariantCulture, $"Re-runs:  {Reruns}\n");
        if (FailedTasks.Count > 0)
        {
            text.Append("\nFailed tasks:\n");
            foreach (var task in FailedTasks)
                text.Append(CultureInfo.InvariantCulture, $"- {task.Key} (attempt {task.Attempt}): {task.Message ?? "no message"}\n");
        }
        else if (Outcome == "timed_out")
        {
            text.Append("\nThe run passed its timeout; its unfinished tasks were cancelled.\n");
        }

        if (ConsoleUrl is not null)
            text.Append(CultureInfo.InvariantCulture, $"\n{ConsoleUrl}\n");
        return text.ToString();
    }

    public string HtmlBody()
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"<p><strong>{E(AutomationName)}</strong>: run <code>{RunId}</code> {E(OutcomeText.ToLowerInvariant())}.</p>");
        html.Append(CultureInfo.InvariantCulture,
            $"<table><tr><td>Workflow</td><td>{E(Workflow)}</td></tr><tr><td>Started</td><td>{E(Format(StartedAt))}</td></tr><tr><td>Finished</td><td>{E(Format(FinishedAt))}</td></tr></table>");
        if (FailedTasks.Count > 0)
        {
            html.Append("<p>Failed tasks:</p><ul>");
            foreach (var task in FailedTasks)
                html.Append(CultureInfo.InvariantCulture, $"<li><code>{E(task.Key)}</code> (attempt {task.Attempt}): {E(task.Message ?? "no message")}</li>");
            html.Append("</ul>");
        }

        if (ConsoleUrl is not null)
            html.Append(CultureInfo.InvariantCulture, $"<p><a href=\"{E(ConsoleUrl)}\">Open the run</a></p>");
        return html.ToString();
    }

    private static string Format(DateTimeOffset? time) =>
        time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "-";

    private static string? ErrorMessage(JsonElement? error) =>
        error is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : null;

    private static string? Truncate(string? value) =>
        value is { Length: > MaxMessageLength } ? value[..MaxMessageLength] + "…" : value;
}
