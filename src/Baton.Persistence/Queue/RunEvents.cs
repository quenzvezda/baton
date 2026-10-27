using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Baton.Persistence.Queue;

/// <summary>
/// Writes <c>run_events</c> (NFR-Auditability) and the <c>tasks_ready</c> notification, always inside the
/// transaction of the transition they describe. <c>at</c> is the transaction's <c>now()</c>.
/// </summary>
internal static class RunEvents
{
    private const string InsertSql = """
        INSERT INTO run_events (workflow_run_id, task_key, type, data) VALUES (@runId, @taskKey, @type, @data)
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static NpgsqlBatchCommand Command(Guid runId, string? taskKey, string type, object? data)
    {
        var command = new NpgsqlBatchCommand(InsertSql);
        command.Parameters.AddWithValue("runId", runId);
        command.Parameters.AddWithValue("taskKey", (object?)taskKey ?? DBNull.Value);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, data is null ? "{}" : JsonSerializer.Serialize(data, Json));
        return command;
    }

    public static async Task AddAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, string? taskKey, string type, object? data,
        CancellationToken ct)
    {
        await using var batch = new NpgsqlBatch(connection, transaction) { BatchCommands = { Command(runId, taskKey, type, data) } };
        await batch.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Delivered on commit; claimers of the group re-check at once (design spec §7 "Operational notes").</summary>
    public static NpgsqlBatchCommand NotifyTasksReady(Guid hostGroupId)
    {
        var command = new NpgsqlBatchCommand("SELECT pg_notify(@channel, @group)");
        command.Parameters.AddWithValue("channel", DbNames.TasksReadyChannel);
        command.Parameters.AddWithValue("group", hostGroupId.ToString());
        return command;
    }
}
