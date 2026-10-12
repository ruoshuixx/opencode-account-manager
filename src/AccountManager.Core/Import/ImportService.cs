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

namespace AccountManager.Core.Import;

/// <summary>UI-facing preview row; never contains secret values.</summary>
public sealed class ImportPreviewRow
{
    public string Key = string.Empty;

    public string Label = string.Empty;

    public string Provider = string.Empty;

    public string Identity = string.Empty;

    public string Workspace = string.Empty;

    public string Plan = string.Empty;

    public string Expires = string.Empty;

    public string Status = string.Empty;

    public AccountKind Kind;
}

/// <summary>Preview result plus the internal rows consumed by <see cref="ImportService.ImportAsync"/>.</summary>
public sealed class ImportPreview
{
    public List<ImportPreviewRow> Rows { get; init; } = new();

    /// <summary>Internal rows (contain secrets); do not display.</summary>
    public object Payload { get; init; } = new();

    /// <summary>
    /// When true, only merges the real subscription expiry into matching existing accounts.
    /// Existing tokens, API keys and provider definitions are never touched, no account is created.
    /// </summary>
    public bool SubscriptionOnly { get; set; }

    /// <summary>
    /// When non-null, only rows whose <see cref="ImportRow.Key"/> is in this set are imported.
    /// Null means import every row. Lets the UI honor the per-row checkboxes.
    /// </summary>
    public HashSet<string>? SelectedKeys { get; set; }
}

/// <summary>Result of an import run. All messages are already redacted.</summary>
public sealed class ImportResult
{
    public string Backup = string.Empty;

    public int Failed;

    public List<string> Messages = new();
}

/// <summary>
/// Port of the <c>Importer.ps1</c> import path (<c>Read-ImportFile</c>, <c>Convert-Account</c>,
/// <c>Normalize-Account</c>, <c>Find-Matches</c>, <c>Same-Credential</c>,
/// <c>Save-ProtectedBackup</c>, <c>Write-Config</c>, <c>Import-Rows</c>). Backs up before every
/// write, never activates another account (activate index is fixed at -1) and redacts every
/// message. Secrets are never logged or returned.
/// </summary>
public sealed class ImportService
{
    private const long MaxImportBytes = 20L * 1024 * 1024;

    private readonly IOpenCodeApi _api;
    private readonly IConfigFile _configFile;
    private readonly BackupService _backups;
    private readonly SecretRegistry _secrets;
    private readonly AppLog _log;
    private readonly SubscriptionStore? _subscriptions;

    public ImportService(OpenCodeApi api, BackupService backups, SecretRegistry secrets, AppLog log, SubscriptionStore? subscriptions = null)
        : this(api, new ConfigFile(api.Cli), backups, secrets, log, subscriptions)
    {
    }

    /// <summary>Testable boundary: parsing/config imports never need a real CLI or account.</summary>
    public ImportService(IOpenCodeApi api, IConfigFile configFile, BackupService backups, SecretRegistry secrets, AppLog log, SubscriptionStore? subscriptions = null)
    {
        _api = api;
        _configFile = configFile;
        _backups = backups;
        _secrets = secrets;
        _log = log;
        _subscriptions = subscriptions;
    }

