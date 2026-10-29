using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baton.Web.Forms;

/// <summary>How a parameter is edited. Anything the form cannot render is edited as JSON (design.md "Parameter form").</summary>
public enum FieldKind
{
    Text,
    WholeNumber,
    Number,
    Boolean,
    Choice,
    Date,
    DateTime,
    TextList,
    Json,
}

/// <param name="Required">A value must be given: the property is required and does not accept null.</param>
/// <param name="Listed">The property is in the schema's <c>required</c> list (it may still accept null).</param>
public sealed record ParameterField(
    string Name,
    string Label,
    FieldKind Kind,
    bool Required,
    bool Nullable,
    bool Listed,
    IReadOnlyList<JsonNode?> Choices,
    string? Description,
    JsonNode? Default);

/// <summary>
/// A form generated from a workflow's params JSON Schema (AUT-03), as exported by the SDK
/// (<c>WorkflowJson.TryExportParamsSchema</c>): an object whose properties are strings, numbers, booleans, enums,
/// dates and arrays of strings get their own input; anything else gets a JSON input. Values are kept as text so a
/// rejected save keeps what was typed. The server's validation stays authoritative.
/// </summary>
public sealed class ParameterForm
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ParameterForm(IReadOnlyList<ParameterField> fields, bool isSupported)
    {
        Fields = fields;
        IsSupported = isSupported;
    }

    public IReadOnlyList<ParameterField> Fields { get; }

    /// <summary><c>false</c> when the schema is not an object of properties; the whole value is then edited as JSON.</summary>
    public bool IsSupported { get; }

    /// <summary>Text per field name; booleans are <c>true</c>/<c>false</c>, lists one item per line.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>The whole parameters object, used when <see cref="IsSupported"/> is <c>false</c>.</summary>
    public string RawJson { get; set; } = "{}";

    public static ParameterForm FromSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !Types(schema).Contains("object"))
            return new ParameterForm([], isSupported: false);

        var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(r => r.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];

        var fields = new List<ParameterField>();
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
                fields.Add(Field(property.Name, property.Value, required.Contains(property.Name)));
        }

        return new ParameterForm(fields, isSupported: true);
    }

    /// <summary>Fills the inputs from saved parameters; fields the parameters leave out show their default.</summary>
    public void Load(JsonElement? parameters)
    {
        Values.Clear();
        var saved = parameters is { ValueKind: JsonValueKind.Object } p ? JsonNode.Parse(p.GetRawText())!.AsObject() : [];
        RawJson = saved.ToJsonString(Json);

        foreach (var field in Fields)
        {
            var value = saved.TryGetPropertyValue(field.Name, out var node) ? node : field.Default;
            Values[field.Name] = ToText(field, value);
        }
    }

    /// <summary>Builds the parameters object, or names the fields whose text cannot be converted.</summary>
    public bool TryBuild(out JsonObject parameters, out Dictionary<string, string> errors)
    {
        parameters = [];
        errors = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!IsSupported)
        {
            try
            {
                if (JsonNode.Parse(string.IsNullOrWhiteSpace(RawJson) ? "{}" : RawJson) is JsonObject whole)
                {
                    parameters = whole;
                    return true;
                }
                errors[""] = "Parameters must be a JSON object.";
            }
            catch (JsonException)
            {
                errors[""] = "Not valid JSON.";
            }
            return false;
        }

        foreach (var field in Fields)
        {
            var text = Values.GetValueOrDefault(field.Name, "").Trim();
            if (field.Kind != FieldKind.Boolean && field.Kind != FieldKind.TextList && text.Length == 0)
            {
                if (field.Required)
                    errors[field.Name] = "Required.";
                else if (field.Listed)
                    parameters[field.Name] = null;
                continue;
            }

            if (TryConvert(field, text, out var value, out var error))
                parameters[field.Name] = value;
            else if (error is not null)
                errors[field.Name] = error;
        }

        return errors.Count == 0;
    }

    /// <summary>Maps an API error key such as <c>parameters.recipients[0]</c> to its field name, or <c>null</c>.</summary>
    public static string? FieldOf(string errorKey)
    {
        ArgumentNullException.ThrowIfNull(errorKey);
        const string prefix = "parameters.";
        if (!errorKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = errorKey[prefix.Length..];
        var end = rest.IndexOfAny(['.', '[']);
        return end < 0 ? rest : rest[..end];
    }

    private static bool TryConvert(ParameterField field, string text, out JsonNode? value, out string? error)
    {
        value = null;
        error = null;
        switch (field.Kind)
        {
            case FieldKind.WholeNumber:
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                    value = integer;
                else
                    error = "Must be a whole number.";
                break;
            case FieldKind.Number:
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    value = number;
                else
                    error = "Must be a number.";
                break;
            case FieldKind.Boolean:
                value = string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
                break;
            case FieldKind.Choice:
                value = field.Choices.FirstOrDefault(c => ChoiceText(c) == text)?.DeepClone();
                if (value is null)
                    error = "Pick one of the listed values.";
                break;
            case FieldKind.Date:
                if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    value = text;
                else
                    error = "Use the format yyyy-MM-dd.";
                break;
            case FieldKind.DateTime:
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && HasOffset(text))
                    value = at.ToString("O", CultureInfo.InvariantCulture);
                else
                    error = "Use ISO 8601 with an offset, e.g. 2026-10-28T09:00:00+07:00.";
                break;
            case FieldKind.TextList:
                var items = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                if (items.Count == 0 && !field.Listed)
                    return false; // omitted: the server applies the default
                value = items.Count == 0 && field.Nullable ? null : new JsonArray([.. items.Select(i => (JsonNode)i)]);
                break;
            case FieldKind.Json:
                try
                {
                    value = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    error = "Not valid JSON.";
                }
                break;
            default:
                value = text;
                break;
        }
        return error is null;
    }

    private static bool HasOffset(string text) =>
        text.EndsWith('Z') || text.EndsWith('z') || (text.Length > 6 && text[^6] is '+' or '-' && text[^3] == ':');

    private static string ToText(ParameterField field, JsonNode? value)
    {
        if (value is null)
            return field.Kind == FieldKind.Boolean ? "false" : "";

        return field.Kind switch
        {
            FieldKind.Boolean => value.GetValueKind() == JsonValueKind.True ? "true" : "false",
            FieldKind.TextList when value is JsonArray array => string.Join('\n', array.Select(i => i?.GetValueKind() == JsonValueKind.String ? i.GetValue<string>() : i?.ToJsonString())),
            FieldKind.Json => value.ToJsonString(Json),
            FieldKind.Choice => ChoiceText(value),
            _ => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString(Json),
        };
    }

    /// <summary>The text an enum value shows and is matched by.</summary>
    public static string ChoiceText(JsonNode? choice) =>
        choice is null ? "" : choice.GetValueKind() == JsonValueKind.String ? choice.GetValue<string>() : choice.ToJsonString();

    private static ParameterField Field(string name, JsonElement schema, bool listed)
    {
        var types = Types(schema);
        var nullable = types.Contains("null");
        types.Remove("null");
        var description = schema.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var defaultValue = schema.TryGetProperty("default", out var def) ? JsonNode.Parse(def.GetRawText()) : null;
        var format = schema.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;

        List<JsonNode?> choices = [];
        FieldKind kind;
        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            choices = [.. values.EnumerateArray().Where(v => v.ValueKind != JsonValueKind.Null).Select(v => JsonNode.Parse(v.GetRawText()))];
            nullable |= values.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.Null);
            kind = FieldKind.Choice;
        }
        else
        {
            kind = KindOf(types, format, schema);
        }

        return new ParameterField(name, Label(name), kind, listed && !nullable, nullable, listed, choices, description, defaultValue);
    }

    private static FieldKind KindOf(HashSet<string> types, string? format, JsonElement schema)
    {
        // Web defaults read numbers from strings too, so the exporter says ["string", "integer"].
        if (types.Contains("integer") && types.IsSubsetOf(["integer", "string"]))
            return FieldKind.WholeNumber;
        if (types.Contains("number") && types.IsSubsetOf(["number", "integer", "string"]))
            return FieldKind.Number;
        if (types.SetEquals(["boolean"]))
            return FieldKind.Boolean;
        if (types.SetEquals(["string"]))
        {
            return format switch
            {
                "date" => FieldKind.Date,
                "date-time" => FieldKind.DateTime,
                _ => FieldKind.Text,
            };
        }
        if (types.SetEquals(["array"]) && schema.TryGetProperty("items", out var items) && Types(items).SetEquals(["string"])
            && !items.TryGetProperty("format", out _) && !items.TryGetProperty("enum", out _))
        {
            return FieldKind.TextList;
        }
        return FieldKind.Json;
    }

    private static HashSet<string> Types(JsonElement schema)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
            return types;
        if (type.ValueKind == JsonValueKind.String)
            types.Add(type.GetString()!);
        else if (type.ValueKind == JsonValueKind.Array)
            types.UnionWith(type.EnumerateArray().Select(t => t.GetString()).OfType<string>());
        return types;
    }

    /// <summary><c>reportDate</c> → <c>Report date</c>.</summary>
    private static string Label(string name)
    {
        var label = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i == 0)
                label.Append(char.ToUpperInvariant(c));
            else if (char.IsUpper(c))
                label.Append(' ').Append(char.ToLowerInvariant(c));
            else
                label.Append(c);
        }
        return label.ToString();
    }
}
