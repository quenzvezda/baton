using System.Text.Json;

namespace Baton.Web.Audit;

/// <summary>One top-level field of an audited entity that differs between before and after, as display text.</summary>
public sealed record FieldChange(string Field, string Before, string After);

/// <summary>
/// AUT-11: what an audit entry changed. Compares the top-level fields of the entity's before and after bodies; nested
/// values are compared as JSON and shown as their JSON text.
/// </summary>
public static class AuditChanges
{
    /// <summary>Shown for a field that is absent or <c>null</c>.</summary>
    public const string Empty = "—";

    /// <summary>The extra field of an update entry naming the webhook channels whose (write-only) URL changed.</summary>
    public const string ChangedWebhookUrlsField = "changedWebhookUrls";

    private const int MaxValueLength = 300;

    private static readonly JsonElement NullValue = JsonElement.Parse("null");

    /// <summary>Fields every change moves on, which say nothing about what changed.</summary>
    private static readonly HashSet<string> Bookkeeping = new(StringComparer.Ordinal) { "updatedAt", "version", ChangedWebhookUrlsField };

    /// <summary>The changed fields, in the order the after body (then the before body) lists them; empty unless both are objects.</summary>
    public static IReadOnlyList<FieldChange> Between(JsonElement? before, JsonElement? after)
    {
        if (before is not { ValueKind: JsonValueKind.Object } old || after is not { ValueKind: JsonValueKind.Object } current)
            return [];

        var names = new List<string>();
        foreach (var property in current.EnumerateObject().Concat(old.EnumerateObject()))
        {
            if (!Bookkeeping.Contains(property.Name) && !names.Contains(property.Name, StringComparer.Ordinal))
                names.Add(property.Name);
        }

        var changes = new List<FieldChange>();
        foreach (var name in names)
        {
            var was = Value(old, name);
            var now = Value(current, name);
            if (!JsonElement.DeepEquals(was, now))
                changes.Add(new FieldChange(name, Text(was), Text(now)));
        }

        return changes;
    }

    /// <summary>The ids of webhook channels whose URL an update changed; the URL itself is never audited.</summary>
    public static IReadOnlyList<string> ChangedWebhookUrls(JsonElement? after) =>
        after is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty(ChangedWebhookUrlsField, out var ids)
        && ids.ValueKind == JsonValueKind.Array
            ? [.. ids.EnumerateArray().Select(id => id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText())]
            : [];

    /// <summary>A short display text for a value: strings as is, <c>null</c> as <see cref="Empty"/>, the rest as JSON.</summary>
    public static string Text(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => Empty,
            JsonValueKind.String => value.GetString() is { Length: > 0 } s ? s : "\"\"",
            _ => value.GetRawText(),
        };
        return text.Length > MaxValueLength ? string.Concat(text.AsSpan(0, MaxValueLength), "…") : text;
    }

    /// <summary>The field's value; an absent field reads as <c>null</c>, so dropping a null field is no change.</summary>
    private static JsonElement Value(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) ? value : NullValue;
}