    /// <summary>
    /// Parses a Cockpit export array into preview rows without writing anything. Rejects files
    /// over 20MB, non-array roots, empty arrays and duplicate identities; requires the
    /// <c>chatgpt-browser</c> OAuth method on this OpenCode install.
    /// </summary>
    public async Task<ImportPreview> PreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        _log.Info("开始读取导入文件：" + path);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new OpenCodeException("无法读取 JSON 文件；请检查文件格式。");
        }

        if (new FileInfo(path).Length > MaxImportBytes)
        {
            throw new OpenCodeException("JSON 超过 20MB，已拒绝读取。");
        }

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new OpenCodeException("无法读取 JSON 文件；请检查文件格式。");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            throw new OpenCodeException("无法读取 JSON 文件；请检查文件格式。");
        }

        if (!raw.TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            throw new OpenCodeException("当前试用版需要 Cockpit 导出的账号数组。");
        }

        if (parsed is not JsonArray accounts)
        {
            throw new OpenCodeException("当前试用版需要 Cockpit 导出的账号数组。");
        }

        _secrets.Register(accounts);
        if (accounts.Count == 0)
        {
            throw new OpenCodeException("导出文件没有账号。");
        }

        var integration = await _api.GetIntegrationAsync("openai", cancellationToken).ConfigureAwait(false);
        var methods = JsonMerge.Get(JsonMerge.Get(integration, "data"), "methods") as JsonArray;
        string? oauthMethod = null;
        if (methods is not null)
        {
            foreach (var method in methods)
            {
                if (method is null) { continue; }
                if (JsonMerge.GetString(method, "type") == "oauth" && JsonMerge.GetString(method, "id") == "chatgpt-browser")
                {
                    oauthMethod = "chatgpt-browser";
                    break;
                }
            }
        }

        if (oauthMethod is null)
        {
            throw new OpenCodeException("此 OpenCode 没有 chatgpt-browser OAuth 方法，请使用兼容的 V2 版本。");
        }

        var rows = new List<ImportRow>();
        for (var i = 0; i < accounts.Count; i++)
        {
            ImportRow row;
            try
            {
                row = ConvertAccount(accounts[i], oauthMethod);
            }
            catch (Exception ex)
            {
                throw new OpenCodeException("第 " + (i + 1) + " 个账号：" + ex.Message);
            }

            _secrets.Register(row.Value);
            row.Key = "preview-" + row.Provider + "|" + Sha256Hex(row.Identity);
            rows.Add(row);
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!identities.Add(row.Provider + "\u0000" + row.Identity))
            {
                throw new OpenCodeException("导出文件中存在重复账号身份，请先去重；不会猜测哪份令牌更新。");
            }
        }

        _log.Info("解析成功：" + rows.Count + " 项；未执行写入。");

        return new ImportPreview
        {
            Rows = rows.Select(ToPreviewRow).ToList(),
            Payload = rows,
        };
    }

    /// <summary>
    /// Writes the previewed rows: update-or-create each credential (create then delete old),
    /// after an encrypted backup. Activation index is always -1.
    /// </summary>
    public async Task<ImportResult> ImportAsync(ImportPreview preview, CancellationToken cancellationToken = default)
    {
        if (preview is null) { throw new OpenCodeException("缺少导入预览数据。"); }
        if (preview.Payload is not List<ImportRow> rows) { throw new OpenCodeException("导入预览数据无效；请重新预览。"); }

        if (preview.SelectedKeys is not null)
        {
            rows = rows.Where(r => preview.SelectedKeys.Contains(r.Key)).ToList();
            if (rows.Count == 0) { throw new OpenCodeException("没有勾选任何账号，已取消导入。"); }
        }

        foreach (var row in rows)
        {
            _secrets.Register(row.Value);
            if (row.Definition is not null) { _secrets.Register(row.Definition); }
        }

        _log.Info("开始导入：" + rows.Count + " 项；激活选择索引=-1");

        var messages = new List<string>();
        var credentials = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        _secrets.Register(credentials);

        // Capture every source entry into the local mirror before any filtering, so the cache
        // keeps Cockpit fields OpenCode never stores (id_token claim, plan, org, quota history…).
        var cacheRecords = BuildCacheRecords(rows);

        if (preview.SubscriptionOnly)
        {
            var apiSkipped = rows.Count(r => r.Kind == AccountKind.Api);
            if (apiSkipped > 0)
            {
                Report(messages, "仅更新订阅模式：跳过 " + apiSkipped + " 个 API Key 账号，配置不变。", LogLevel.Info);
            }

            var merged = new List<ImportRow>();
            foreach (var row in rows)
            {
                if (row.Kind != AccountKind.Codex) { continue; }

                var desiredNode = JsonMerge.Get(JsonMerge.Get(row.Value, "metadata"), "subscriptionActiveUntil");
                if (desiredNode is null)
                {
                    Report(messages, row.Label + "：源数据没有订阅到期时间，跳过。", LogLevel.Info);
                    continue;
                }

                var matches = FindMatches(row, credentials);
                if (matches.Count == 0)
                {
                    Report(messages, row.Label + "：未找到匹配的现有账号，跳过（不新建）。", LogLevel.Info);
                    continue;
                }

                if (matches.Count > 1)
                {
                    throw new OpenCodeException("存在多个匹配账号，停止操作以免误删；请先在 OpenCode 中整理重复账号。");
                }

                // Take the existing credential as-is (tokens, keys, method, expiry) and only
                // overlay the subscription metadata; the source tokens are never written back.
                var oldValue = JsonMerge.Get(matches[0], "value");
                var mergedValue = oldValue is JsonObject existing ? (JsonObject)existing.DeepClone() : new JsonObject();
                if (JsonMerge.Get(mergedValue, "metadata") is not JsonObject metadata)
                {
                    metadata = new JsonObject();
                    mergedValue["metadata"] = metadata;
                }

                var currentToken = JsonMerge.Get(metadata, "subscriptionActiveUntil")?.ToJsonString() ?? string.Empty;
                if (string.Equals(currentToken, desiredNode.ToJsonString(), StringComparison.Ordinal))
                {
                    Report(messages, row.Label + "：订阅到期无变化，跳过。", LogLevel.Info);
                    continue;
                }

                metadata["subscriptionActiveUntil"] = desiredNode.DeepClone();
                row.Value = mergedValue;
                merged.Add(row);
            }

            rows = merged;
            _log.Info("仅更新订阅模式：仅合并订阅到期，准备写回 " + rows.Count + " 个 Codex 账号。");
        }

        ConfigDocument? configDocument = null;
        if (rows.Any(r => r.Kind == AccountKind.Api))
        {
            configDocument = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (JsonMerge.Get(configDocument.Config, "providers") is null)
            {
                configDocument.Config["providers"] = new JsonObject();
            }

            if (JsonMerge.Get(configDocument.Config, "providers") is not JsonObject providers)
            {
                throw new OpenCodeException("providers 配置不是对象，已停止。");
            }

            foreach (var row in rows)
            {
                if (row.Kind != AccountKind.Api) { continue; }

                JsonObject definition;
                if (providers.TryGetPropertyValue(row.Provider, out var existingNode))
                {
                    if (existingNode is not JsonObject existing)
                    {
                        throw new OpenCodeException("匹配供应商配置不是对象，停止更新。");
                    }

                    // Compare the original URL and credential before applying imported settings.
                    var refreshExistingVariants = CanRefreshImportedVariants(row, existing, credentials);
                    // Preserve unrelated settings and model catalog when a compact export omitted them.
                    definition = existing;
                    var oldPackage = JsonMerge.GetString(definition, "package");
                    definition["name"] = JsonMerge.Get(row.Definition, "name")?.DeepClone();
                    if (row.ProtocolSupplied || string.IsNullOrEmpty(JsonMerge.GetString(definition, "package")))
                    {
                        definition["package"] = JsonMerge.Get(row.Definition, "package")?.DeepClone();
                    }
                    else if (oldPackage is not null && OpenAiApiProtocol.IsManagedPackage(oldPackage))
                    {
                        definition["package"] = OpenAiApiProtocol.CanonicalPackage(oldPackage);
                    }

                    if (JsonMerge.Get(definition, "settings") is null)
                    {
                        definition["settings"] = new JsonObject();
                    }

                    if (JsonMerge.Get(definition, "settings") is not JsonObject settings)
                    {
                        throw new OpenCodeException("供应商 settings 配置不是对象，停止更新。");
                    }

                    settings["baseURL"] = JsonMerge.Get(JsonMerge.Get(row.Definition, "settings"), "baseURL")?.DeepClone();
                    var package = JsonMerge.GetString(definition, "package");
                    var changedToResponses = OpenAiApiProtocol.UsesResponses(package)
                        && !string.Equals(oldPackage, package, StringComparison.Ordinal);
                    if (OpenAiApiProtocol.UsesResponses(package) && (changedToResponses || settings["transport"] is null))
                    {
                        settings["transport"] = "http";
                    }
                    if (row.ModelsSupplied || JsonMerge.Get(definition, "models") is null)
                    {
                        definition["models"] = MergeImportedModels(JsonMerge.Get(definition, "models"), JsonMerge.Get(row.Definition, "models"), OpenAiApiProtocol.UsesResponses(package), JsonMerge.GetString(settings, "baseURL"));
                    }
                    if (refreshExistingVariants && definition["models"] is JsonObject refreshModels)
                    {
                        foreach (var (modelId, model) in refreshModels)
                        {
                            if (model is not JsonObject entry) { continue; }
                            var generated = ModelVariantBuilder.NewModelEntry(modelId, useResponses: true,
                                baseUrl: JsonMerge.GetString(settings, "baseURL"));
                            if (generated["variants"] is { } variants) { entry["variants"] = variants.DeepClone(); }
                        }
                    }
                    if (package is not null && OpenAiApiProtocol.IsManagedPackage(package) && definition["models"] is JsonObject models)
                    {
                        OpenAiApiProtocol.ApplyModelProtocol(models, package,
                            row.ProtocolSupplied ? OpenAiApiProtocol.UsesResponses(package) : null, changedToResponses);
                    }
                }
                else
                {
                    definition = (JsonObject)row.Definition!.DeepClone();
                }

                providers[row.Provider] = definition;
            }
        }

        foreach (var row in rows)
        {
            if (FindMatches(row, credentials).Count > 1)
            {
                throw new OpenCodeException("存在多个匹配旧账号，停止操作以免误删；请先在 OpenCode 中整理重复账号。");
            }
        }

        var backup = _backups.Save(credentials, configDocument?.Path, configDocument?.Raw);
        Report(messages, "加密备份：" + backup, LogLevel.Info);

        // Provider definitions contain no API keys; preserve all unrelated config fields.
        if (configDocument is not null)
        {
            await _configFile.WriteAsync(configDocument, cancellationToken).ConfigureAwait(false);
            Report(messages, "已更新 API 接口和模型配置；OpenCode V2 通常自动读取更改，不重启、不调用 reload。", LogLevel.Info);
        }

        var failed = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            try
            {
                // Recheck the latest state before each account update.
                var current = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
                var matches = FindMatches(row, current);
                if (matches.Count > 1) { throw new OpenCodeException("匹配账号数量发生变化，跳过此账号。"); }

                var old = matches.Count > 0 ? matches[0] : null;
                var oldId = old is null ? null : JsonMerge.GetString(old, "id");
                var oldValue = old is null ? null : JsonMerge.Get(old, "value");

                if (oldValue is not null && SameCredential(oldValue, row.Value))
                {
                    Report(messages, row.Label + "：凭据相同，跳过更新。", LogLevel.Info);
                    continue;
                }

                if (oldValue is not null && row.Kind == AccountKind.Codex)
                {
                    var oldExpires = JsonNum.ToLong(JsonMerge.Get(oldValue, "expires")) ?? 0;
                    var newExpires = JsonNum.ToLong(JsonMerge.Get(row.Value, "expires")) ?? 0;
                    if (oldExpires > newExpires)
                    {
                        Report(messages, row.Label + "：备份令牌到期时间早于现有令牌，为避免回滚，保留现有凭据。", LogLevel.Warn);
                        continue;
                    }
                }

                // Activate index is fixed at -1, so only an already-active old credential stays active.
                var activate = old is not null && JsonNum.ToBool(JsonMerge.Get(old, "active"));
                var label = row.Label;
                if (row.Kind == AccountKind.Codex) { label += " [" + row.Workspace + "]"; }

                var createdId = await _api.CreateCredentialAsync(row.Provider, label, row.Value, activate, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(createdId)) { throw new OpenCodeException("没有收到新凭据 ID，保留旧账号。"); }

                if (old is not null && !string.IsNullOrWhiteSpace(oldId))
                {
                    try
                    {
                        await _api.DeleteCredentialAsync(oldId!, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _log.Error("移除旧凭据失败", ex);
                        Report(messages, row.Label + "：新账号已保存，但旧凭据未移除；请在 OpenCode 中检查重复项。", LogLevel.Error);
                        continue;
                    }
                }

                Report(messages, row.Label + "：" + (old is not null ? "更新成功" : "导入成功"), LogLevel.Info);
            }
            catch (Exception ex)
            {
                failed++;
                _log.Error("账号写入异常", ex);
                Report(messages, row.Label + "：失败。" + ex.Message, LogLevel.Error);
            }
        }

        if (_subscriptions is not null && cacheRecords.Count > 0)
        {
            try
            {
                await _subscriptions.SaveAsync(cacheRecords, cancellationToken).ConfigureAwait(false);
                Report(messages, "已把 " + cacheRecords.Count + " 条 Cockpit 账户信息缓存到 OpenCode 数据目录（" + SubscriptionStore.FileName + "）。", LogLevel.Info);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Warn("写入账户缓存失败：" + ex.Message);
            }
        }

        if (configDocument is not null && _api is IProviderRuntimeReader runtimeReader)
        {
            var verifier = new ProviderRuntimeVerifier(runtimeReader);
            foreach (var row in rows.Where(r => r.Kind == AccountKind.Api))
            {
                var package = JsonMerge.GetString(configDocument.Config["providers"]?[row.Provider], "package");
                if (package is null || !OpenAiApiProtocol.IsManagedPackage(package)) { continue; }
                var runtime = await verifier.VerifyAsync(row.Provider, package).ConfigureAwait(false);
                Report(messages, row.Label + "：" + runtime.Message,
                    runtime.State == ProviderRuntimeState.Applied ? LogLevel.Info : LogLevel.Warn);
            }
        }

        Report(messages, "完成。失败或需检查：" + failed + "。保存成功不代表令牌/API 已联网验证。", failed > 0 ? LogLevel.Warn : LogLevel.Info);
        return new ImportResult { Backup = backup, Failed = failed, Messages = messages };
    }

    private ImportPreviewRow ToPreviewRow(ImportRow row) => new()
    {
        Key = row.Key,
        Label = _secrets.Redact(row.Label),
        Provider = _secrets.Redact(row.Provider),
        Identity = _secrets.Redact(row.Identity),
        Workspace = _secrets.Redact(row.Workspace),
        Plan = _secrets.Redact(row.Plan),
        Expires = _secrets.Redact(row.Expires),
        Status = _secrets.Redact(row.Status),
        Kind = row.Kind,
    };

    /// <summary>Port of <c>Normalize-Account</c>: pass through Cockpit shape or convert the legacy codex token shape.</summary>
    private static JsonNode NormalizeAccount(JsonNode? account)
    {
        if (account is null)
        {
            throw new OpenCodeException("不支持的账号结构：需要 Cockpit auth_mode，或 type=codex 的顶层令牌格式。");
        }

        var authMode = JsonMerge.GetString(account, "auth_mode");
        if (!string.IsNullOrEmpty(authMode))
        {
            if (account is not JsonObject nativeAccount)
            {
                throw new OpenCodeException("不支持的账号结构：需要 Cockpit auth_mode，或 type=codex 的顶层令牌格式。");
            }

            var canonical = CockpitSecretFields.Canonicalize(nativeAccount);
            if (canonical["api_wire_api"] is JsonValue wireNode && wireNode.TryGetValue<string>(out var wireText))
            {
                var normalizedWire = wireText.ToLowerInvariant();
                if (normalizedWire is "responses" or "chat_completions") { canonical["api_wire_api"] = normalizedWire; }
            }

            if (CockpitAccountSchema.GetOptionalFieldInvalidReason(canonical) is not null)
            {
                throw new OpenCodeException("账号字段格式不符合 Cockpit Codex 结构。");
            }

            foreach (var timeName in new[] { "created_at", "last_used" })
            {
                if (canonical.ContainsKey(timeName) && !CockpitAccountSchema.IsInt64Value(canonical[timeName]))
                {
                    throw new OpenCodeException("账号时间字段格式无效；需要整数 Unix 时间。");
                }
            }

            return canonical;
        }

        var type = JsonMerge.GetString(account, "type");
        var accessToken = JsonMerge.GetString(account, "access_token");
        var refreshToken = JsonMerge.GetString(account, "refresh_token");
        var idToken = JsonMerge.GetString(account, "id_token");
        if (string.Equals(type, "codex", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(refreshToken))
        {
            var claims = Jwt.ReadPayload(accessToken);
            var idClaims = Jwt.ReadPayload(idToken);
            var plan = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/auth"), "chatgpt_plan_type") ?? string.Empty;
            if (string.IsNullOrEmpty(plan))
            {
                plan = JsonMerge.GetString(JsonMerge.Get(idClaims, "https://api.openai.com/auth"), "chatgpt_plan_type") ?? string.Empty;
            }

            var legacy = new JsonObject
            {
                ["auth_mode"] = "oauth",
                ["email"] = JsonMerge.GetString(account, "email") ?? string.Empty,
                ["account_id"] = JsonMerge.GetString(account, "account_id") ?? string.Empty,
                ["plan_type"] = plan,
                ["tokens"] = new JsonObject
                {
                    ["access_token"] = accessToken,
                    ["refresh_token"] = refreshToken,
                    ["id_token"] = idToken ?? string.Empty,
                },
            };
            var legacySubscription = JsonMerge.Get(account, "subscription_active_until")
                ?? JsonMerge.Get(account, "subscriptionActiveUntil");
            if (legacySubscription is not null) { legacy["subscription_active_until"] = legacySubscription.DeepClone(); }
            return legacy;
        }

        throw new OpenCodeException("不支持的账号结构：需要 Cockpit auth_mode，或 type=codex 的顶层令牌格式。");
    }

    /// <summary>Port of <c>Convert-Account</c>: one Cockpit account to an internal import row.</summary>
    internal static ImportRow ConvertAccount(JsonNode? account, string oauthMethod)
    {
        var normalized = NormalizeAccount(account);
        var authMode = JsonMerge.GetString(normalized, "auth_mode");

        if (string.Equals(authMode, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = JsonMerge.Get(normalized, "tokens");
            var access = JsonMerge.GetString(tokens, "access_token");
            var refresh = JsonMerge.GetString(tokens, "refresh_token");
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
            {
                throw new OpenCodeException("OAuth 账号缺少访问令牌或刷新令牌。");
            }

            var claims = Jwt.ReadPayload(access);
            var exp = JsonNum.ToLong(JsonMerge.Get(claims, "exp"));
            if (exp is null || exp.Value == 0)
            {
                throw new OpenCodeException("无法从 OAuth 访问令牌读取到期时间。");
            }

            var accountId = JsonMerge.GetString(normalized, "account_id");
            if (string.IsNullOrEmpty(accountId))
            {
                accountId = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/auth"), "chatgpt_account_id");
            }

            if (string.IsNullOrEmpty(accountId))
            {
                throw new OpenCodeException("OAuth 账号缺少 account_id，不能安全导入。");
            }

            var email = JsonMerge.GetString(normalized, "email");
            if (string.IsNullOrEmpty(email))
            {
                email = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/profile"), "email");
            }

            if (string.IsNullOrEmpty(email)) { email = "Codex " + accountId; }

            var expired = exp.Value <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var workspace = accountId.Length > 8 ? accountId[^8..] : accountId;

            var metadata = new JsonObject
            {
                ["accountID"] = accountId,
                ["cockpitEmail"] = email,
            };
            CopyStoredTimes(normalized, metadata);
            var idToken = JsonMerge.GetString(tokens, "id_token");
            if (!string.IsNullOrWhiteSpace(idToken)) { metadata["id_token"] = idToken; }
            var plan = JsonMerge.GetString(normalized, "plan_type");
            if (!string.IsNullOrWhiteSpace(plan)) { metadata["planType"] = plan; }

            // Preserve a real subscription expiry when the source data provides one (Cockpit field),
            // otherwise fall back to the id_token/access_token claim chatgpt_subscription_active_until.
            var subscription = JsonMerge.Get(normalized, "subscription_active_until")
                ?? JsonMerge.Get(normalized, "subscriptionActiveUntil")
                ?? ReadSubscriptionFromTokens(normalized);
            if (subscription is not null) { metadata["subscriptionActiveUntil"] = subscription.DeepClone(); }

            return new ImportRow
            {
                Kind = AccountKind.Codex,
                Label = email,
                Provider = "openai",
                Identity = accountId + "|" + email.ToLowerInvariant(),
                Plan = JsonMerge.GetString(normalized, "plan_type") ?? string.Empty,
                Workspace = workspace,
                Expires = DateTimeOffset.FromUnixTimeSeconds(exp.Value).LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                Status = expired ? "访问令牌已过期（刷新是否有效未验证）" : "可导入（未联网验证）",
                Value = new JsonObject
                {
                    ["type"] = "oauth",
                    ["methodID"] = oauthMethod,
                    ["access"] = access,
                    ["refresh"] = refresh,
                    ["expires"] = exp.Value * 1000,
                    ["metadata"] = metadata,
                },
                Definition = null,
                Raw = (JsonObject)normalized.DeepClone(),
            };
        }

        if (!string.Equals(authMode, "apikey", StringComparison.OrdinalIgnoreCase)) { throw new OpenCodeException("不支持的登录方式。"); }

        // Cockpit writes lowercase openai_api_key. Legacy environment/generic aliases were
        // collapsed (and conflicts rejected) before this raw account can enter the mirror.
        var apiKey = JsonMerge.GetString(normalized, "openai_api_key");
        if (string.IsNullOrEmpty(apiKey)) { throw new OpenCodeException("API 账号缺少 openai_api_key。"); }

        var baseUrl = JsonMerge.GetString(normalized, "api_base_url");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new OpenCodeException("API 地址必须是无内嵌凭据、查询参数和片段的 HTTPS 地址。");
        }

        var sourceId = JsonMerge.GetString(normalized, "api_provider_id");
        if (string.IsNullOrEmpty(sourceId)) { sourceId = JsonMerge.GetString(normalized, "id"); }
        if (string.IsNullOrEmpty(sourceId)) { throw new OpenCodeException("API 账号缺少稳定的供应商 ID。"); }

        var provider = "cockpit-" + Sha256Hex(sourceId)[..12];
        var name = JsonMerge.GetString(normalized, "api_provider_name");
        if (string.IsNullOrEmpty(name)) { name = sourceId; }

        var catalogItems = new List<JsonNode?>();
        var catalogNode = JsonMerge.Get(normalized, "api_model_catalog");
        if (catalogNode is JsonArray catalogArray) { catalogItems.AddRange(catalogArray); }
        else if (catalogNode is not null) { throw new OpenCodeException("模型列表格式不受支持；需要字符串数组。"); }

        var modelsSupplied = catalogItems.Count > 0;
        var knownArk = string.Equals(uri.Host, "ark.cn-beijing.volces.com", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(uri.AbsolutePath.TrimEnd('/'), "/api/coding/v3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.AbsolutePath.TrimEnd('/'), "/api/plan/v3", StringComparison.OrdinalIgnoreCase));

        if (!modelsSupplied && knownArk)
        {
            catalogItems = new List<JsonNode?> { JsonValue.Create("ark-code-latest") };
        }

        var models = new JsonObject();
        foreach (var model in catalogItems)
        {
            if (model is not JsonValue value || !value.TryGetValue<string>(out var modelId) || string.IsNullOrWhiteSpace(modelId))
            {
                throw new OpenCodeException("模型列表格式不受支持；需要字符串数组。");
            }

            models[modelId] = new JsonObject { ["name"] = modelId };
        }

        if (models.Count == 0)
        {
            throw new OpenCodeException("API 账号没有模型列表；只有已识别的火山 Coding/Agent Plan 接口可使用最小默认模型。");
        }

        var wireText = JsonMerge.GetString(normalized, "api_wire_api");
        var protocolSupplied = !string.IsNullOrWhiteSpace(wireText);
        var wire = wireText?.ToLowerInvariant() ?? string.Empty;
        if (!protocolSupplied && knownArk) { wire = "chat_completions"; }

        var package = wire.ToLowerInvariant() switch
        {
            "chat_completions" => OpenAiApiProtocol.ChatPackage,
            "responses" => OpenAiApiProtocol.ResponsesPackage,
            _ => throw new OpenCodeException("不支持的 API 协议，不能猜测请求格式。"),
        };

        var apiSettings = new JsonObject { ["baseURL"] = uri.AbsoluteUri };
        if (wire == "responses")
        {
            apiSettings["transport"] = "http";
            foreach (var (modelId, model) in models)
            {
                if (model is JsonObject modelEntry) { ModelVariantBuilder.TryAddMissingVariants(modelEntry, modelId, useResponses: true, baseUrl: uri.AbsoluteUri); }
            }
        }
        var apiMetadata = new JsonObject { ["cockpitID"] = sourceId };
        CopyStoredTimes(normalized, apiMetadata);

        return new ImportRow
        {
            Kind = AccountKind.Api,
            Label = name,
            Provider = provider,
            Identity = sourceId,
            Workspace = "不适用",
            Plan = "API Key",
            Expires = "不适用",
            Status = modelsSupplied ? "可导入（未联网验证）" : "缺少模型列表：保留已有模型，否则仅 ark-code-latest",
            ModelsSupplied = modelsSupplied,
            ProtocolSupplied = protocolSupplied,
            Value = new JsonObject
            {
                ["type"] = "key",
                ["key"] = apiKey,
                ["metadata"] = apiMetadata,
            },
            Definition = new JsonObject
            {
                ["name"] = name,
                ["package"] = package,
                ["settings"] = apiSettings,
                ["models"] = models,
            },
            Raw = (JsonObject)normalized.DeepClone(),
        };
    }

    private static void CopyStoredTimes(JsonNode account, JsonObject metadata)
    {
        foreach (var field in new[] { "created_at", "last_used" })
        {
            if (CockpitAccountSchema.TryGetInt64Value(JsonMerge.Get(account, field), out var timestamp)) { metadata[field] = timestamp; }
        }
    }

    private static bool CanRefreshImportedVariants(ImportRow row, JsonObject existing, JsonArray credentials)
    {
        if (!OpenAiApiProtocol.UsesResponses(JsonMerge.GetString(row.Definition, "package"))) { return false; }
        var incomingUrl = JsonMerge.GetString(JsonMerge.Get(row.Definition, "settings"), "baseURL");
        var existingUrl = JsonMerge.GetString(JsonMerge.Get(existing, "settings"), "baseURL");
        if (!ModelVariantBuilder.IsArkPlanEndpoint(incomingUrl)
            || !ModelVariantBuilder.IsArkPlanEndpoint(existingUrl)) { return false; }
        // Host/scheme/port are already constrained. A trailing slash is not a different plan.
        if (!string.Equals(new Uri(incomingUrl!).AbsolutePath.TrimEnd('/'),
            new Uri(existingUrl!).AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) { return false; }

        var incomingKey = JsonMerge.GetString(row.Value, "key");
        if (string.IsNullOrWhiteSpace(incomingKey)) { return false; }
        var matches = FindMatches(row, credentials);
        if (matches.Count != 1) { return false; }
        var value = JsonMerge.Get(matches[0], "value");
        return string.Equals(JsonMerge.GetString(value, "type"), "key", StringComparison.OrdinalIgnoreCase)
            && string.Equals(incomingKey, JsonMerge.GetString(value, "key"), StringComparison.Ordinal);
    }

    private static JsonObject MergeImportedModels(JsonNode? existing, JsonNode? incoming, bool useResponses, string? baseUrl)
    {
        var result = new JsonObject();
        if (incoming is not JsonObject models) { return result; }
        foreach (var (id, value) in models)
        {
            var oldEntry = JsonMerge.Get(existing, id);
            // ConvertAccount synthesises variants for its preview. Remove those before merging:
            // existing models retain their exact variants; new ones use the final URL/protocol.
            var incomingEntry = value?.DeepClone();
            if (incomingEntry is JsonObject incomingObject) { incomingObject.Remove("variants"); }
            var merged = JsonMerge.Merge(oldEntry, incomingEntry);
            // Backfill tiers only for documented models that have none; a user's custom
            // variants are never replaced here (the same-URL+Key path does the refresh).
            if (merged is JsonObject mergedEntry)
            {
                ModelVariantBuilder.TryAddMissingVariants(mergedEntry, id, useResponses, baseUrl);
            }
            result[id] = merged;
        }
        return result;
    }

    /// <summary>
    /// Cockpit reads the real subscription expiry from the OAuth id_token JWT claim
    /// <c>https://api.openai.com/auth.chatgpt_subscription_active_until</c> (falling back to the
    /// access token). This recovers it for exports that only carry the raw tokens.
    /// </summary>
    private static JsonNode? ReadSubscriptionFromTokens(JsonNode? normalized)
    {
        var tokens = JsonMerge.Get(normalized, "tokens");
        foreach (var name in new[] { "id_token", "access_token" })
        {
            var payload = Jwt.ReadPayload(JsonMerge.GetString(tokens, name));
            var node = JsonMerge.Get(
                JsonMerge.Get(payload, "https://api.openai.com/auth"),
                "chatgpt_subscription_active_until");
            if (node is not null) { return node.DeepClone(); }
        }

        return null;
    }

    /// <summary>
    /// Builds the local mirror records for all source rows: every Cockpit field is preserved,
    /// token/key fields are sealed. Keyed like the mapper (accountID|email for Codex, source id
    /// for API providers).
    /// </summary>
    private static List<SubscriptionRecord> BuildCacheRecords(IEnumerable<ImportRow> rows)
    {
        var records = new List<SubscriptionRecord>();
        foreach (var row in rows)
        {
            if (row.Raw is null) { continue; }
            var metadata = JsonMerge.Get(row.Value, "metadata");
            var accountId = row.Kind == AccountKind.Codex
                ? (JsonMerge.GetString(metadata, "accountID") ?? JsonMerge.GetString(row.Raw, "account_id") ?? string.Empty)
                : row.Identity;
            if (string.IsNullOrWhiteSpace(accountId)) { continue; }

            var email = JsonMerge.GetString(metadata, "cockpitEmail")
                ?? JsonMerge.GetString(row.Raw, "email")
                ?? string.Empty;

            var activeUntil = TextOf(JsonMerge.Get(metadata, "subscriptionActiveUntil"))
                ?? TextOf(JsonMerge.Get(row.Raw, "subscription_active_until"))
                ?? TextOf(ReadSubscriptionFromTokens(row.Raw));

            var (cockpit, secrets) = SubscriptionStore.SplitSecrets(row.Raw);
            records.Add(new SubscriptionRecord
            {
                AccountId = accountId,
                Email = email,
                ActiveUntil = activeUntil,
                PlanType = row.Plan,
                Source = "import",
                Cockpit = cockpit,
                SealedSecrets = SubscriptionStore.SealSecrets(secrets),
            });
        }

        return records;
    }

    /// <summary>Reads a JSON string value (or its JSON text for non-strings); null when absent.</summary>
    private static string? TextOf(JsonNode? node)
    {
        if (node is null) { return null; }
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) { return text; }
        return node.ToJsonString();
    }

    /// <summary>Port of <c>Find-Matches</c>: credentials whose exact identity equals the row's.</summary>
    private static List<JsonNode> FindMatches(ImportRow row, JsonArray credentials)
    {
        var matches = new List<JsonNode>();
        foreach (var credential in credentials)
        {
            if (credential is null) { continue; }
            if (!string.Equals(JsonMerge.GetString(credential, "integrationID"), row.Provider, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (row.Kind == AccountKind.Codex)
            {
                var info = CredentialHelpers.GetIdentityInfo(credential);
                if (string.IsNullOrWhiteSpace(info.Identity)) { continue; }
                if (string.Equals(info.Identity, row.Identity, StringComparison.Ordinal)) { matches.Add(credential); }
            }
            else
            {
                var cockpitId = JsonMerge.GetString(JsonMerge.Get(JsonMerge.Get(credential, "value"), "metadata"), "cockpitID");
                if (string.Equals(cockpitId, row.Identity, StringComparison.Ordinal)) { matches.Add(credential); }
            }
        }

        return matches;
    }

    /// <summary>Port of <c>Same-Credential</c>: compare two credential value objects.</summary>
    private static bool SameCredential(JsonNode? left, JsonNode? right)
    {
        var leftType = JsonMerge.GetString(left, "type");
        if (!string.Equals(leftType, JsonMerge.GetString(right, "type"), StringComparison.OrdinalIgnoreCase)) { return false; }

        if (string.Equals(leftType, "key", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(JsonMerge.GetString(left, "key"), JsonMerge.GetString(right, "key"), StringComparison.Ordinal);
        }

        return string.Equals(JsonMerge.GetString(left, "access"), JsonMerge.GetString(right, "access"), StringComparison.Ordinal)
            && string.Equals(JsonMerge.GetString(left, "refresh"), JsonMerge.GetString(right, "refresh"), StringComparison.Ordinal)
            && string.Equals(JsonMerge.GetString(left, "methodID"), JsonMerge.GetString(right, "methodID"), StringComparison.OrdinalIgnoreCase)
            && (JsonNum.ToLong(JsonMerge.Get(left, "expires")) ?? 0) == (JsonNum.ToLong(JsonMerge.Get(right, "expires")) ?? 0)
            && string.Equals(
                JsonMerge.GetString(JsonMerge.Get(left, "metadata"), "accountID"),
                JsonMerge.GetString(JsonMerge.Get(right, "metadata"), "accountID"),
                StringComparison.Ordinal)
            && string.Equals(SubscriptionToken(left), SubscriptionToken(right), StringComparison.Ordinal);
    }

    /// <summary>Subscription expiry as stored in credential metadata, for change detection.</summary>
    private static string SubscriptionToken(JsonNode? value)
    {
        var metadata = JsonMerge.Get(value, "metadata");
        var node = JsonMerge.Get(metadata, "subscriptionActiveUntil") ?? JsonMerge.Get(metadata, "subscription_active_until");
        return node?.ToJsonString() ?? string.Empty;
    }

    private static string Sha256Hex(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private void Report(List<string> messages, string message, LogLevel level)
    {
        var safe = _secrets.Redact(message);
        messages.Add(safe);
        switch (level)
        {
            case LogLevel.Warn:
                _log.Warn(safe);
                break;
            case LogLevel.Error:
                _log.Error(safe);
                break;
            default:
                _log.Info(safe);
                break;
        }
    }

    /// <summary>Internal preview/import row. Contains secrets; never shown or logged.</summary>
    internal sealed class ImportRow
    {
        public AccountKind Kind;
        public string Key = string.Empty;
        public string Label = string.Empty;
        public string Provider = string.Empty;
        public string Identity = string.Empty;
        public string Workspace = string.Empty;
        public string Plan = string.Empty;
        public string Expires = string.Empty;
        public string Status = string.Empty;
        public bool ModelsSupplied;
        public bool ProtocolSupplied;
        public JsonObject Value = new();
        public JsonObject? Definition;

        /// <summary>Full source entry (for the local account cache); contains secrets, never shown.</summary>
        public JsonObject? Raw;
    }
}
