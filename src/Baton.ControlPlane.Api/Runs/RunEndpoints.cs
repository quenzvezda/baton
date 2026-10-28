using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Baton.ControlPlane.Api.Security;
using Baton.Persistence.Queue;
using Baton.Persistence.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Baton.ControlPlane.Api.Runs;

/// <param name="Status"><c>cancelled</c> for a Pending run, else <c>cancelling</c> until its leased tasks stop.</param>
public sealed record CancelAccepted(Guid RunId, string Status);

/// <summary>A page of an attempt's log (OBS-03).</summary>
/// <param name="IsLive">The attempt can still write lines.</param>
/// <param name="NextAfterSeq">Present when the page was full; pass it as <c>afterSeq</c> to read on.</param>
public sealed record LogPage(int Attempt, bool IsLive, IReadOnlyList<LogLineView> Lines, int? NextAfterSeq);

/// <summary>Runs query (OBS-01), run detail (OBS-02), task logs with live tail (OBS-03) and cancellation (ORC-11).</summary>
public static class RunEndpoints
{
    /// <summary>How often a live tail looks for new lines; well inside the 2 s budget (design.md decision 5).</summary>
    internal static readonly TimeSpan LogPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>SSE event type of a log line; its <c>id</c> is the line's sequence number.</summary>
    public const string LineEvent = "line";

    /// <summary>SSE event type sent once the attempt has finished and every line was sent; the stream then ends.</summary>
    public const string EndEvent = "end";

    public static IEndpointRouteBuilder MapRuns(this IEndpointRouteBuilder app)
    {
        var runs = app.MapGroup("/api/runs");
        runs.MapGet("/", ListAsync).RequireAuthorization(BatonRoles.Viewer);
        runs.MapGet("/{id:guid}", GetAsync).RequireAuthorization(BatonRoles.Viewer);
        runs.MapGet("/{id:guid}/tasks/{key}/logs", LogsAsync).RequireAuthorization(BatonRoles.Viewer);
        runs.MapPost("/{id:guid}/cancel", CancelAsync).RequireAuthorization(BatonRoles.Operator);
        return app;
    }

    /// <summary>OBS-01: newest first; <c>status</c> may repeat; <c>to</c> is exclusive.</summary>
    private static async Task<Results<Ok<RunPage>, ValidationProblem>> ListAsync(
        RunQueries queries, CancellationToken ct,
        Guid? automationId = null, string? workflowKey = null, string[]? status = null, Guid? hostGroupId = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? cursor = null, int? limit = null)
    {
        var errors = new Dictionary<string, string[]>();
        var statuses = status?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToLowerInvariant()).Distinct().ToArray() ?? [];
        if (statuses.Except(RunQueries.Statuses).Any())
            errors["status"] = [$"Unknown status. Use one of: {string.Join(", ", RunQueries.Statuses)}."];

        RunCursor? after = null;
        if (cursor is not null && !RunCursor.TryDecode(cursor, out after))
            errors["cursor"] = ["Not a cursor returned by this API."];
        if (limit is < 1 or > RunQueries.MaxPageSize)
            errors["limit"] = [$"Must be between 1 and {RunQueries.MaxPageSize}."];
        if (from is not null && to is not null && from >= to)
            errors["to"] = ["Must be after 'from'."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        var filter = new RunFilter(automationId, string.IsNullOrWhiteSpace(workflowKey) ? null : workflowKey, statuses, hostGroupId, from, to);
        return TypedResults.Ok(await queries.ListAsync(filter, after, limit ?? RunQueries.DefaultPageSize, ct));
    }

    /// <summary>OBS-02: 404 when the run does not exist or was purged (ADR-0011).</summary>
    private static async Task<Results<Ok<RunDetail>, NotFound>> GetAsync(Guid id, RunQueries queries, CancellationToken ct) =>
        await queries.FindAsync(id, ct) is { } run ? TypedResults.Ok(run) : TypedResults.NotFound();

    /// <summary>
    /// OBS-03: one attempt's log (default: the latest attempt). With <c>Accept: text/event-stream</c> the lines
    /// stream as SSE (resuming after <c>Last-Event-ID</c>) and new lines follow while the attempt runs;
    /// otherwise a page of up to <c>limit</c> lines after <c>afterSeq</c>.
    /// </summary>
    private static async Task<IResult> LogsAsync(
        Guid id, string key, HttpRequest request, TaskLogReader logs, CancellationToken ct,
        int? attempt = null, int? afterSeq = null, int? limit = null)
    {
        if (limit is < 1 or > TaskLogReader.MaxPageSize)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = [$"Must be between 1 and {TaskLogReader.MaxPageSize}."],
            });
        }

        if (await logs.ResolveAsync(id, key, attempt, ct) is not { } target)
            return TypedResults.NotFound();

        if (request.GetTypedHeaders().Accept.Any(a => a.MediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)))
        {
            var resumeAfter = afterSeq
                ?? (int.TryParse(request.Headers["Last-Event-ID"], NumberStyles.None, CultureInfo.InvariantCulture, out var last) ? last : 0);
            return TypedResults.ServerSentEvents(TailAsync(logs, target, resumeAfter, ct));
        }

        var pageSize = limit ?? TaskLogReader.DefaultPageSize;
        var lines = await logs.ReadAsync(target, afterSeq ?? 0, pageSize, ct);
        return TypedResults.Ok(new LogPage(
            target.Attempt, await logs.IsLiveAsync(target, ct), lines, lines.Count == pageSize ? lines[^1].Seq : null));
    }

    /// <summary>
    /// Polls for lines after the last one sent. Liveness is read before the lines, so once a poll sees the attempt
    /// finished, the lines read after it are the last ones (the agent can no longer append, ADR-0006).
    /// </summary>
    private static async IAsyncEnumerable<SseItem<LogLineView?>> TailAsync(
        TaskLogReader logs, LogTarget target, int afterSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var live = await logs.IsLiveAsync(target, ct);
            IReadOnlyList<LogLineView> lines;
            do
            {
                lines = await logs.ReadAsync(target, afterSeq, TaskLogReader.DefaultPageSize, ct);
                foreach (var line in lines)
                {
                    afterSeq = line.Seq;
                    yield return new SseItem<LogLineView?>(line, LineEvent) { EventId = line.Seq.ToString(CultureInfo.InvariantCulture) };
                }
            }
            while (lines.Count == TaskLogReader.DefaultPageSize);

            if (!live)
            {
                yield return new SseItem<LogLineView?>(null, EndEvent);
                yield break;
            }

            await Task.Delay(LogPollInterval, ct);
        }
    }

    /// <summary>ORC-11: 202 while the run stops, 409 when it had already finished.</summary>
    private static async Task<Results<Accepted<CancelAccepted>, NotFound, Conflict<ProblemDetails>>> CancelAsync(
        Guid id, RunCancellation cancellation, CancellationToken ct) =>
        await cancellation.CancelAsync(id, RunCancelReason.User, ct) switch
        {
            CancelOutcome.NotFound => TypedResults.NotFound(),
            CancelOutcome.AlreadyFinished => TypedResults.Conflict(new ProblemDetails
            {
                Title = "Run already finished",
                Detail = "Only a pending or running run can be cancelled.",
                Status = StatusCodes.Status409Conflict,
            }),
            CancelOutcome.Cancelled => TypedResults.Accepted((string?)null, new CancelAccepted(id, "cancelled")),
            _ => TypedResults.Accepted((string?)null, new CancelAccepted(id, "cancelling")),
        };
}
