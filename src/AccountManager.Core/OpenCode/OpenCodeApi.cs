using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using AccountManager.Core.Security;

namespace AccountManager.Core.OpenCode;

/// <summary>
/// Thin typed wrapper over the OpenCode V2 HTTP API exposed through the CLI.
/// All responses are registered with the secret registry before use.
/// </summary>
public sealed class OpenCodeApi : IOpenCodeApi, IProviderRuntimeReader
{
    private static readonly JsonSerializerOptions Compact = CoreJson.Compact;

    private readonly OpenCodeCli _cli;
    private readonly SecretRegistry _secrets;

    public OpenCodeApi(OpenCodeCli cli, SecretRegistry secrets)
    {
        _cli = cli;
        _secrets = secrets;
    }

    public OpenCodeCli Cli => _cli;

    public Task<JsonNode?> GetProviderAsync(string providerId, CancellationToken ct = default)
        => InvokeAsync("get", "/api/provider/" + Uri.EscapeDataString(providerId), cancellationToken: ct);

    public async Task<JsonNode?> InvokeAsync(string method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "api", method, path };
        if (body is not null)
        {
            _secrets.Register(body);
            args.Add("--data");
            args.Add(body.ToJsonString(Compact));
        }

        var text = await _cli.RunAsync(args, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) { return null; }

        JsonNode? result;
        try
        {
            result = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new OpenCodeException("OpenCode 响应不是有效 JSON；请确认使用 OpenCode V2。");
        }

        if (result is not null) { _secrets.Register(result); }
        return result;
    }

    /// <summary>GET /api/credential -&gt; data array.</summary>
    public async Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync("get", "/api/credential", null, cancellationToken).ConfigureAwait(false);
        var data = JsonMerge.Get(response, "data");
        return data as JsonArray ?? throw new OpenCodeException("OpenCode 凭据列表响应格式无效；不会把读取失败当作没有账号。");
    }

    /// <summary>GET /api/config -&gt; the raw (unwrapped) array of config documents.</summary>
    public async Task<JsonArray> GetConfigEntriesAsync(CancellationToken cancellationToken = default)
    {
        var response = await InvokeAsync("get", "/api/config", null, cancellationToken).ConfigureAwait(false);
        return response as JsonArray ?? throw new OpenCodeException("OpenCode 配置列表响应格式无效，无法安全确认供应商定义。");
    }

    public async Task<JsonNode?> GetIntegrationAsync(string id, CancellationToken cancellationToken = default)
        => await InvokeAsync("get", "/api/integration/" + Uri.EscapeDataString(id), null, cancellationToken).ConfigureAwait(false);

    public Task ActivateCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
        => InvokeAsync("post", "/api/credential/" + Uri.EscapeDataString(credentialId) + "/activate", null, cancellationToken);

    public async Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate,
        CancellationToken cancellationToken = default, string? requestedId = null)
    {
        var body = new JsonObject
        {
            ["integrationID"] = integrationId,
            ["label"] = label,
            ["value"] = value.DeepClone(),
            ["activate"] = activate,
        };
        if (!string.IsNullOrWhiteSpace(requestedId)) { body["id"] = requestedId; }
        var response = await InvokeAsync("post", "/api/credential", body, cancellationToken).ConfigureAwait(false);
        var id = JsonMerge.GetString(JsonMerge.Get(response, "data"), "id");
        return id ?? string.Empty;
    }

    public Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
        => InvokeAsync("delete", "/api/credential/" + Uri.EscapeDataString(credentialId), null, cancellationToken);
}
