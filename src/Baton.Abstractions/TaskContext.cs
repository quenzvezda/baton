using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Baton.Abstractions;

public sealed class TaskContext<TParams>
{
    public required Guid RunId { get; init; }
    public required string TaskKey { get; init; }
    public required int Attempt { get; init; }
    public required TParams Params { get; init; }

    /// <summary>Lines written here are captured into the task's logs (OBS-03).</summary>
    public required ILogger Logger { get; init; }

    /// <summary>Serialized outputs of this task's ancestors only, keyed by task key.</summary>
    public required IReadOnlyDictionary<string, JsonElement> UpstreamOutputs { get; init; }

    public JsonSerializerOptions? SerializerOptions { get; init; }

    /// <summary>Stable across retries of the same task in the same run.</summary>
    public string IdempotencyKey => $"{RunId}:{TaskKey}";

    /// <summary>Reads an ancestor's output. Throws when <paramref name="upstreamTaskKey"/> is not an ancestor (WF-07).</summary>
    public T GetOutput<T>(string upstreamTaskKey)
    {
        if (!UpstreamOutputs.TryGetValue(upstreamTaskKey, out var json))
        {
            throw new InvalidOperationException(
                $"Task '{TaskKey}' cannot read the output of '{upstreamTaskKey}': it is not an ancestor.");
        }

        return json.Deserialize<T>(SerializerOptions)
            ?? throw new InvalidOperationException($"Output of '{upstreamTaskKey}' is null.");
    }
}
