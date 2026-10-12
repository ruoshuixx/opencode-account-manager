using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using AccountManager.Core.Models;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;

namespace AccountManager.Core.Accounts;

/// <summary>
/// Read-only snapshot of the local OpenCode state: exactly one GET /api/credential and
/// (when possible) exactly one GET /api/config. Config failures keep the Codex rows and
/// report a fixed warning instead of pretending there are zero providers.
/// </summary>
public sealed class AccountSnapshotService
{
    private readonly IOpenCodeApi _api;
    private readonly AccountMapper _mapper;
    private readonly SecretRegistry _secrets;
    private readonly SubscriptionStore? _subscriptions;
    private readonly ProviderConfigReader? _providers;

    public AccountSnapshotService(IOpenCodeApi api, AccountMapper mapper, SecretRegistry secrets,
        SubscriptionStore? subscriptions = null, ProviderConfigReader? providers = null)
    {
        _api = api;
        _mapper = mapper;
        _secrets = secrets;
        _subscriptions = subscriptions;
        _providers = providers;
    }

    public async Task<AccountSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        JsonArray credentials;
        try
        {
            credentials = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OpenCodeException)
        {
            throw;
        }
        catch
        {
            throw new OpenCodeException("无法读取 OpenCode 本地凭据列表；未生成账号快照。");
        }

        _secrets.Register(credentials);

        Dictionary<string, string> subscriptionFallback = new(StringComparer.Ordinal);
        if (_subscriptions is not null)
        {
            try
            {
                subscriptionFallback = await _subscriptions.LoadActiveUntilAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                subscriptionFallback = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        var accounts = _mapper.GetManagedAccounts(credentials, subscriptionFallback);

        var warning = string.Empty;
        var definitions = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        try
        {
            var entries = _providers is null
                ? await _api.GetConfigEntriesAsync(cancellationToken).ConfigureAwait(false)
                : await _providers.ReadAsync(cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (entry is null) { continue; }
                _secrets.Register(entry);
                _secrets.RegisterProviderSecrets(JsonMerge.Get(entry, "info"));
            }

            definitions = AccountMapper.GetProviderDefinitions(entries);
        }
        catch
        {
            warning = "供应商配置读取失败，已显示可读取的 Codex 账号。";
            definitions = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        }

        if (string.IsNullOrEmpty(warning))
        {
            foreach (var (providerId, definition) in definitions)
            {
                var package = JsonMerge.GetString(definition, "package") ?? string.Empty;
                if (!OpenAiApiProtocol.IsManagedPackage(package)) { continue; }
                var models = JsonMerge.Get(definition, "models") as JsonObject;
                if (OpenAiApiProtocol.IsLegacyResponses(package)
                    || (models?.Any(m => OpenAiApiProtocol.IsLegacyResponses(JsonMerge.GetString(m.Value, "package"))) ?? false))
                {
                    warning = "发现旧 Responses 入口，OpenCode 2.0.24 可能报找不到 @opencode/ai；打开对应 API 配置，Key 留空并保存即可修复，不会自动改账号或重启。";
                }

                var keyCredentials = credentials.Where(c =>
                {
                    if (c is null) { return false; }
                    if (JsonMerge.GetString(c, "integrationID") != providerId) { return false; }
                    var value = JsonMerge.Get(c, "value");
                    return value is not null && JsonMerge.GetString(value, "type") == "key";
                }).ToList();

                if (keyCredentials.Count > 0)
                {
                    foreach (var credential in keyCredentials) { accounts.Add(_mapper.ConvertProviderOverview(providerId, definition, credential)); }
                }
                else
                {
                    accounts.Add(_mapper.ConvertProviderOverview(providerId, definition, null));
                }
            }
        }

        return new AccountSnapshot
        {
            Accounts = accounts,
            ProviderCount = definitions.Count,
            Warning = warning,
        };
    }
}
