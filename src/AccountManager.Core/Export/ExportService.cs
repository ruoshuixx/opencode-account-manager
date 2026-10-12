using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.Models;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;

namespace AccountManager.Core.Export;

/// <summary>
/// Port of <c>Export.ps1</c>: exports caller-selected saved credentials as a Cockpit-compatible
/// account array. Read-only against OpenCode, atomic no-overwrite file write, never returns or
/// logs secret material.
/// </summary>
public sealed class ExportService
{
    private static readonly JsonSerializerOptions ExportJsonOptions = CoreJson.Indented;

    private readonly IOpenCodeApi _api;
    private readonly SecretRegistry _secrets;
    private readonly AppLog _log;
    private readonly SubscriptionStore? _subscriptions;
    private readonly ProviderConfigReader? _providers;

    public ExportService(IOpenCodeApi api, SecretRegistry secrets, AppLog log,
        SubscriptionStore? subscriptions = null, ProviderConfigReader? providers = null)
    {
        _api = api;
        _secrets = secrets;
        _log = log;
        _subscriptions = subscriptions;
        _providers = providers;
    }

    /// <summary>Fixed export filename: <c>opencode_yyyy-MM-dd_HH-mm-ss.json</c>.</summary>
    public static string GetExportFileName(DateTime date)
        => "opencode_" + date.ToString("yyyy-MM-dd_HH-mm-ss") + ".json";

    /// <summary>
    /// Pure structural check against Cockpit's native CodexAccount serde shape. The optional
    /// <paramref name="mirrored"/> parameter remains for source compatibility; current Cockpit
    /// fields are valid in both portable and mirrored entries. Never throws.
    /// </summary>
    public static bool TestCockpitExportShape(JsonNode? node, bool mirrored = false)
        => CockpitAccountSchema.IsValid(node);

