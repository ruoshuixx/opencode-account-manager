using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using AccountManager.Core.Security;

namespace AccountManager.Core.OpenCode;

/// <summary>
/// Replaces only the cached global document with its current disk contents. Project overrides
/// retain their ordering/precedence. This makes edits visible/exportable without reloading or
/// restarting OpenCode. This is not runtime verification: callers confirm protocol adoption
/// separately against GET /api/provider, allowing the server's filesystem watcher to settle.
/// </summary>
public sealed class ProviderConfigReader(IOpenCodeApi api, IConfigFile configFile, SecretRegistry secrets)
{
    public async Task<JsonArray> ReadAsync(CancellationToken ct = default)
    {
        var entries = (JsonArray)(await api.GetConfigEntriesAsync(ct).ConfigureAwait(false)).DeepClone();
        var global = await configFile.ReadAsync(ct).ConfigureAwait(false);
        var replaced = false;
        foreach (var entry in entries.OfType<JsonObject>())
        {
            if (!SamePath(JsonMerge.GetString(entry, "path"), global.Path)) { continue; }
            entry["info"] = global.Config.DeepClone();
            replaced = true;
        }
        if (!replaced)
        {
            entries.Insert(0, new JsonObject { ["type"] = "file", ["path"] = global.Path, ["info"] = global.Config.DeepClone() });
        }
        foreach (var entry in entries)
        {
            secrets.Register(entry);
            secrets.RegisterProviderSecrets(JsonMerge.Get(entry, "info"));
        }
        return entries;
    }

    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) { return false; }
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
