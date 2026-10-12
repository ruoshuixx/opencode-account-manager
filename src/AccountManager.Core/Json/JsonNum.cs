using System.Globalization;
using System.Text.Json.Nodes;

namespace AccountManager.Core.Json;

/// <summary>
/// Strict numeric coercion: only finite real numbers; bools, empty/non-numeric strings,
/// NaN and Infinity all return null so callers never mistake "empty" for 0.
/// </summary>
public static class JsonNum
{
    public static double? ToNumber(JsonNode? node)
    {
        if (node is null) { return null; }
        if (node is not JsonValue value) { return null; }
        if (value.TryGetValue<bool>(out _)) { return null; }
        if (value.TryGetValue<string>(out var s))
        {
            var text = s.Trim();
            if (text.Length == 0) { return null; }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) { return null; }
            return double.IsNaN(parsed) || double.IsInfinity(parsed) ? null : parsed;
        }

        if (value.TryGetValue<double>(out var d)) { return double.IsNaN(d) || double.IsInfinity(d) ? null : d; }
        if (value.TryGetValue<decimal>(out var dec)) { return (double)dec; }
        if (value.TryGetValue<long>(out var l)) { return l; }
        if (value.TryGetValue<int>(out var i)) { return i; }
        return null;
    }

    public static long? ToLong(JsonNode? node)
    {
        var n = ToNumber(node);
        if (n is null) { return null; }
        return (long)Math.Round(n.Value);
    }

    /// <summary>Only explicit true / "true" / "1" / "yes" count as true.</summary>
    public static bool ToBool(JsonNode? node)
    {
        if (node is not JsonValue value) { return false; }
        if (value.TryGetValue<bool>(out var b)) { return b; }
        if (value.TryGetValue<string>(out var s))
        {
            var text = s.Trim().ToLowerInvariant();
            return text is "true" or "1" or "yes";
        }

        return false;
    }
}