    /// <summary>
    /// Exports the selected saved credentials to a Cockpit-compatible JSON array file.
    /// Returns only path/count/warning; the document body is never returned.
    /// </summary>
    public async Task<ExportResult> ExportAsync(IReadOnlyList<string> credentialIds, string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) { throw new OpenCodeException("请先选择导出文件位置。"); }

        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            fullPath = Path.Combine(fullPath, GetExportFileName(DateTime.Now));
        }

        if (Path.GetExtension(fullPath).ToLowerInvariant() != ".json")
        {
            throw new OpenCodeException("导出文件必须使用 .json 扩展名。");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new OpenCodeException("导出文件所在文件夹不存在；请选择已存在的文件夹，本工具不会创建目录。");
        }

        if (File.Exists(fullPath))
        {
            throw new OpenCodeException("目标文件已存在；为避免覆盖用户文件，请选择新的文件名或明确允许覆盖。");
        }

        // Deduplicate requested ids (Ordinal, first occurrence wins).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var candidate in credentialIds ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(candidate)) { continue; }
            if (seen.Add(candidate)) { ids.Add(candidate); }
        }

        if (ids.Count == 0) { throw new OpenCodeException("请选择要导出的已保存账号。"); }

        // Exactly one GET /api/credential and one GET /api/config.
        var credentials = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        _secrets.Register(credentials);

        var configEntries = _providers is null
            ? await _api.GetConfigEntriesAsync(cancellationToken).ConfigureAwait(false)
            : await _providers.ReadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in configEntries)
        {
            if (entry is null) { continue; }
            _secrets.Register(entry);
            _secrets.RegisterProviderSecrets(JsonMerge.Get(entry, "info"));
        }

        var definitions = AccountMapper.GetProviderDefinitions(configEntries);

        // Local mirror cache: when an account was imported from Cockpit, the sidecar keeps its
        // full (secret-free) Cockpit entry. It becomes the base of the exported row so client
        // state, quota history and other Cockpit-only fields survive a round trip.
        var cachedRecords = _subscriptions is null
            ? new List<SubscriptionRecord>()
            : (await _subscriptions.LoadAsync(cancellationToken).ConfigureAwait(false))
                .Values.Where(r => r.Cockpit is not null).ToList();

        var rows = new JsonArray();
        var portableCount = 0;
        var unknownTimes = false;
        var hasNativeApiProvider = false;

        foreach (var id in ids)
        {
            var matches = credentials
                .Where(c => c is not null && string.Equals(JsonMerge.GetString(c, "id"), id, StringComparison.Ordinal))
                .ToList();
            if (matches.Count != 1)
            {
                throw new OpenCodeException("所选账号已不存在或不再唯一，已取消导出；请刷新账号列表后重试。");
            }

            var credential = matches[0];
            JsonObject row;
            try
            {
                row = ConvertCredentialToCockpitExport(credential, definitions);
            }
            catch (Exception ex)
            {
                throw new OpenCodeException("无法导出所选账号：" + _secrets.Redact(ex.Message));
            }

            // Full Cockpit mirror: if a cached Cockpit entry matches, use it as the base and
            // overlay the live identity/tokens/keys. Otherwise keep the portable row.
            var mirrored = false;
            var cached = FindCachedRecord(credential, row, cachedRecords);
            if (cached?.Cockpit is not null)
            {
                JsonObject cachedMirror;
                try
                {
                    cachedMirror = SubscriptionStore.GetExportMirror(cached);
                }
                catch (OpenCodeException ex)
                {
                    throw new OpenCodeException(_secrets.Redact(ex.Message));
                }
                catch
                {
                    throw new OpenCodeException("本地 Cockpit 镜像的密封字段无法安全恢复；为避免丢失源数据，已取消导出。请重新导入有效备份后重试。");
                }

                var merged = MergeCockpitMirror(row, cachedMirror, JsonMerge.GetString(row, "auth_mode") == "oauth");
                if (!TestCockpitExportShape(merged, mirrored: true))
                {
                    throw new OpenCodeException("本地 Cockpit 镜像不符合可导出结构；为避免静默丢失历史数据，已取消导出。请重新导入有效备份后重试。");
                }

                row = merged;
                mirrored = true;
            }

            if (!mirrored) { portableCount++; }
            if (!TestCockpitExportShape(row, mirrored))
            {
                throw new OpenCodeException("导出对象未通过结构校验，已取消导出（不会写入无效文件）。");
            }

            var authMode = JsonMerge.GetString(row, "auth_mode");
            if (IsUnknownTimestamp(row["created_at"]) || IsUnknownTimestamp(row["last_used"])) { unknownTimes = true; }
            if (authMode == "apikey")
            {
                var cockpitId = JsonMerge.GetString(JsonMerge.Get(JsonMerge.Get(credential, "value"), "metadata"), "cockpitID");
                if (string.IsNullOrWhiteSpace(cockpitId)) { hasNativeApiProvider = true; }
            }

            rows.Add(row);
        }

        var warningParts = new List<string>();
        if (portableCount > 0) { warningParts.Add(portableCount + " 条没有本地缓存，按可移植格式导出（仅身份与认证字段，未伪造其他数据）。"); }
        if (unknownTimes) { warningParts.Add("缺少可信来源的创建/使用时间以 0 作为未知占位；这不表示原始时间。"); }
        if (hasNativeApiProvider) { warningParts.Add("原生 API 供应商标识在重新导入时会映射为新的 cockpit- 供应商，不保证与原标识相同。"); }

        var json = rows.ToJsonString(ExportJsonOptions);
        await WriteExportAtomicAsync(fullPath, json, overwrite: false, expectedHash: null, cancellationToken).ConfigureAwait(false);

        _log.Info("导出完成：" + fullPath + "；项数=" + rows.Count);
        return new ExportResult { Path = fullPath, Count = rows.Count, Warning = string.Join(' ', warningParts) };
    }

    /// <summary>
    /// Finds the cached full Cockpit entry matching one OpenCode credential, or null. OAuth
    /// matches on account id + email; API matches on the stored provider / cockpit id.
    /// </summary>
    internal static SubscriptionRecord? FindCachedRecord(JsonNode? credential, JsonObject portable, List<SubscriptionRecord> cachedRecords)
    {
        if (credential is null) { return null; }
        if (cachedRecords.Count == 0) { return null; }
        var value = JsonMerge.Get(credential, "value");
        if (value is null) { return null; }

        var type = JsonMerge.GetString(value, "type");
        if (string.Equals(type, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            var info = CredentialHelpers.GetIdentityInfo(value);
            if (string.IsNullOrWhiteSpace(info.AccountID) || string.IsNullOrWhiteSpace(info.Email)) { return null; }
            foreach (var record in cachedRecords)
            {
                if (string.Equals(JsonMerge.GetString(record.Cockpit, "auth_mode"), "oauth", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(record.AccountId, info.AccountID, StringComparison.Ordinal)
                    && string.Equals(record.Email, info.Email, StringComparison.OrdinalIgnoreCase))
                {
                    return record;
                }
            }

            return null;
        }

        // API: exact provider identity survives explicit key rotation. For stale ids, a URL
        // alone cannot identify an account: restoring passwords/private keys from another
        // account on the same gateway would be unsafe. Require a unique URL + sealed-key match.
        var cockpitId = JsonMerge.GetString(JsonMerge.Get(value, "metadata"), "cockpitID");
        if (!string.IsNullOrWhiteSpace(cockpitId))
        {
            foreach (var record in cachedRecords)
            {
                if (!IsCachedApi(record)) { continue; }
                if (string.Equals(record.AccountId, cockpitId, StringComparison.Ordinal)
                    || string.Equals(JsonMerge.GetString(record.Cockpit, "api_provider_id"), cockpitId, StringComparison.Ordinal))
                {
                    return record;
                }
            }
        }

        var baseUrl = NormalizeUrl(JsonMerge.GetString(portable, "api_base_url"));
        var liveKey = JsonMerge.GetString(value, "key");
        if (baseUrl.Length > 0 && !string.IsNullOrWhiteSpace(liveKey))
        {
            SubscriptionRecord? match = null;
            foreach (var record in cachedRecords)
            {
                if (!IsCachedApi(record)) { continue; }
                if (!string.Equals(NormalizeUrl(JsonMerge.GetString(record.Cockpit, "api_base_url")), baseUrl, StringComparison.Ordinal)) { continue; }
                var sealedKey = JsonMerge.GetString(SubscriptionStore.OpenSecrets(record.SealedSecrets), "openai_api_key");
                if (string.Equals(sealedKey, liveKey, StringComparison.Ordinal))
                {
                    if (match is not null) { return null; }
                    match = record;
                }
            }
            return match;
        }

        return null;
    }

    private static bool IsCachedApi(SubscriptionRecord record)
        => string.Equals(JsonMerge.GetString(record.Cockpit, "auth_mode"), "apikey", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>
    /// Merges a portable export row onto a cached full Cockpit entry: the cached entry keeps its
    /// client-state/history fields, while the current identity, tokens/keys and live subscription
    /// data are overlaid. The returned object contains current secrets in memory and must never be
    /// logged or exposed except through the guarded export-file path.
    /// </summary>
    internal static JsonObject MergeCockpitMirror(JsonObject portable, JsonObject cachedEntry, bool oauth)
    {
        var merged = (JsonObject)cachedEntry.DeepClone();
        // Old cache versions could retain environment-style API aliases or plaintext token
        // properties. Never let those stale values reach an export; current credential values
        // are overlaid below in the one canonical Cockpit spelling.
        CockpitSecretFields.RemoveLiveCredentials(merged);

        // Keep the original Cockpit id/email when the cache has them; otherwise adopt the credential's.
        if (string.IsNullOrWhiteSpace(JsonMerge.GetString(merged, "id"))) { CopyNode(portable, merged, "id"); }
        if (string.IsNullOrWhiteSpace(JsonMerge.GetString(merged, "email"))) { CopyNode(portable, merged, "email"); }
        CopyNode(portable, merged, "auth_mode");
        if (!string.IsNullOrWhiteSpace(JsonMerge.GetString(portable, "account_id"))) { CopyNode(portable, merged, "account_id"); }
        foreach (var timestamp in new[] { "created_at", "last_used" })
        {
            if (portable[timestamp] is not null
                && (!CockpitAccountSchema.IsInt64Value(merged[timestamp])
                    || (IsUnknownTimestamp(merged[timestamp]) && !IsUnknownTimestamp(portable[timestamp]))))
            {
                CopyNode(portable, merged, timestamp);
            }
        }

        if (oauth)
        {
            CopyNode(portable, merged, "tokens");
            if (portable["plan_type"] is not null) { CopyNode(portable, merged, "plan_type"); }
            if (portable["subscription_active_until"] is not null) { CopyNode(portable, merged, "subscription_active_until"); }
        }
        else
        {
            // Shape parity with the portable row: the export schema always carries a tokens object.
            CopyNode(portable, merged, "tokens");
            foreach (var field in new[] { "openai_api_key", "api_base_url", "api_provider_mode", "api_provider_id", "api_provider_name", "api_model_catalog", "api_wire_api" })
            {
                if (portable[field] is not null) { CopyNode(portable, merged, field); }
            }

            // Chat Completions cannot carry the Responses websocket capability forward from a
            // cached entry. Other source capability metadata remains untouched and is never
            // guessed for portable rows.
            if (string.Equals(JsonMerge.GetString(portable, "api_wire_api"), "chat_completions", StringComparison.Ordinal))
            {
                merged["api_supports_websockets"] = false;
            }
        }

        return merged;
    }

    private static void CopyNode(JsonObject from, JsonObject to, string name)
    {
        if (from[name] is { } node) { to[name] = node.DeepClone(); }
        else { to.Remove(name); }
    }

    /// <summary>
    /// Pure mapping of one full credential to a Cockpit-compatible export object (contains
    /// plaintext tokens/keys internally). Unsupported or unsafe inputs throw; all known secrets
    /// are registered before any throw so errors never leak them.
    /// </summary>
    public JsonObject ConvertCredentialToCockpitExport(JsonNode? credential, IReadOnlyDictionary<string, JsonNode>? providerDefinitions)
    {
        if (credential is null) { throw new OpenCodeException("凭据为空，无法导出。"); }
        _secrets.Register(credential);

        var credentialId = JsonMerge.GetString(credential, "id");
        if (string.IsNullOrWhiteSpace(credentialId)) { throw new OpenCodeException("凭据缺少 ID，无法导出。"); }

        var value = JsonMerge.Get(credential, "value");
        if (value is null) { throw new OpenCodeException("凭据缺少 value，无法导出。"); }

        var type = JsonMerge.GetString(value, "type");
        var metadata = JsonMerge.Get(value, "metadata");

        if (string.Equals(type, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            return ConvertOAuth(credential, value, metadata, credentialId);
        }

        if (string.Equals(type, "key", StringComparison.OrdinalIgnoreCase))
        {
            return ConvertApiKey(credential, value, metadata, credentialId, providerDefinitions);
        }

        throw new OpenCodeException("不支持的凭据类型，无法导出。");
    }

    private JsonObject ConvertOAuth(JsonNode credential, JsonNode value, JsonNode? metadata, string credentialId)
    {
        if (!string.Equals(JsonMerge.GetString(credential, "integrationID"), "openai", StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenCodeException("不是 OpenAI 凭据。");
        }

        if (!CredentialHelpers.IsCodexOAuthMethod(JsonMerge.GetString(value, "methodID")))
        {
            throw new OpenCodeException("不是已识别的 ChatGPT OAuth 登录方式。");
        }

        var access = JsonMerge.GetString(value, "access");
        var refresh = JsonMerge.GetString(value, "refresh");
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
        {
            throw new OpenCodeException("OAuth 账号缺少访问令牌或刷新令牌，无法还原为可用登录。");
        }

        var info = CredentialHelpers.GetIdentityInfo(value);
        if (string.IsNullOrWhiteSpace(info.AccountID) || string.IsNullOrWhiteSpace(info.Email))
        {
            throw new OpenCodeException("无法从本地凭据确定账号 ID 或邮箱，拒绝导出虚构身份。");
        }

        // Only export id_token when it was actually stored; never reverse-engineer it.
        var idToken = JsonMerge.GetString(value, "id_token");
        if (string.IsNullOrWhiteSpace(idToken)) { idToken = JsonMerge.GetString(metadata, "id_token"); }
        if (string.IsNullOrWhiteSpace(idToken)) { idToken = string.Empty; }

        var claims = Jwt.ReadPayload(access);
        var plan = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/auth"), "chatgpt_plan_type") ?? string.Empty;
        if (string.IsNullOrEmpty(plan))
        {
            plan = JsonMerge.GetString(metadata, "planType") ?? JsonMerge.GetString(metadata, "plan") ?? string.Empty;
        }

        var subscription = JsonMerge.Get(metadata, "subscriptionActiveUntil")
            ?? JsonMerge.Get(metadata, "subscription_active_until");

        var obj = new JsonObject
        {
            ["id"] = credentialId,
            ["email"] = info.Email,
            ["auth_mode"] = "oauth",
            ["user_id"] = null,
            ["plan_type"] = string.IsNullOrWhiteSpace(plan) ? null : plan,
            ["subscription_active_until"] = subscription?.DeepClone(),
            ["account_id"] = info.AccountID,
            ["organization_id"] = null,
            ["tokens"] = new JsonObject
            {
                ["id_token"] = idToken,
                ["access_token"] = access,
                ["refresh_token"] = refresh,
            },
            ["token_updated_at"] = null,
            ["client_auth_status"] = null,
            ["quota"] = null,
            ["team_quota_history"] = null,
            ["usage_updated_at"] = null,
            ["tags"] = null,
        };

        AddTimestamp(obj, "created_at", GetExportMetadataInt64(metadata, "created_at"));
        AddTimestamp(obj, "last_used", GetExportMetadataInt64(metadata, "last_used"));
        return obj;
    }

    private JsonObject ConvertApiKey(
        JsonNode credential,
        JsonNode value,
        JsonNode? metadata,
        string credentialId,
        IReadOnlyDictionary<string, JsonNode>? providerDefinitions)
    {
        var providerId = JsonMerge.GetString(credential, "integrationID");
        if (string.IsNullOrWhiteSpace(providerId)) { throw new OpenCodeException("API 凭据缺少供应商 ID，无法导出。"); }

        var apiKey = JsonMerge.GetString(value, "key");
        if (string.IsNullOrWhiteSpace(apiKey)) { throw new OpenCodeException("API 凭据缺少本地密钥，拒绝导出。"); }

        JsonNode? definition = null;
        if (providerDefinitions is not null && providerDefinitions.TryGetValue(providerId, out var found)) { definition = found; }
        if (definition is null) { throw new OpenCodeException("找不到该 API 账号对应的供应商配置，无法安全导出。"); }

        var package = JsonMerge.GetString(definition, "package");
        var wire = OpenAiApiProtocol.WireApiFor(package)
            ?? throw new OpenCodeException("不支持的 API 供应商包类型，拒绝导出。");

        var baseRaw = JsonMerge.GetString(JsonMerge.Get(definition, "settings"), "baseURL") ?? string.Empty;
        if (!Uri.TryCreate(baseRaw, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new OpenCodeException("API 地址必须是无内嵌凭据、查询参数和片段的 HTTPS 地址；拒绝导出无效配置。");
        }

        var catalog = new List<string>();
        if (JsonMerge.Get(definition, "models") is JsonObject modelsObject)
        {
            foreach (var kv in modelsObject)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key)) { catalog.Add(kv.Key); }
            }
        }

        if (catalog.Count == 0) { throw new OpenCodeException("API 账号没有可回读的模型列表，拒绝导出。"); }

        var cockpitId = JsonMerge.GetString(metadata, "cockpitID");
        var sourceId = string.IsNullOrWhiteSpace(cockpitId) ? providerId : cockpitId;
        var name = JsonMerge.GetString(definition, "name");
        if (string.IsNullOrWhiteSpace(name)) { name = sourceId; }

        // (JsonNode?) cast keeps these as primitive nodes; JsonArray.Add(string) would wrap them
        // in JsonValueCustomized<string>, whose writer requires a TypeInfoResolver on the options.
        var catalogArray = new JsonArray();
        foreach (var model in catalog) { catalogArray.Add((JsonNode?)model); }

        var obj = new JsonObject
        {
            ["id"] = credentialId,
            // API key accounts have no OAuth email; the public type is a non-Option string, so "".
            ["email"] = string.Empty,
            ["auth_mode"] = "apikey",
            ["openai_api_key"] = apiKey,
            ["api_base_url"] = uri.AbsoluteUri,
            ["api_provider_mode"] = "custom",
            ["api_provider_id"] = sourceId,
            ["api_provider_name"] = name,
            ["api_model_catalog"] = catalogArray,
            ["api_wire_api"] = wire,
            ["user_id"] = null,
            ["plan_type"] = null,
            ["account_id"] = null,
            ["organization_id"] = null,
            ["account_name"] = null,
            // API key accounts have no OAuth tokens: non-Option strings empty, refresh_token omitted.
            ["tokens"] = new JsonObject
            {
                ["id_token"] = string.Empty,
                ["access_token"] = string.Empty,
            },
            ["token_updated_at"] = null,
            ["quota"] = null,
            ["tags"] = null,
        };

        AddTimestamp(obj, "created_at", GetExportMetadataInt64(metadata, "created_at"));
        AddTimestamp(obj, "last_used", GetExportMetadataInt64(metadata, "last_used"));
        return obj;
    }

    private static void AddTimestamp(JsonObject obj, string name, long? value)
    {
        // CodexAccount requires these fields. Zero is an explicit unknown placeholder, not a
        // fabricated current timestamp; ExportAsync reports it in the warning summary.
        obj[name] = value ?? 0L;
    }

    /// <summary>Only accept a genuinely stored numeric timestamp; missing/invalid returns null.</summary>
    private static long? GetExportMetadataInt64(JsonNode? metadata, string name)
    {
        var raw = JsonMerge.Get(metadata, name);
        return CockpitAccountSchema.TryGetInt64Value(raw, out var value) ? value : null;
    }

    private static bool IsUnknownTimestamp(JsonNode? node)
        => CockpitAccountSchema.TryGetInt64Value(node, out var timestamp) && timestamp == 0;

    private static async Task WriteExportAtomicAsync(string path, string content, bool overwrite, string? expectedHash, CancellationToken cancellationToken)
    {
        var targetExists = File.Exists(path);
        if (targetExists && !overwrite)
        {
            throw new OpenCodeException("目标文件已存在；已停止写入以避免覆盖。");
        }

        if (targetExists)
        {
            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                throw new OpenCodeException("缺少覆盖前的一致性校验值，已停止写入。");
            }

            if (!string.Equals(GetExportFileHash(path), expectedHash, StringComparison.Ordinal))
            {
                throw new OpenCodeException("目标文件已被其他程序修改；已停止写入以避免覆盖。");
            }
        }

        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = new UTF8Encoding(false).GetBytes(content);
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (targetExists)
            {
                if (!string.Equals(GetExportFileHash(path), expectedHash, StringComparison.Ordinal))
                {
                    throw new OpenCodeException("目标文件在写入期间被修改；已停止替换。");
                }

                File.Replace(temp, path, null);
            }
            else
            {
                // File.Move fails when the target has appeared; it never overwrites.
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort cleanup of our own temp file */ }
            }
        }
    }

    private static string GetExportFileHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
