using System.Text.Json.Nodes;
using AccountManager.Core.Json;

namespace AccountManager.Core.OpenCode;

/// <summary>One protocol/package mapping for editing, importing, exporting and displaying APIs.</summary>
public static class OpenAiApiProtocol
{
    public const string ChatPackage = "@opencode/ai/providers/openai-compatible";
    public const string ResponsesPackage = "@opencode/ai/providers/openai/responses";
    public const string LegacyResponsesPackage = "@opencode/ai/providers/openai-compatible/responses";

    // V2's documented compatible Responses entrypoint is missing from the 2.0.24 compiled
    // builtins map (packages/core/src/provider.ts). The documented OpenAI Responses entrypoint
    // is bundled. Keep the same /responses protocol, custom baseURL and bearer credential;
    // use HTTP explicitly because a third-party endpoint need not support OpenAI WebSockets.
    public static string PackageFor(bool useResponses) => useResponses ? ResponsesPackage : ChatPackage;

    public static bool UsesResponses(string? package) => Equal(package, ResponsesPackage) || Equal(package, LegacyResponsesPackage);

    public static bool IsManagedPackage(string? package) => Equal(package, ChatPackage) || UsesResponses(package);

    public static string? WireApiFor(string? package)
        => UsesResponses(package) ? "responses" : Equal(package, ChatPackage) ? "chat_completions" : null;

    /// <summary>Preserve the protocol while repairing the entrypoint that 2.0.24 cannot load.</summary>
    public static string CanonicalPackage(string package)
        => IsManagedPackage(package) ? PackageFor(UsesResponses(package)) : package;

    public static bool IsLegacyResponses(string? package) => Equal(package, LegacyResponsesPackage);

    /// <summary>Repair supported model overrides; an explicit choice also changes their protocol.</summary>
    public static void ApplyModelProtocol(JsonObject models, string providerPackage, bool? useResponses, bool changedToResponses)
    {
        foreach (var model in models.Select(pair => pair.Value).OfType<JsonObject>())
        {
            var oldPackage = JsonMerge.GetString(model, "package");
            var expectedPackage = IsManagedPackage(oldPackage)
                ? useResponses.HasValue ? providerPackage : CanonicalPackage(oldPackage!)
                : null;
            if (expectedPackage is not null) { model["package"] = expectedPackage; }
            // Unknown custom runtimes are not rewritten. A model/variant transport overrides
            // its provider: migrate those HTTP-compatible settings too, without enabling WS.
            var inherits = string.IsNullOrWhiteSpace(oldPackage);
            var switchedModelToResponses = expectedPackage is not null && UsesResponses(expectedPackage)
                && !string.Equals(oldPackage, expectedPackage, StringComparison.Ordinal);
            var settings = JsonMerge.Get(model, "settings");
            if (switchedModelToResponses || (inherits && changedToResponses && JsonMerge.Get(settings, "transport") is not null))
            {
                settings ??= new JsonObject();
                if (settings is not JsonObject modelSettings) { throw new OpenCodeException("模型 settings 不是对象，未保存协议更改。"); }
                modelSettings["transport"] = "http";
                if (model["settings"] is null) { model["settings"] = modelSettings; }
            }
            if (!(switchedModelToResponses || (inherits && changedToResponses))) { continue; }
            if (model["variants"] is not JsonArray variants) { continue; }
            foreach (var variant in variants.OfType<JsonObject>())
            {
                if (variant["settings"] is JsonObject variantSettings && variantSettings.ContainsKey("transport"))
                {
                    variantSettings["transport"] = "http";
                }
            }
        }
    }

    private static bool Equal(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
