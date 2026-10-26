using System.Text.Json;

namespace Baton.Persistence.Queue;

/// <summary>The JSON stored in <c>task_runs.error</c> and <c>task_attempts.error</c>.</summary>
public static class TaskErrors
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Json(string type, string message, string? stackTrace = null) =>
        JsonSerializer.Serialize(new { type, message, stackTrace }, Options);
}
