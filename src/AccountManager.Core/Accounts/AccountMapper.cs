using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AccountManager.Core.Json;
using AccountManager.Core.Models;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Quota;
using AccountManager.Core.Security;

namespace AccountManager.Core.Accounts;

/// <summary>
/// Maps raw OpenCode credentials/config into display account rows, exactly matching the
/// PowerShell <c>Convert-CredentialAccount</c> / <c>Convert-ProviderOverviewRow</c> contract.
/// Never exposes secret values on a row's display fields.
/// </summary>
public sealed class AccountMapper
{
    private readonly SecretRegistry _secrets;

    public AccountMapper(SecretRegistry secrets) => _secrets = secrets;

    public string Mask(string text) => _secrets.Redact(text);

    public AccountRow ConvertCredential(JsonNode? credential, IReadOnlyDictionary<string, string>? subscriptionFallback = null)
    {
        if (credential is null) { throw new InvalidOperationException("凭据为空，无法映射账号。"); }
        var id = JsonMerge.GetString(credential, "id");
        if (string.IsNullOrWhiteSpace(id)) { throw new InvalidOperationException("凭据缺少 ID，无法安全映射。"); }
        if (JsonMerge.GetString(credential, "integrationID") != "openai") { throw new InvalidOperationException("不是 OpenAI 凭据。"); }

        var value = JsonMerge.Get(credential, "value");
        if (value is null) { throw new InvalidOperationException("凭据缺少 value，无法映射。"); }
        if (JsonMerge.GetString(value, "type") != "oauth") { throw new InvalidOperationException("不是 OAuth 凭据。"); }
        if (!CredentialHelpers.IsCodexOAuthMethod(JsonMerge.GetString(value, "methodID"))) { throw new InvalidOperationException("不是已识别的 ChatGPT OAuth 登录方式。"); }

        var info = CredentialHelpers.GetIdentityInfo(value);
        var labelRaw = JsonMerge.GetString(credential, "label") ?? string.Empty;

        var email = info.Email;
        if (string.IsNullOrEmpty(email) && CredentialHelpers.IsEmail(labelRaw)) { email = labelRaw; }
        var display = email;
        if (string.IsNullOrEmpty(display)) { display = labelRaw; }
        if (string.IsNullOrEmpty(display)) { display = "Codex " + info.AccountID; }

        var access = JsonMerge.GetString(value, "access");
        var claims = Jwt.ReadPayload(access);

        var plan = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/auth"), "chatgpt_plan_type") ?? string.Empty;
        if (string.IsNullOrEmpty(plan))
        {
            var metadata = JsonMerge.Get(value, "metadata");
            plan = JsonMerge.GetString(metadata, "planType") ?? JsonMerge.GetString(metadata, "plan") ?? string.Empty;
        }

        var expiry = CredentialHelpers.GetTokenExpiryMs(value);
        var expiresText = expiry.ExpiresMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(expiry.ExpiresMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : string.Empty;

        var workspace = info.AccountID;
        if (workspace.Length > 8) { workspace = workspace[^8..]; }

        var statusCode = "Ready";
        var status = "可激活（未联网验证）";
        if (string.IsNullOrWhiteSpace(access))
        {
            statusCode = "Missing";
            status = "缺少访问令牌";
        }
        else if (expiry.ExpiresMs > 0 && expiry.ExpiresMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            statusCode = "Expired";
            status = "访问令牌已过期（刷新是否有效未验证）";
        }

        return new AccountRow
        {
            Key = id,
            CredentialId = id,
            Active = JsonNum.ToBool(JsonMerge.Get(credential, "active")),
            Kind = AccountKind.Codex,
            Label = display,
            Provider = "openai",
            Identity = info.Identity,
            Workspace = workspace,
            Plan = plan,
            Expires = expiresText,
            Status = status,
            StatusCode = statusCode,
            AccountId = info.AccountID,
            Email = info.Email,
            SubscriptionExpires = ResolveSubscription(GetSubscriptionExpiry(value), info.Identity, subscriptionFallback),
            Value = value.DeepClone() as JsonObject,
        };
    }

    private static readonly string[] SubscriptionFields =
    {
        "subscriptionActiveUntil", "subscription_active_until", "subscriptionActiveuntil",
        "subscriptionUntil", "subscription_until",
    };

    /// <summary>
    /// Reads a ChatGPT subscription expiry from the credential metadata when the importer stored one.
    /// Accepts epoch seconds/milliseconds or ISO-8601 text; returns null when absent.
    /// </summary>
    public static DateTimeOffset? GetSubscriptionExpiry(JsonNode? value)
    {
        var metadata = JsonMerge.Get(value, "metadata");
        foreach (var field in SubscriptionFields)
        {
            var node = JsonMerge.Get(metadata, field) ?? JsonMerge.Get(value, field);
            if (node is null) { continue; }
            var parsed = QuotaService.ConvertResetTime(node);
            if (parsed is not null) { return parsed; }
        }

        return null;
    }

    /// <summary>
    /// Combines the credential-metadata expiry with the local cache. A live lookup that authoritatively
    /// found no expiry wins outright, so a stale metadata date is never shown; otherwise the later of
    /// the two is used, since a renewal only extends the expiry.
    /// </summary>
    private static DateTimeOffset? ResolveSubscription(DateTimeOffset? metadata, string identity, IReadOnlyDictionary<string, string>? fallback)
    {
        var (cached, cleared) = FallbackSubscription(identity, fallback);
        return cleared ? null : Later(metadata, cached);
    }

    /// <summary>
    /// Cached expiry for <paramref name="identity"/>, or <c>Cleared</c> when a live lookup
    /// authoritatively found none (the cache stores an empty-string sentinel for that case).
    /// </summary>
    private static (DateTimeOffset? Value, bool Cleared) FallbackSubscription(string identity, IReadOnlyDictionary<string, string>? fallback)
    {
        if (string.IsNullOrWhiteSpace(identity) || fallback is null) { return (null, false); }
        if (!fallback.TryGetValue(identity, out var raw)) { return (null, false); }
        if (raw.Length == 0) { return (null, true); }
        return (QuotaService.ConvertResetTime(JsonValue.Create(raw)), false);
    }

    /// <summary>
    /// A subscription expiry only moves forward (a renewal extends it), so when metadata and the
    /// refreshed local mirror disagree, the later value is the current one. This lets a live
    /// refresh show immediately even if the stored credential metadata is older.
    /// </summary>
    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null) { return b; }
        if (b is null) { return a; }
        return a >= b ? a : b;
    }

    public AccountRow ConvertProviderOverview(string providerId, JsonNode definition, JsonNode? credential)
    {
        var name = JsonMerge.GetString(definition, "name");
        if (string.IsNullOrWhiteSpace(name)) { name = providerId; }
        name = Mask(name);
        if (string.IsNullOrWhiteSpace(name)) { name = Mask(providerId); }

        var package = Mask(JsonMerge.GetString(definition, "package") ?? string.Empty);
        var baseRaw = JsonMerge.GetString(JsonMerge.Get(definition, "settings"), "baseURL") ?? string.Empty;
        var baseUrl = SafeBaseUrl(baseRaw);

        var models = new List<string>();
        var modelIds = new List<string>();
        if (JsonMerge.Get(definition, "models") is JsonObject modelObject)
        {
            foreach (var kv in modelObject)
            {
                var modelId = Mask(kv.Key);
                modelIds.Add(modelId);
                var declared = JsonMerge.GetString(kv.Value, "name");
                var modelName = string.IsNullOrWhiteSpace(declared) ? kv.Key : declared;
                modelName = Mask(modelName);
                if (!string.IsNullOrWhiteSpace(modelName)) { models.Add(modelName); }
            }
        }

        var projection = new ProviderProjection
        {
            Name = name,
            Package = package,
            BaseUrl = baseUrl,
            Models = models,
            ModelIds = modelIds,
        };

        if (credential is null)
        {
            return new AccountRow
            {
                Key = "provider-" + providerId,
                CredentialId = string.Empty,
                Active = false,
                Kind = AccountKind.Api,
                Label = name,
                Provider = providerId,
                Identity = providerId,
                ProviderName = name,
                BaseUrl = baseUrl,
                Models = models,
                ModelIds = modelIds,
                Workspace = "不适用",
                Plan = "API Key",
                Expires = "不适用",
                Status = "配置账号（未存本地凭据）",
                StatusCode = "ConfiguredOnly",
                Definition = projection,
            };
        }

        var value = JsonMerge.Get(credential, "value");
        var credentialId = JsonMerge.GetString(credential, "id") ?? string.Empty;
        var identity = JsonMerge.GetString(JsonMerge.Get(value, "metadata"), "cockpitID");
        if (string.IsNullOrWhiteSpace(identity)) { identity = providerId; }

        return new AccountRow
        {
            Key = credentialId,
            CredentialId = credentialId,
            Active = JsonNum.ToBool(JsonMerge.Get(credential, "active")),
            Kind = AccountKind.Api,
            Label = name,
            Provider = providerId,
            Identity = identity,
            ProviderName = name,
            BaseUrl = baseUrl,
            Models = models,
            ModelIds = modelIds,
            Workspace = "不适用",
            Plan = "API Key",
            Expires = "不适用",
            Status = "已保存凭据（未联网验证）",
            StatusCode = "Stored",
            Value = value?.DeepClone() as JsonObject,
            Definition = projection,
        };
    }

    /// <summary>Safe HTTPS scheme://host[:port]/path, stripping credentials/query/fragment.</summary>
    public string SafeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { return "地址未配置"; }
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) { return "地址无法安全显示"; }
        if (uri.Scheme != "https") { return "地址无法安全显示"; }
        var safe = uri.Scheme + "://" + uri.Host;
        if (!uri.IsDefaultPort) { safe += ":" + uri.Port; }
        if (!string.IsNullOrWhiteSpace(uri.AbsolutePath) && uri.AbsolutePath != "/") { safe += uri.AbsolutePath; }
        return Mask(safe);
    }

    /// <summary>Merge provider definitions across config documents (low→high priority).</summary>
    public static Dictionary<string, JsonNode> GetProviderDefinitions(JsonArray entries)
    {
        var definitions = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null) { continue; }
            if (JsonMerge.GetString(entry, "type") == "directory") { continue; }
            var providers = JsonMerge.Get(JsonMerge.Get(entry, "info"), "providers");
            if (providers is not JsonObject providerObj) { continue; }
            foreach (var kv in providerObj)
            {
                if (kv.Value is not JsonObject overlay) { continue; }
                definitions[kv.Key] = definitions.TryGetValue(kv.Key, out var existing)
                    ? JsonMerge.Merge(existing, overlay)!
                    : overlay.DeepClone();
            }
        }

        return definitions;
    }

    /// <summary>GET /api/credential -> Codex accounts (skips unmappable ones).</summary>
    public List<AccountRow> GetManagedAccounts(JsonArray credentials, IReadOnlyDictionary<string, string>? subscriptionFallback = null)
    {
        _secrets.Register(credentials);
        var accounts = new List<AccountRow>();
        foreach (var credential in credentials)
        {
            if (credential is null) { continue; }
            if (JsonMerge.GetString(credential, "integrationID") != "openai") { continue; }
            var value = JsonMerge.Get(credential, "value");
            if (value is null) { continue; }
            if (JsonMerge.GetString(value, "type") != "oauth") { continue; }
            if (!CredentialHelpers.IsCodexOAuthMethod(JsonMerge.GetString(value, "methodID"))) { continue; }
            try
            {
                accounts.Add(ConvertCredential(credential, subscriptionFallback));
            }
            catch (Exception ex)
            {
                // Never surface an unmappable credential as an account.
                _ = ex;
            }
        }

        return accounts;
    }
}
