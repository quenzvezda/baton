using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Baton.Domain.Workflows;

/// <summary>
/// WF-04: the graph as canonical JSON and its SHA-256. Fixed contract, written by hand so no serializer
/// setting can change it: properties in a fixed order, tasks sorted ordinally by key, <c>dependsOn</c>
/// and <c>requiredLabels</c> sorted and de-duplicated, every option emitted (defaults included),
/// durations as invariant <c>c</c>-format strings, types by namespace-qualified name, no indentation.
/// The display name is a label and is left out. Any change to this output is a hash change for every
/// registered definition; the golden-file test locks it.
/// </summary>
public static class CanonicalGraph
{
    public static string ToJson(WorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("key", graph.Key);
            writer.WriteNumber("version", graph.Version);
            writer.WriteString("params", TypeName(graph.ParamsType));
            writer.WriteStartArray("tasks");
            foreach (var task in graph.Tasks.OrderBy(t => t.Key, StringComparer.Ordinal))
                WriteTask(writer, task);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 canonical JSON (64 characters, <c>graph_hash</c>).</summary>
    public static string Hash(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    private static void WriteTask(Utf8JsonWriter writer, TaskNode task)
    {
        writer.WriteStartObject();
        writer.WriteString("key", task.Key);
        writer.WriteString("handler", TypeName(task.HandlerType));
        WriteSortedSet(writer, "dependsOn", task.DependsOn);

        writer.WriteStartObject("retry");
        writer.WriteNumber("maxAttempts", task.Retry.MaxAttempts);
        writer.WriteString("initialDelay", Duration(task.Retry.InitialDelay));
        writer.WriteNumber("multiplier", task.Retry.Multiplier);
        writer.WriteString("maxDelay", Duration(task.Retry.MaxDelay));
        writer.WriteEndObject();

        writer.WriteString("timeout", Duration(task.Timeout));
        writer.WriteString("triggerRule", task.TriggerRule.ToString());
        WriteSortedSet(writer, "requiredLabels", task.RequiredLabels);
        writer.WriteNumber("priorityOffset", task.PriorityOffset);
        writer.WriteEndObject();
    }

    private static void WriteSortedSet(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static string Duration(TimeSpan value) => value.ToString("c");

    /// <summary>
    /// <see cref="Type.FullName"/> without assembly names: generic arguments in <c>FullName</c> carry
    /// assembly versions, which would change the hash on every package bump.
    /// </summary>
    private static string TypeName(Type type)
    {
        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var name = definition[..definition.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(",", type.GetGenericArguments().Select(TypeName))}>";
    }
}
