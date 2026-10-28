using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Baton.ControlPlane.Api.Automations;

/// <summary>
/// Automation parameters against the selected definition's params JSON Schema (AUT-03, ADR-0016). Errors are
/// keyed by field, <c>parameters.region</c> or <c>parameters.recipients[0]</c>; a missing required property is
/// reported under its own name. Schemas are parsed once per process: a registered schema never changes.
/// </summary>
public static class ParameterValidator
{
    private const string Root = "parameters";

    // Summaries of their children's errors, which are reported instead.
    private static readonly HashSet<string> SummaryKeywords = new(StringComparer.Ordinal) { "properties", "items", "prefixItems" };

    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    public static Dictionary<string, string[]> Validate(string schemaJson, JsonElement parameters)
    {
        ArgumentNullException.ThrowIfNull(schemaJson);

        var schema = Schemas.GetOrAdd(schemaJson, static json => JsonSchema.FromText(json));
        var results = schema.Evaluate(parameters, Options);
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (results.IsValid)
            return [];

        JsonNode? schemaNode = null;
        foreach (var detail in results.Details ?? [])
        {
            if (detail.Errors is not { Count: > 0 })
                continue;

            var location = Segments(detail.InstanceLocation.ToString());
            foreach (var (keyword, message) in detail.Errors)
            {
                if (SummaryKeywords.Contains(keyword))
                    continue;

                if (keyword == "required")
                {
                    schemaNode ??= JsonNode.Parse(schemaJson);
                    var missing = MissingRequired(schemaNode, Segments(detail.EvaluationPath.ToString()), parameters, location);
                    if (missing.Count > 0)
                    {
                        foreach (var name in missing)
                            Add(errors, FieldPath([.. location, name]), "Required.");
                        continue;
                    }
                }

                Add(errors, FieldPath(location), message);
            }
        }

        if (errors.Count == 0)
            Add(errors, Root, "Does not match the workflow's parameter schema.");

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
    }

    private static void Add(Dictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out var messages))
            errors[field] = messages = [];
        messages.Add(message);
    }

    /// <summary>The <c>required</c> names at the evaluated schema location that the instance object lacks.</summary>
    private static List<string> MissingRequired(JsonNode? schema, List<string> schemaPath, JsonElement instance, List<string> location)
    {
        var node = schema;
        foreach (var segment in schemaPath)
            node = node is JsonObject obj ? obj[segment] : node is JsonArray array && int.TryParse(segment, out var i) && i < array.Count ? array[i] : null;

        var target = instance;
        foreach (var segment in location)
        {
            if (target.ValueKind == JsonValueKind.Object && target.TryGetProperty(segment, out var child))
                target = child;
            else if (target.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var i) && i < target.GetArrayLength())
                target = target[i];
            else
                return [];
        }

        if (node?["required"] is not JsonArray required || target.ValueKind != JsonValueKind.Object)
            return [];

        return [.. required.Select(r => r?.GetValue<string>()).OfType<string>().Where(name => !target.TryGetProperty(name, out _))];
    }

    private static List<string> Segments(string pointer) =>
        [.. pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];

    private static string FieldPath(IEnumerable<string> segments)
    {
        var path = Root;
        foreach (var segment in segments)
            path += int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? $"[{segment}]" : $".{segment}";
        return path;
    }
}
