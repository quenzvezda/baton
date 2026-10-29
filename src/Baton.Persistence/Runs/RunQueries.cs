using System.Text.Json;
using Baton.Domain.Runs;
using Baton.Persistence.Queue;
using Npgsql;
using NpgsqlTypes;

namespace Baton.Persistence.Runs;

/// <summary>One row of the runs list (OBS-01).</summary>
public sealed record RunSummary(
    Guid Id,
    Guid? AutomationId,
    string? AutomationName,
    string WorkflowKey,
    int WorkflowVersion,
    string Status,
    int Priority,
    Guid HostGroupId,
    string HostGroupName,
    string TriggerKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Runs list filters; every one is optional. <see cref="To"/> is exclusive.</summary>
public sealed record RunFilter(
    Guid? AutomationId = null,
    string? WorkflowKey = null,
    IReadOnlyList<string>? Statuses = null,
    Guid? HostGroupId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

/// <param name="NextCursor">Present when more runs match; pass it back to read the next page.</param>
public sealed record RunPage(IReadOnlyList<RunSummary> Items, string? NextCursor);

/// <summary>One attempt of a task: where and when it ran and how it ended (OBS-02).</summary>
public sealed record TaskAttemptView(
    int Attempt,
    Guid HostId,
    string? HostName,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Outcome,
    JsonElement? Error);

/// <summary>A task of a run with its final (or current) state and every attempt (OBS-02).</summary>
public sealed record TaskRunView(
    Guid Id,
    string Key,
    string Status,
    int Attempt,
    int MaxAttempts,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    JsonElement? Output,
    JsonElement? Error,
    IReadOnlyList<TaskAttemptView> Attempts);

/// <summary>A run with its definition's graph and every task (OBS-02).</summary>
public sealed record RunDetail(
    Guid Id,
    Guid? AutomationId,
    string? AutomationName,
    string WorkflowKey,
    int WorkflowVersion,
    string GraphHash,
    JsonElement Graph,
    string Status,
    int Priority,
    Guid HostGroupId,
    string HostGroupName,
    string TriggerKind,
    JsonElement Parameters,
    string? CancelReason,
    string? BlockedOn,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? DeadlineAt,
    IReadOnlyList<TaskRunView> Tasks);

/// <summary>
/// Read side of runs for the API and console (OBS-01, OBS-02). Plain SQL: EF does not model the run tables
/// (ADR-0007). A run that does not exist or was purged (ADR-0011) reads as <c>null</c>.
/// </summary>
public sealed class RunQueries(NpgsqlDataSource dataSource)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <summary>Every run status, as stored and returned.</summary>
    public static readonly IReadOnlyList<string> Statuses = Enum.GetValues<WorkflowRunStatus>().Select(DbNames.Status).ToArray();

    private const string RunColumns = """
        r.id, r.automation_id, a.name, d.key, d.version, r.status, r.priority, r.host_group_id, g.name,
        r.trigger_kind, r.created_at, r.started_at, r.finished_at
        """;

    private const string RunJoins = """
        FROM workflow_runs r
        JOIN workflow_definitions d ON d.id = r.definition_id
        JOIN host_groups g ON g.id = r.host_group_id
        LEFT JOIN automations a ON a.id = r.automation_id
        """;

    // Newest first by (created_at, id), served by ix_runs_recent (or ix_runs_by_automation when filtered by
    // automation); the row-value comparison resumes strictly after the cursor, so pages neither repeat nor skip.
    private const string ListSql = $"""
        SELECT {RunColumns}
        {RunJoins}
        WHERE (@automationId::uuid IS NULL OR r.automation_id = @automationId)
          AND (@workflowKey::text IS NULL OR d.key = @workflowKey)
          AND (@statuses::text[] IS NULL OR r.status = ANY (@statuses))
          AND (@groupId::uuid IS NULL OR r.host_group_id = @groupId)
          AND (@from::timestamptz IS NULL OR r.created_at >= @from)
          AND (@to::timestamptz IS NULL OR r.created_at < @to)
          AND (@afterAt::timestamptz IS NULL OR (r.created_at, r.id) < (@afterAt, @afterId))
        ORDER BY r.created_at DESC, r.id DESC
        LIMIT @take
        """;

    private const string DetailSql = $"""
        SELECT {RunColumns}, d.graph_hash, d.graph::text, r.parameters::text, r.cancel_reason, r.blocked_on, r.deadline_at
        {RunJoins}
        WHERE r.id = @runId;

        SELECT t.id, t.task_key, t.status, t.attempt, t.max_attempts, t.queued_at, t.started_at, t.finished_at,
               t.output::text, t.error::text
        FROM task_runs t
        WHERE t.workflow_run_id = @runId
        ORDER BY t.task_key;

        SELECT ta.task_run_id, ta.attempt, ta.host_id, h.name, ta.started_at, ta.finished_at, ta.outcome, ta.error::text
        FROM task_attempts ta
        JOIN task_runs t ON t.id = ta.task_run_id
        LEFT JOIN hosts h ON h.id = ta.host_id
        WHERE t.workflow_run_id = @runId
        ORDER BY ta.task_run_id, ta.attempt, ta.started_at;
        """;

    /// <param name="limit">Clamped to 1..<see cref="MaxPageSize"/>.</param>
    public async Task<RunPage> ListAsync(RunFilter filter, RunCursor? after, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        limit = Math.Clamp(limit, 1, MaxPageSize);

        await using var cmd = dataSource.CreateCommand(ListSql);
        cmd.Parameters.AddWithValue("automationId", NpgsqlDbType.Uuid, (object?)filter.AutomationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("workflowKey", NpgsqlDbType.Text, (object?)filter.WorkflowKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text,
            filter.Statuses is { Count: > 0 } statuses ? statuses.ToArray() : DBNull.Value);
        cmd.Parameters.AddWithValue("groupId", NpgsqlDbType.Uuid, (object?)filter.HostGroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, (object?)filter.From?.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, (object?)filter.To?.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("afterAt", NpgsqlDbType.TimestampTz, (object?)after?.CreatedAt.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("afterId", NpgsqlDbType.Uuid, (object?)after?.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("take", limit + 1);

        var items = new List<RunSummary>(limit + 1);
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                items.Add(ReadSummary(reader));
        }

        if (items.Count <= limit)
            return new RunPage(items, null);

        items.RemoveAt(limit);
        var last = items[^1];
        return new RunPage(items, new RunCursor(last.CreatedAt, last.Id).Encode());
    }

    public async Task<RunDetail?> FindAsync(Guid runId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(DetailSql);
        cmd.Parameters.AddWithValue("runId", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return null;

        var run = ReadSummary(reader);
        var graphHash = reader.GetString(13);
        var graph = ParseJson(reader.GetString(14));
        var parameters = ParseJson(reader.GetString(15));
        var cancelReason = reader.IsDBNull(16) ? null : reader.GetString(16);
        var blockedOn = reader.IsDBNull(17) ? null : reader.GetString(17);
        var deadlineAt = NullableTime(reader, 18);

        await reader.NextResultAsync(ct);
        var tasks = new List<(Guid Id, TaskRunView View)>();
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            tasks.Add((id, new TaskRunView(
                id,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                NullableTime(reader, 5),
                NullableTime(reader, 6),
                NullableTime(reader, 7),
                NullableJson(reader, 8),
                NullableJson(reader, 9),
                [])));
        }

        await reader.NextResultAsync(ct);
        var attempts = new Dictionary<Guid, List<TaskAttemptView>>();
        while (await reader.ReadAsync(ct))
        {
            var taskRunId = reader.GetGuid(0);
            if (!attempts.TryGetValue(taskRunId, out var list))
                attempts[taskRunId] = list = [];
            list.Add(new TaskAttemptView(
                reader.GetInt32(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                NullableTime(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                NullableJson(reader, 7)));
        }

        return new RunDetail(
            run.Id, run.AutomationId, run.AutomationName, run.WorkflowKey, run.WorkflowVersion, graphHash, graph,
            run.Status, run.Priority, run.HostGroupId, run.HostGroupName, run.TriggerKind, parameters, cancelReason,
            blockedOn, run.CreatedAt, run.StartedAt, run.FinishedAt, deadlineAt,
            [.. tasks.Select(t => t.View with { Attempts = attempts.TryGetValue(t.Id, out var a) ? a : [] })]);
    }

    private static RunSummary ReadSummary(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.IsDBNull(1) ? null : reader.GetGuid(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.GetString(5),
        reader.GetInt16(6),
        reader.GetGuid(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetFieldValue<DateTimeOffset>(10),
        NullableTime(reader, 11),
        NullableTime(reader, 12));

    private static DateTimeOffset? NullableTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static JsonElement? NullableJson(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseJson(reader.GetString(ordinal));

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
