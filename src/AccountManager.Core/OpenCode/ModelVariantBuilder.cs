using System.Text.Json.Nodes;

namespace AccountManager.Core.OpenCode;

/// <summary>
/// Builds model catalog entries and, for the Responses protocol, synthesises <c>variants</c>
/// (reasoning tiers) only on the official Ark Coding/Agent Plan URLs, for newly introduced models supporting
/// <c>reasoning.effort</c>. Existing entries are never modified and unknown models are never
/// guessed, so a model whose request body the endpoint would reject still gets only a name.
/// </summary>
public static class ModelVariantBuilder
{
    /// <summary>A brand-new model entry: <c>{ name }</c>, plus variants when applicable.</summary>
    public static JsonObject NewModelEntry(string modelId, bool useResponses, string? baseUrl)
    {
        var entry = new JsonObject { ["name"] = modelId };
        TryAddMissingVariants(entry, modelId, useResponses, baseUrl);
        return entry;
    }

    /// <summary>
    /// Adds variants in place only when the entry has none and the model is documented for the
    /// Responses protocol. Returns whether the entry changed.
    /// </summary>
    public static bool TryAddMissingVariants(JsonObject entry, string modelId, bool useResponses, string? baseUrl)
    {
        if (!useResponses || !IsArkPlanEndpoint(baseUrl) || entry.ContainsKey("variants")) { return false; }
        if (!ModelReasoningCatalog.TryGetEffortValues(modelId, out var effortValues)) { return false; }
        entry["variants"] = BuildEffortVariants(effortValues);
        return true;
    }

    /// <summary>Only the official Ark Coding Plan and Agent Plan endpoints qualify.</summary>
    public static bool IsArkPlanEndpoint(string? baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && string.Equals(uri.Host, "ark.cn-beijing.volces.com", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(uri.AbsolutePath.TrimEnd('/'), "/api/coding/v3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.AbsolutePath.TrimEnd('/'), "/api/plan/v3", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Responses variant shape: <c>{ id, body: { reasoning: { effort } } }</c>.</summary>
    public static JsonArray BuildEffortVariants(IReadOnlyList<string> effortValues)
    {
        var variants = new JsonArray();
        foreach (var value in effortValues)
        {
            variants.Add(new JsonObject
            {
                ["id"] = value,
                ["body"] = new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = value } },
            });
        }

        return variants;
    }
}
