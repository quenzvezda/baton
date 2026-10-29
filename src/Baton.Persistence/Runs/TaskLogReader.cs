using Npgsql;

namespace Baton.Persistence.Runs;

/// <summary>A handler log line (OBS-03). <see cref="Level"/> is a <c>Microsoft.Extensions.Logging.LogLevel</c> name.</summary>
public sealed record LogLineView(int Seq, DateTimeOffset LoggedAt, string Level, string Message);

/// <summary>The attempt whose logs are read, and whether it can still write more.</summary>
/// <param name="Attempt">0 when the task has not been claimed yet (it has no logs).</param>
/// <param name="IsLive">The attempt is the task's current one and is Leased or Running.</param>
public sealed record LogTarget(Guid TaskRunId, int Attempt, bool IsLive);

/// <summary>Reads <c>task_logs</c> per task attempt in the order written (OBS-03).</summary>
public sealed class TaskLogReader(NpgsqlDataSource dataSource)
{
    public const int DefaultPageSize = 1000;
    public const int MaxPageSize = 5000;

    private static readonly string[] LevelNames = ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];

    private const string TargetSql = """
        SELECT t.id, t.attempt, t.status IN ('leased', 'running')
        FROM task_runs t
        WHERE t.workflow_run_id = @runId AND t.task_key = @taskKey
        """;

    private const string LiveSql = """
        SELECT t.attempt = @attempt AND t.status IN ('leased', 'running') FROM task_runs t WHERE t.id = @id
        """;

    // A retried log chunk can land twice when the agent's clock was clamped (TaskLeases.AppendLogsSql), so one
    // line per seq.
    private const string ReadSql = """
        SELECT DISTINCT ON (l.seq) l.seq, l.logged_at, l.level, l.message
        FROM task_logs l
        WHERE l.task_run_id = @id AND l.attempt = @attempt AND l.seq > @afterSeq
        ORDER BY l.seq, l.logged_at
        LIMIT @take
        """;

    /// <param name="attempt">The attempt to read; <c>null</c> for the task's latest.</param>
    /// <returns><c>null</c> when the run has no such task (or was purged), or the attempt does not exist.</returns>
    public async Task<LogTarget?> ResolveAsync(Guid runId, string taskKey, int? attempt, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(TargetSql);
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.Parameters.AddWithValue("taskKey", taskKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var id = reader.GetGuid(0);
        var current = reader.GetInt32(1);
        var active = reader.GetBoolean(2);
        var requested = attempt ?? current;
        if (requested < 0 || requested > current || (attempt is not null && requested == 0))
            return null;

        return new LogTarget(id, requested, active && requested == current);
    }

    /// <summary>Whether the attempt is still the task's current, Leased or Running one.</summary>
    public async Task<bool> IsLiveAsync(LogTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var cmd = dataSource.CreateCommand(LiveSql);
        cmd.Parameters.AddWithValue("id", target.TaskRunId);
        cmd.Parameters.AddWithValue("attempt", target.Attempt);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Lines with a sequence number above <paramref name="afterSeq"/>, in order.</summary>
    /// <param name="limit">Clamped to 1..<see cref="MaxPageSize"/>.</param>
    public async Task<IReadOnlyList<LogLineView>> ReadAsync(LogTarget target, int afterSeq, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Attempt == 0)
            return [];

        await using var cmd = dataSource.CreateCommand(ReadSql);
        cmd.Parameters.AddWithValue("id", target.TaskRunId);
        cmd.Parameters.AddWithValue("attempt", target.Attempt);
        cmd.Parameters.AddWithValue("afterSeq", afterSeq);
        cmd.Parameters.AddWithValue("take", Math.Clamp(limit, 1, MaxPageSize));

        var lines = new List<LogLineView>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var level = reader.GetInt16(2);
            lines.Add(new LogLineView(
                reader.GetInt32(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                level >= 0 && level < LevelNames.Length ? LevelNames[level] : level.ToString(System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(3)));
        }
        return lines;
    }
}
