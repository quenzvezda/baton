using Baton.Domain.Runs;
using Npgsql;

namespace Baton.Persistence.Queue;

/// <summary>
/// ORC-06 (M6 design.md decision 9): recomputes the effective priority of queued tasks from how long they have
/// waited, the same formula as <see cref="TaskPriority.Effective"/> with Postgres <c>now()</c>. Only rows whose
/// value changes are written, so a task is written at most once per step, and rows a claim holds are skipped
/// (<c>SKIP LOCKED</c>): the tick never waits, and a skipped row is aged on the next tick.
/// </summary>
public static class PriorityAging
{
    private const string Aged = """
        LEAST(100, priority + LEAST(@cap, GREATEST(0, floor(extract(epoch FROM now() - queued_at) / @stepSeconds))::int))::smallint
        """;

    private static readonly string AgeSql = $"""
        WITH aged AS (
          SELECT id, {Aged} AS p
          FROM task_runs
          WHERE status = 'queued' AND effective_priority <> {Aged}
          FOR UPDATE SKIP LOCKED)
        UPDATE task_runs t SET effective_priority = aged.p FROM aged WHERE t.id = aged.id
        """;

    /// <returns>How many tasks changed effective priority.</returns>
    public static async Task<int> AgeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, AgingSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var command = new NpgsqlCommand(AgeSql, connection, transaction);
        command.Parameters.AddWithValue("cap", settings.Cap);
        command.Parameters.AddWithValue("stepSeconds", settings.Step.TotalSeconds);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
