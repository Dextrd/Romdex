using System.Text.Json;
using System.Text.Json.Nodes;

namespace Romdex.Core.Plugins;

/// <summary>
/// The parts of JSON Schema plugin settings use: a flat object of strings, numbers, integers and booleans, and lists
/// of them, with defaults, choices (<c>enum</c>, or <c>oneOf</c> with a <c>const</c> each), ranges and required fields. Secrets are marked <c>writeOnly</c>, never returned, and kept when an
/// update leaves them out.
/// </summary>
public static class SettingsSchema
{
    /// <summary>The settings a new instance starts with: every property's default.</summary>
    public static JsonObject Defaults(JsonElement? schema)
    {
        var result = new JsonObject();
        foreach (var (name, property) in Properties(schema))
            if (property.TryGetProperty("default", out var value))
                result[name] = JsonNode.Parse(value.GetRawText());
        return result;
    }

    /// <summary>What's wrong with these settings, by property; empty when they're fine.</summary>
    public static Dictionary<string, string[]> Validate(JsonElement? schema, JsonObject settings)
    {
        var errors = new Dictionary<string, string[]>();
        if (schema is not { ValueKind: JsonValueKind.Object } s)
            return errors;
        if (s.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                if (settings[name] is null || settings[name] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length == 0)
                    errors[name] = ["Required."];

        foreach (var (name, property) in Properties(schema))
        {
            if (settings[name] is not { } node || errors.ContainsKey(name))
                continue;
            var problem = Check(property, node);
            if (problem is not null)
                errors[name] = [problem];
        }
        return errors;
    }

    /// <summary>Settings to show: secrets removed.</summary>
    public static JsonObject Redact(JsonElement? schema, JsonObject settings)
    {
        var copy = (JsonObject)settings.DeepClone();
        foreach (var (name, property) in Properties(schema))
            if (IsSecret(property))
                copy.Remove(name);
        return copy;
    }

    /// <summary>An update applied over the current settings: secrets it leaves out or blanks keep their value.</summary>
    public static JsonObject Merge(JsonElement? schema, JsonObject current, JsonObject update)
    {
        var merged = (JsonObject)update.DeepClone();
        foreach (var (name, property) in Properties(schema))
            if (IsSecret(property) && (merged[name] is null || merged[name] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length == 0)
                && current[name] is { } kept)
                merged[name] = kept.DeepClone();
        return merged;
    }

    private static bool IsSecret(JsonElement property) =>
        property.TryGetProperty("writeOnly", out var w) && w.ValueKind == JsonValueKind.True;

    private static string? Check(JsonElement property, JsonNode node)
    {
        var type = property.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "array")
        {
            if (node is not JsonArray list)
                return "Must be a list.";
            if (property.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True
                && list.Select(i => i?.ToJsonString()).Distinct().Count() != list.Count)
                return "Must list each only once.";
            var items = property.TryGetProperty("items", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
            return items.ValueKind == JsonValueKind.Undefined ? null
                : list.Select(item => item is null ? "Can't list nothing." : Check(items, item)).FirstOrDefault(p => p is not null);
        }
        if (node is not JsonValue value)
            return "Must be a single value.";
        switch (type)
        {
            case "string" when !value.TryGetValue<string>(out _):
                return "Must be text.";
            case "boolean" when !value.TryGetValue<bool>(out _):
                return "Must be true or false.";
            case "integer" when !(value.TryGetValue<long>(out _) || value.TryGetValue<double>(out var d) && d == Math.Floor(d)):
                return "Must be a whole number.";
            case "number" when !value.TryGetValue<double>(out _):
                return "Must be a number.";
        }
        if (property.TryGetProperty("enum", out var options) && options.ValueKind == JsonValueKind.Array
            && !options.EnumerateArray().Any(o => JsonNode.DeepEquals(JsonNode.Parse(o.GetRawText()), node)))
            return $"Must be one of {string.Join(", ", options.EnumerateArray().Select(o => o.ToString()))}.";
        if (property.TryGetProperty("oneOf", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.EnumerateArray().Where(c => c.TryGetProperty("const", out _)).Select(c => c.GetProperty("const")).ToList() is { Count: > 0 } consts
            && !consts.Any(c => JsonNode.DeepEquals(JsonNode.Parse(c.GetRawText()), node)))
            return $"Must be one of {string.Join(", ", consts.Select(c => c.ToString()))}.";
        if (value.TryGetValue<double>(out var number))
        {
            if (property.TryGetProperty("minimum", out var min) && number < min.GetDouble())
                return $"Must be at least {min}.";
            if (property.TryGetProperty("maximum", out var max) && number > max.GetDouble())
                return $"Must be at most {max}.";
        }
        return null;
    }

    private static IEnumerable<(string Name, JsonElement Property)> Properties(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => (p.Name, p.Value))
            : [];
}
