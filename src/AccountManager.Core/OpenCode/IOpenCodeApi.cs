using System.Text.Json.Nodes;

namespace AccountManager.Core.OpenCode;

/// <summary>The credential/config API boundary; replaceable without touching real accounts in tests.</summary>
public interface IOpenCodeApi
{
    Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default);
    Task<JsonArray> GetConfigEntriesAsync(CancellationToken cancellationToken = default);
    Task<JsonNode?> GetIntegrationAsync(string id, CancellationToken cancellationToken = default);
    Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate,
        CancellationToken cancellationToken = default, string? requestedId = null);
    Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default);
}

public interface IConfigFile
{
    Task<ConfigDocument> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(ConfigDocument document, CancellationToken cancellationToken = default);
}
