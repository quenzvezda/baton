using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Baton.Domain.Workflows;

/// <summary>
/// The one set of serializer options for workflow params and task outputs: web defaults (camelCase),
/// nullable annotations and required constructor parameters respected. The params JSON Schema is
/// exported with the same options, so the schema and the runtime agree (AUT-03).
/// </summary>
public static class WorkflowJson
{
    private const string UnsupportedTypeComment = "Unsupported .NET type";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// The JSON Schema of <paramref name="paramsType"/>, or <c>null</c> with <paramref name="error"/> set
    /// when the type cannot round-trip through System.Text.Json (WF-03).
    /// </summary>
    public static string? TryExportParamsSchema(Type paramsType, out string? error)
    {
        ArgumentNullException.ThrowIfNull(paramsType);

        if (paramsType.IsAbstract || paramsType.IsInterface || paramsType.IsGenericTypeDefinition)
        {
            error = $"params type '{paramsType}' is abstract and cannot be deserialized";
            return null;
        }

        JsonNode schema;
        try
        {
            schema = JsonSchemaExporter.GetJsonSchemaAsNode(
                Options, paramsType, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            error = $"params type '{paramsType}' has no JSON Schema: {ex.Message}";
            return null;
        }

        // The exporter does not throw for members it cannot serialize; it marks them instead.
        var unsupported = FindUnsupported(schema, "#").ToList();
        if (unsupported.Count > 0)
        {
            error = $"params type '{paramsType}' cannot be serialized to JSON: unsupported member at {string.Join(", ", unsupported)}";
            return null;
        }

        error = null;
        return schema.ToJsonString();
    }

    private static IEnumerable<string> FindUnsupported(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$comment"]?.GetValue<string>() == UnsupportedTypeComment)
                {
                    yield return path;
                    yield break;
                }

                foreach (var (name, child) in obj)
                {
                    foreach (var found in FindUnsupported(child, $"{path}/{name}"))
                        yield return found;
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    foreach (var found in FindUnsupported(array[i], $"{path}/{i}"))
                        yield return found;
                }

                break;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.MakeReadOnly();
        return options;
    }
}
