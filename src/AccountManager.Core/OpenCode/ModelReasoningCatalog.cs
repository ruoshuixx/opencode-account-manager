namespace AccountManager.Core.OpenCode;

/// <summary>
/// Reasoning tiers ("档位") that Volcengine Ark documents for its models through the Responses
/// API <c>reasoning.effort</c> field. Source:
/// https://www.volcengine.com/docs/ark/responses-api-deep-thinking (model mapping table).
/// </summary>
/// <remarks>
/// Only the <em>effective</em> tiers are listed: values Ark maps onto the same tier are collapsed,
/// and every "关闭思考" tier (<c>none</c> / <c>minimal</c>) is intentionally omitted.
/// This catalog is deliberately conservative — models the official table does not document
/// (for example <c>glm-5.3</c>, <c>kimi-k2.7-code</c>, <c>minimax-m3</c>, <c>ark-code-latest</c>)
/// are not guessed. It is used only to synthesise tiers for newly written model entries; existing
/// entries are never overwritten.
/// </remarks>
public static class ModelReasoningCatalog
{
    // Matching is case-insensitive and boundary-aware. More specific prefixes are listed first.
    private static readonly (string Match, string[] EffortValues)[] Entries =
    {
        ("deepseek-v4.1-flash",   new[] { "low", "high" }),
        ("deepseek-v4-1-flash",   new[] { "low", "high" }),
        ("deepseek-v4-pro",       new[] { "low", "high" }),
        ("deepseek-v4-flash",     new[] { "low", "high" }),
        ("glm-5.3-flash",         new[] { "low", "high", "max" }),
        ("glm-5-3-flash",         new[] { "low", "high", "max" }),
        ("glm-5.2",               new[] { "high", "max" }),
        ("glm-5-2",               new[] { "high", "max" }),
        ("doubao-seed-evolving",  new[] { "low", "medium", "high" }),
        ("doubao-seed-2.1",       new[] { "low", "medium", "high" }),
        ("doubao-seed-2-1",       new[] { "low", "medium", "high" }),
        ("doubao-seed-2.0",       new[] { "low", "medium", "high" }),
        ("doubao-seed-2-0",       new[] { "low", "medium", "high" }),
        ("doubao-seed-character", new[] { "low", "medium", "high" }),
    };

    /// <summary>Returns the documented effective effort tiers for a model id or alias.</summary>
    public static bool TryGetEffortValues(string? modelId, out IReadOnlyList<string> effortValues)
    {
        effortValues = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(modelId)) { return false; }

        var id = modelId.Trim().ToLowerInvariant();
        foreach (var (match, values) in Entries)
        {
            if (!IsMatch(id, match)) { continue; }
            effortValues = values;
            return true;
        }

        return false;
    }

    private static bool IsMatch(string id, string prefix)
    {
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) { return false; }
        if (id.Length == prefix.Length) { return true; }
        return id[prefix.Length] is '-' or '.' or '_';
    }
}
