using System.Text.Json.Nodes;

namespace AccountManager.Core.Json;

/// <summary>Deep copy / merge helpers for arbitrary JSON configuration.</summary>
public static class JsonMerge
{
    public static JsonNode? DeepCopy(JsonNode? node) => node?.DeepClone();

    public static bool IsObject(JsonNode? node) => node is JsonObject;

    /// <summary>
    /// Merge <paramref name="overlay"/> on top of <paramref name="base"/>: objects merge
    /// field-by-field (overlay wins), scalars/arrays replace. Returns a new node; inputs
    /// are never mutated.
    /// </summary>
    public static JsonNode? Merge(JsonNode? baseNode, JsonNode? overlay)
    {
        if (overlay is not JsonObject overlayObj) { return overlay?.DeepClone(); }

        var result = new JsonObject();
        if (baseNode is JsonObject baseObj)
        {
            foreach (var kv in baseObj) { result[kv.Key] = kv.Value?.DeepClone(); }
        }

        foreach (var kv in overlayObj)
        {
            if (result.TryGetPropertyValue(kv.Key, out var existing)
                && existing is JsonObject
                && kv.Value is JsonObject)
            {
                result[kv.Key] = Merge(existing, kv.Value);
            }
            else
            {
                result[kv.Key] = kv.Value?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>Case-sensitive get by name, returning null when absent.</summary>
    public static JsonNode? Get(JsonNode? node, string name)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(name, out var value)) { return value; }
        return null;
    }

    public static string? GetString(JsonNode? node, string name)
    {
        var value = Get(node, name);
        return value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
