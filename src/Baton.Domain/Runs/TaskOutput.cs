using System.Text.Json;
using Baton.Abstractions;
using Baton.Domain.Workflows;

namespace Baton.Domain.Runs;

/// <summary>
/// WF-07: a task's return value is stored as JSON in <c>task_runs.output</c>, at most 256 KB. Checked
/// where the agent serializes the output and again when the server accepts Complete.
/// </summary>
public static class TaskOutput
{
    public const int MaxBytes = 256 * 1024;

    /// <summary>Serializes a handler's return value with <see cref="WorkflowJson.Options"/> and enforces the limit.</summary>
    /// <exception cref="NonRetryableException">The output is over the limit; it is deterministic, so a retry would fail the same way.</exception>
    public static byte[] Serialize(object? output, Type outputType, string taskKey)
    {
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(output, outputType, WorkflowJson.Options);
        EnsureWithinLimit(utf8.Length, taskKey);
        return utf8;
    }

    /// <exception cref="NonRetryableException"><paramref name="byteCount"/> is over <see cref="MaxBytes"/>.</exception>
    public static void EnsureWithinLimit(int byteCount, string taskKey)
    {
        if (byteCount > MaxBytes)
        {
            throw new NonRetryableException(
                $"Output of task '{taskKey}' is too large: {byteCount} bytes, the limit is {MaxBytes} (256 KB). " +
                "Store large data elsewhere and return a reference (URI).");
        }
    }
}
