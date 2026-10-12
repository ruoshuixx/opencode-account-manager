using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AccountManager.Core.Accounts;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.Models;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;

namespace AccountManager.Core.Actions;

/// <summary>
/// Faithful C# port of <c>Actions.ps1</c>: delete managed Codex/API accounts and edit API
/// provider definitions (with optional key rotation). The original safety boundaries are
/// preserved: backup before any destructive write, exact-ID validation, refusal of ambiguous
/// cases, "create new before deleting old" key rotation, CAS-guarded config writes, read-back
/// verification, and explicit partial/failure warnings. Never calls a reload endpoint and
/// never edits the database.
/// </summary>
public sealed partial class AccountActions
{
    private static readonly JsonSerializerOptions WriteOptions = CoreJson.Indented;

    private static readonly Regex IndirectExpression = new(@"\{\s*(env|file)\s*:", RegexOptions.Compiled);

    private const string ConfigReloadWarning =
        "配置已写入磁盘；OpenCode V2 通常会自动读取更改。本工具不重启服务或调用 reload，也不发送模型验证请求。";

    private const string OverrideRefuseMessage =
        "该供应商从 Authorization 请求头或环境变量读取密钥，直接替换会覆盖配置/环境而不安全；已拒绝在未修改系统环境变量的前提下继续。";

    private const string DuplicateMessage =
        "检测到其它配置文档（项目或更高优先级）也定义了该供应商，改动全局定义会超出预期范围；已在写入任何内容前停止。";

    private readonly IOpenCodeApi _api;
    private readonly IConfigFile _configFile;
    private readonly BackupService _backups;
    private readonly SecretRegistry _secrets;
    private readonly AppLog _log;
    private readonly AccountMapper _mapper;

    public AccountActions(IOpenCodeApi api, IConfigFile configFile, BackupService backups,
                          SecretRegistry secrets, AppLog log, AccountMapper mapper)
    {
        _api = api;
        _configFile = configFile;
        _backups = backups;
        _secrets = secrets;
        _log = log;
        _mapper = mapper;
    }

    // ------------------------------------------------------------------
    // Public operations
    // ------------------------------------------------------------------

    /// <summary>Original <c>Remove-ManagedAccount</c>: delete a Codex database credential, or
    /// an API provider definition plus its local key credential.</summary>
    public async Task<DeleteResult> RemoveManagedAccountAsync(string credentialId, string? providerId, CancellationToken ct = default)
    {
        var credentialIdText = credentialId ?? string.Empty;
        var providerIdText = providerId ?? string.Empty;

        var credentials = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(credentialIdText))
        {
            var matches = credentials
                .Where(c => c is not null && Ordinal(GetString(c, "id"), credentialIdText))
                .ToList();
            if (matches.Count == 0) { throw new OpenCodeException("未找到该凭据；它可能已被移除，请刷新后重试。"); }
            if (matches.Count > 1) { throw new OpenCodeException("凭据 ID 不唯一，已停止以避免误删。"); }

            var credential = matches[0]!;
            var integration = GetString(credential, "integrationID");
            if (!string.IsNullOrEmpty(providerIdText))
            {
                if (!Ordinal(integration, providerIdText)) { throw new OpenCodeException("凭据与所选供应商不匹配，已停止。"); }
            }
            else
            {
                providerIdText = integration;
            }

            var value = GetValue(credential, "value");
            if (Ordinal(integration, "openai")
                && EqIgnoreCase(GetString(value, "type"), "oauth")
                && CredentialHelpers.IsCodexOAuthMethod(GetString(value, "methodID")))
            {
                return await RemoveCodexCredentialAsync(credential, credentials, ct).ConfigureAwait(false);
            }

            if (EqIgnoreCase(GetString(value, "type"), "key"))
            {
                if (string.IsNullOrWhiteSpace(providerIdText)) { throw new OpenCodeException("无法确定 API 凭据所属供应商，已停止。"); }
                return await RemoveApiProviderAsync(credential, credentials, providerIdText, ct).ConfigureAwait(false);
            }

            throw new OpenCodeException("不支持的凭据类型；已停止以避免误删。");
        }

        if (string.IsNullOrWhiteSpace(providerIdText)) { throw new OpenCodeException("缺少凭据 ID 和供应商 ID，无法确定要删除的内容。"); }
        return await RemoveApiProviderAsync(null, credentials, providerIdText, ct).ConfigureAwait(false);
    }

    /// <summary>Original <c>Update-ManagedApiProvider</c>: edit an API provider's HTTPS endpoint
    /// and model list, optionally rotating its key (create new → verify → write config → delete old).</summary>
    public async Task<ApiSaveResult> UpdateManagedApiProviderAsync(string credentialId, string providerId,
        string baseUrl, IReadOnlyList<string> modelIds, string? newKey, CancellationToken ct = default)
        => await UpdateManagedApiProviderAsync(credentialId, providerId, null, baseUrl, modelIds, newKey, ct).ConfigureAwait(false);

    /// <summary>Changes the display name without changing provider/model reference IDs.</summary>
    public async Task<ApiSaveResult> UpdateManagedApiProviderAsync(string credentialId, string providerId,
        string? displayName, string baseUrl, IReadOnlyList<string> modelIds, string? newKey, CancellationToken ct = default)
        => await UpdateManagedApiProviderAsync(credentialId, providerId, displayName, baseUrl, modelIds, newKey, null, ct).ConfigureAwait(false);

    /// <summary>Choose Chat Completions/Responses; null preserves the existing package for older callers.</summary>
    public async Task<ApiSaveResult> UpdateManagedApiProviderAsync(string credentialId, string providerId,
        string? displayName, string baseUrl, IReadOnlyList<string> modelIds, string? newKey, bool? useResponses, CancellationToken ct = default)
    {
        var credentialIdText = credentialId ?? string.Empty;
        var providerIdText = providerId ?? string.Empty;
        var newKeyText = newKey ?? string.Empty;

        // Register the new key immediately so no later error text can leak it.
        if (!string.IsNullOrEmpty(newKeyText)) { _secrets.RegisterValue(newKeyText); }

        var name = displayName is null ? null : ValidateProviderName(displayName);

        var baseUrlText = (baseUrl ?? string.Empty).Trim();
        if (!HttpsEndpoint(baseUrlText)) { throw new OpenCodeException("接口地址必须是无内嵌凭据、查询参数和片段的 HTTPS 地址。"); }
        var modelMap = ModelMap(modelIds);

        var credentials = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        JsonNode? oldCredential = null;
        if (!string.IsNullOrEmpty(credentialIdText))
        {
            var matches = credentials
                .Where(c => c is not null && Ordinal(GetString(c, "id"), credentialIdText))
                .ToList();
            if (matches.Count != 1) { throw new OpenCodeException("未找到唯一匹配的凭据；请刷新后重试。"); }
            oldCredential = matches[0];
            var integration = GetString(oldCredential, "integrationID");
            if (string.IsNullOrEmpty(providerIdText)) { providerIdText = integration; }
            else if (!Ordinal(integration, providerIdText)) { throw new OpenCodeException("凭据与所选供应商不匹配，已停止。"); }

            if (!EqIgnoreCase(GetString(GetValue(oldCredential, "value"), "type"), "key")) { throw new OpenCodeException("仅支持编辑 API Key 供应商。"); }

            var sameProvider = ProviderCredentials(credentials, providerIdText);
            if (sameProvider.Count != 1) { throw new OpenCodeException("该供应商存在多份本地凭据，请先在 OpenCode 中处理；已停止。"); }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(providerIdText)) { throw new OpenCodeException("缺少凭据 ID 和供应商 ID，无法确定要编辑的供应商。"); }
            if (ProviderCredentials(credentials, providerIdText).Count > 0)
            {
                throw new OpenCodeException("该供应商已有本地凭据，请刷新后使用带凭据的编辑；已停止。");
            }
        }

        var oldId = string.Empty;
        Snapshot? oldSnapshot = null;
        if (oldCredential is not null)
        {
            oldId = GetString(oldCredential, "id");
            oldSnapshot = CredentialSnapshot(oldCredential);
        }

        var plain = await GetPlainConfigAsync(ct).ConfigureAwait(false);
        var globalPath = plain.Path;
        var originalCopy = (JsonObject)plain.ConfigCopy.DeepClone()!;
        var definition = ProviderProperty(GetValue(plain.ConfigCopy, "providers"), providerIdText);
        if (definition is null) { throw new OpenCodeException("全局配置中找不到该供应商定义；已停止，未修改任何内容。"); }
        if (!KnownPackage(GetString(definition, "package")))
        {
            throw new OpenCodeException("该供应商不是本工具支持的 OpenAI 兼容 / Responses 包；已停止，避免破坏未知配置。");
        }
        var oldPackage = GetString(definition, "package");
        var expectedPackage = useResponses.HasValue
            ? OpenAiApiProtocol.PackageFor(useResponses.Value)
            : OpenAiApiProtocol.CanonicalPackage(oldPackage);

        if (!string.IsNullOrEmpty(newKeyText) && ProviderKeyIsIndirect(definition)) { throw new OpenCodeException(OverrideRefuseMessage); }

        var documents = await GetConfigDocumentsAsync(ct).ConfigureAwait(false);
        AssertSingleGlobalDefinition(documents, providerIdText, globalPath);

        var backup = _backups.Save(credentials, plain.Path, plain.Raw);
        _log.Info("已生成加密备份（API 更新）：" + backup);

        // Post-backup re-validation: single definition, target snapshot and provider credential count unchanged.
        var documentsAfter = await GetConfigDocumentsAsync(ct).ConfigureAwait(false);
        AssertSingleGlobalDefinition(documentsAfter, providerIdText, globalPath);
        var latest = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        if (oldCredential is not null)
        {
            var sameLatest = ProviderCredentials(latest, providerIdText);
            if (sameLatest.Count != 1) { throw new OpenCodeException("编辑前复核失败：该供应商凭据数量已变化，未修改。加密备份：" + backup); }
            if (!CredentialSnapshotEqual(sameLatest[0], oldSnapshot)) { throw new OpenCodeException("编辑前复核失败：目标凭据的激活状态或值已变化（可能已被刷新），未修改。加密备份：" + backup); }
        }
        else if (ProviderCredentials(latest, providerIdText).Count > 0)
        {
            throw new OpenCodeException("编辑前复核发现供应商新增了本地凭据，未修改。加密备份：" + backup);
        }
        var latestIds = latest.Where(c => c is not null).Select(c => GetString(c, "id")).ToList();

        // Build the new definition: update name/package/baseURL/models; drop apiKey only when rotating.
        var newConfig = (JsonObject)plain.ConfigCopy.DeepClone()!;
        var newProvider = ProviderProperty(GetValue(newConfig, "providers"), providerIdText);
        if (newProvider is null) { throw new OpenCodeException("全局配置中找不到该供应商定义；已停止，未修改任何内容。"); }
        if (name is not null) { SetValue(newProvider, "name", JsonValue.Create(name)); }
        SetValue(newProvider, "package", JsonValue.Create(expectedPackage));
        var newSettings = GetValue(newProvider, "settings");
        if (newSettings is null)
        {
            newSettings = new JsonObject();
            SetValue(newProvider, "settings", newSettings);
        }
        if (newSettings is not JsonObject) { throw new OpenCodeException("该供应商 settings 不是对象，已停止。"); }
        SetValue(newSettings, "baseURL", JsonValue.Create(baseUrlText));
        var changedToResponses = OpenAiApiProtocol.UsesResponses(expectedPackage) && !Ordinal(oldPackage, expectedPackage);
        if (OpenAiApiProtocol.UsesResponses(expectedPackage)
            && (changedToResponses || GetValue(newSettings, "transport") is null))
        {
            SetValue(newSettings, "transport", JsonValue.Create("http"));
        }
        var mergedModels = MergeModelCatalog(GetValue(newProvider, "models"), modelMap, OpenAiApiProtocol.UsesResponses(expectedPackage), baseUrlText);
        OpenAiApiProtocol.ApplyModelProtocol(mergedModels, expectedPackage, useResponses, changedToResponses);
        SetValue(newProvider, "models", mergedModels);
        if (!string.IsNullOrEmpty(newKeyText)) { RemoveValue(newSettings, "apiKey"); }
        var expectedRaw = newConfig.ToJsonString(WriteOptions);

        // Branch 1: keep existing key — update config only, preserve settings/env.
        if (string.IsNullOrEmpty(newKeyText))
        {
            var newInfo = new ConfigDocument { Path = globalPath, Raw = plain.Raw, Config = newConfig };
            try { await _configFile.WriteAsync(newInfo, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new OpenCodeException("保存全局配置失败，未修改任何凭据。加密备份：" + backup + "；" + _secrets.Redact(ex.Message));
            }

            ConfigDocument? written = null;
            try { written = await ReadConfigAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("API 更新后回读配置失败：" + ex.Message);
                return UpdateResult(credentialIdText, providerIdText, backup, false,
                    "配置写入后无法回读确认；未修改任何凭据，请手动检查磁盘。加密备份：" + backup);
            }
            if (written is null || !SamePath(written.Path, globalPath) || !Ordinal(written.Raw, expectedRaw))
            {
                return UpdateResult(credentialIdText, providerIdText, backup, false,
                    "配置写入后磁盘内容与预期不一致（可能被并发修改）；未回滚用户改动，请手动检查。加密备份：" + backup);
            }

            try { await AssertDefinitionSavedAsync(globalPath, providerIdText, baseUrlText, modelMap, expectedPackage, backup, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var rolled = await ConfigRollbackAsync(globalPath, originalCopy, expectedRaw, ct).ConfigureAwait(false);
                var suffix = rolled ? " 已回滚全局配置。" : " 检测到并发修改，未强行回滚。";
                throw new OpenCodeException(_secrets.Redact(ex.Message) + suffix);
            }

            _log.Info("已更新 API 供应商配置（保留原 Key）：" + providerIdText);
            return UpdateResult(credentialIdText, providerIdText, backup, true, ConfigReloadWarning);
        }

        // Branch 2: rotate key — create new credential first, verify, write config, then delete old.
        JsonNode metadata = NewAccountMetadata();
        if (oldCredential is not null)
        {
            var oldMetadata = GetValue(GetValue(oldCredential, "value"), "metadata");
            if (oldMetadata is not null) { metadata = oldMetadata.DeepClone()!; }
        }
        var activate = true;
        if (oldCredential is not null) { activate = JsonNum.ToBool(GetValue(oldCredential, "active")); }
        var label = name ?? GetString(definition, "name");
        if (string.IsNullOrWhiteSpace(label)) { label = providerIdText; }

        // A read failure (e.g. json + jsonc both present) must propagate unchanged.
        var beforeWrite = await ReadConfigAsync(ct).ConfigureAwait(false);
        if (beforeWrite is null || !SamePath(beforeWrite.Path, globalPath) || !Ordinal(beforeWrite.Raw, plain.Raw))
        {
            throw new OpenCodeException("创建新凭据前复核失败：全局配置已被并发修改，未创建任何凭据。加密备份：" + backup);
        }

        var createdValue = new JsonObject
        {
            ["type"] = "key",
            ["key"] = newKeyText,
            ["metadata"] = metadata.DeepClone(),
        };

        string newId;
        try
        {
            newId = await _api.CreateCredentialAsync(providerIdText, label, createdValue, activate, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OpenCodeException("创建新 Key 凭据失败，已保留原凭据且未修改配置。加密备份：" + backup + "；" + _secrets.Redact(ex.Message));
        }

        // Verify the new credential exactly before touching config.
        List<JsonNode?>? verify;
        try { verify = await GetFreshCredentialsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { verify = null; }

        var newMatches = false;
        if (!string.IsNullOrEmpty(newId) && !Ordinal(newId, oldId))
        {
            var owned = !latestIds.Any(existing => Ordinal(existing, newId));
            if (owned && verify is not null)
            {
                var newFound = verify.Where(c => c is not null && Ordinal(GetString(c, "id"), newId)).ToList();
                if (newFound.Count == 1) { newMatches = NewCredentialMatches(newFound[0]!, newId, providerIdText, newKeyText, activate, metadata); }
            }
        }

        if (!newMatches)
        {
            var cleanupOk = await RemoveNewCredentialIfSafeAsync(newId, oldId, providerIdText, newKeyText, latestIds, ct).ConfigureAwait(false);
            var tail = cleanupOk
                ? " 已清理未确认的新凭据。"
                : " 无法安全清理新凭据（绝不删除非本次创建的凭据），请手动检查。";
            throw new OpenCodeException("未能在创建后确认新 Key 凭据（ID 缺失、与既有 ID 冲突或内容不匹配）；未修改配置、未删除原凭据，原状保留。" + tail + "加密备份：" + backup);
        }

        var rotateInfo = new ConfigDocument { Path = globalPath, Raw = plain.Raw, Config = newConfig };
        try { await _configFile.WriteAsync(rotateInfo, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cleanupOk = await RemoveNewCredentialIfSafeAsync(newId, oldId, providerIdText, newKeyText, latestIds, ct).ConfigureAwait(false);
            if (cleanupOk) { throw new OpenCodeException("保存全局配置失败，已删除刚创建的新 Key 凭据，原凭据未改动。加密备份：" + backup); }
            throw new OpenCodeException("保存全局配置失败，且无法安全清理刚创建的新 Key 凭据；新旧凭据均未擅自删除，请手动检查。加密备份：" + backup);
        }

        ConfigDocument? written2 = null;
        try { written2 = await ReadConfigAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("API Key 轮换后回读配置失败：" + ex.Message);
            return UpdateResult(newId, providerIdText, backup, false,
                "配置写入后无法回读确认；未删除旧凭据（新旧两份均保留），请手动检查。加密备份：" + backup);
        }
        if (written2 is null || !SamePath(written2.Path, globalPath) || !Ordinal(written2.Raw, expectedRaw))
        {
            return UpdateResult(newId, providerIdText, backup, false,
                "配置写入后磁盘内容与预期不一致（可能被并发修改）；未删除旧凭据，也不回滚用户改动，请手动检查。加密备份：" + backup);
        }

        try
        {
            await AssertDefinitionSavedAsync(globalPath, providerIdText, baseUrlText, modelMap, expectedPackage, backup, ct).ConfigureAwait(false);
            await AssertApiKeyRemovedAsync(globalPath, providerIdText, backup, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var rolled = await ConfigRollbackAsync(globalPath, originalCopy, expectedRaw, ct).ConfigureAwait(false);
            var cleanupOk = await RemoveNewCredentialIfSafeAsync(newId, oldId, providerIdText, newKeyText, latestIds, ct).ConfigureAwait(false);
            var suffix = rolled ? " 已回滚全局配置。" : " 检测到并发修改，未强行回滚。";
            suffix += cleanupOk ? " 已清理新 Key 凭据。" : " 未能清理新 Key 凭据，请手动检查。";
            throw new OpenCodeException(_secrets.Redact(ex.Message) + suffix + " 加密备份：" + backup);
        }

        // Delete the old credential. On failure keep both and warn explicitly.
        if (!string.IsNullOrEmpty(oldId) && !Ordinal(oldId, newId))
        {
            try { await _api.DeleteCredentialAsync(oldId, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return UpdateResult(newId, providerIdText, backup, false,
                    "新 Key 凭据与配置已保存，但旧凭据未移除（新旧两份均保留）；请手动检查重复项。加密备份：" + backup);
            }

            List<JsonNode?> after;
            try { after = await GetFreshCredentialsAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return UpdateResult(newId, providerIdText, backup, false,
                    "新 Key 已保存，但删除旧凭据后无法回读确认；请手动检查是否残留旧凭据。加密备份：" + backup);
            }
            if (after.Any(c => c is not null && Ordinal(GetString(c, "id"), oldId)))
            {
                return UpdateResult(newId, providerIdText, backup, false,
                    "新 Key 已保存，但回读仍发现旧凭据，无法确认已移除；请手动检查。加密备份：" + backup);
            }
        }

        // Final exact verification that the new credential still exists and matches.
        List<JsonNode?> final;
        try { final = await GetFreshCredentialsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UpdateResult(newId, providerIdText, backup, false,
                "无法回读确认新 Key 凭据的最终状态；请手动检查。加密备份：" + backup);
        }
        var finalFound = final.Where(c => c is not null && Ordinal(GetString(c, "id"), newId)).ToList();
        if (finalFound.Count != 1 || !NewCredentialMatches(finalFound[0]!, newId, providerIdText, newKeyText, activate, metadata))
        {
            return UpdateResult(newId, providerIdText, backup, false,
                "配置已更新，但新 Key 凭据的最终状态未通过精确校验；请手动检查凭据状态。加密备份：" + backup);
        }
        if (!string.IsNullOrEmpty(oldId) && final.Any(c => c is not null && Ordinal(GetString(c, "id"), oldId)))
        {
            return UpdateResult(newId, providerIdText, backup, false,
                "配置已更新，但回读仍发现旧凭据；请手动检查重复项。加密备份：" + backup);
        }

        _log.Info("已更新 API 供应商并完成 Key 轮换：" + providerIdText);
        return UpdateResult(newId, providerIdText, backup, true, ConfigReloadWarning);
    }

    // ------------------------------------------------------------------
    // Delete: Codex
    // ------------------------------------------------------------------

    private async Task<DeleteResult> RemoveCodexCredentialAsync(JsonNode credential, List<JsonNode?> credentials, CancellationToken ct)
    {
        var id = GetString(credential, "id");
        var label = SafeCodexLabel(credential);

        // PORTS contract: the currently-active Codex account is refused as a result
        // (Removed=false + explicit Warning), never as an unhandled exception.
        if (JsonNum.ToBool(GetValue(credential, "active")))
        {
            _log.Warn("拒绝删除当前激活的 Codex 账号：" + id);
            return RemoveResult(false, AccountKind.Codex, id, "openai", label, string.Empty,
                "不能删除当前正在使用的 Codex 账号；请先在 OpenCode 中切换到其它账号，然后重试。");
        }

        var snapshot = CredentialSnapshot(credential);
        var backup = _backups.Save(credentials, null, null);
        _log.Info("已生成加密备份（Codex 删除）：" + backup);

        // Post-backup re-check: ID unique, type/identity/active/value unchanged.
        var latest = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        var found = latest.Where(c => c is not null && Ordinal(GetString(c, "id"), id)).ToList();
        if (found.Count != 1)
        {
            throw new OpenCodeException("删除前复核失败：凭据数量或 ID 已变化，未删除任何内容。加密备份：" + backup);
        }
        var fresh = found[0]!;
        var freshValue = GetValue(fresh, "value");
        if (!Ordinal(GetString(fresh, "integrationID"), "openai")
            || !EqIgnoreCase(GetString(freshValue, "type"), "oauth")
            || !CredentialHelpers.IsCodexOAuthMethod(GetString(freshValue, "methodID")))
        {
            throw new OpenCodeException("删除前复核失败：凭据类型已变化，未删除任何内容。加密备份：" + backup);
        }
        if (JsonNum.ToBool(GetValue(fresh, "active")))
        {
            _log.Warn("删除前复核发现 Codex 账号已成为当前账号：" + id);
            return RemoveResult(false, AccountKind.Codex, id, "openai", label, backup,
                "删除前复核发现该账号已成为当前账号；请先切换后重试，未删除。加密备份：" + backup);
        }
        if (!CredentialSnapshotEqual(fresh, snapshot))
        {
            throw new OpenCodeException("删除前复核失败：凭据身份或值已变化（可能已在别处刷新），未删除任何内容。加密备份：" + backup);
        }

        try { await _api.DeleteCredentialAsync(id, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The DELETE may have reached the server; never claim it did not happen.
            _log.Warn("Codex 删除请求异常：" + ex.Message);
            return RemoveResult(false, AccountKind.Codex, id, "openai", label, backup,
                "删除请求异常，可能已经到达服务端，无法确认是否完成；请先读取 OpenCode 核对。未更改配置，也不自动重试。加密备份：" + backup);
        }

        List<JsonNode?> after;
        try { after = await GetFreshCredentialsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("Codex 删除后回读失败：" + ex.Message);
            return RemoveResult(false, AccountKind.Codex, id, "openai", label, backup,
                "删除请求已发送，但无法回读确认；未做任何回滚（无法确认当前状态）。请手动检查。加密备份：" + backup);
        }
        if (after.Any(c => c is not null && Ordinal(GetString(c, "id"), id)))
        {
            return RemoveResult(false, AccountKind.Codex, id, "openai", label, backup,
                "删除请求已发送，但回读仍发现该凭据，无法确认已删除；请重试或手动检查。加密备份：" + backup);
        }

        _log.Info("已删除 Codex 凭据：" + id);
        return RemoveResult(true, AccountKind.Codex, id, "openai", label, backup, string.Empty);
    }

    // ------------------------------------------------------------------
    // Delete: API provider (+ local key credential)
    // ------------------------------------------------------------------

    private async Task<DeleteResult> RemoveApiProviderAsync(JsonNode? credential, List<JsonNode?> credentials, string providerId, CancellationToken ct)
    {
        var targetId = credential is null ? string.Empty : GetString(credential, "id");
        var snapshot = CredentialSnapshot(credential);

        var plain = await GetPlainConfigAsync(ct).ConfigureAwait(false);
        var globalPath = plain.Path;
        var originalCopy = (JsonObject)plain.ConfigCopy.DeepClone()!;
        var providers = GetValue(plain.ConfigCopy, "providers");
        var definition = ProviderProperty(providers, providerId);
        if (definition is null) { throw new OpenCodeException("全局配置中找不到该供应商定义；已停止，未删除任何内容。"); }
        if (!KnownPackage(GetString(definition, "package")))
        {
            throw new OpenCodeException("该供应商不是本工具支持的 OpenAI 兼容 / Responses 包；已停止，避免破坏未知配置。");
        }

        var label = _secrets.Redact(GetString(definition, "name"));
        if (string.IsNullOrWhiteSpace(label)) { label = _secrets.Redact(providerId); }

        // Initial check: exactly one local credential per provider (including unknown types);
        // a config-only delete is refused when any credential exists for the provider.
        var sameProvider = ProviderCredentials(credentials, providerId);
        if (credential is not null)
        {
            if (sameProvider.Count != 1) { throw new OpenCodeException("该供应商存在多份本地凭据，删除会影响其它账号；已停止。"); }
            if (!CredentialSnapshotEqual(sameProvider[0], snapshot)) { throw new OpenCodeException("目标凭据已变化；已停止。"); }
        }
        else if (sameProvider.Count > 0)
        {
            throw new OpenCodeException("该供应商存在本地凭据，请刷新后改用带凭据的删除；已停止。");
        }

        var documents = await GetConfigDocumentsAsync(ct).ConfigureAwait(false);
        AssertSingleGlobalDefinition(documents, providerId, globalPath);

        var backup = _backups.Save(credentials, plain.Path, plain.Raw);
        _log.Info("已生成加密备份（API 删除）：" + backup);

        // Post-backup re-validation of documents and credential snapshot.
        var documentsAfter = await GetConfigDocumentsAsync(ct).ConfigureAwait(false);
        AssertSingleGlobalDefinition(documentsAfter, providerId, globalPath);
        await AssertApiRemovalStillValidAsync(credential, snapshot, providerId, targetId, backup, ct).ConfigureAwait(false);

        var beforeWrite = await ReadConfigAsync(ct).ConfigureAwait(false);
        if (beforeWrite is null || !SamePath(beforeWrite.Path, globalPath) || !Ordinal(beforeWrite.Raw, plain.Raw))
        {
            throw new OpenCodeException("删除前复核失败：全局配置已被并发修改，未删除任何内容。加密备份：" + backup);
        }

        // Remove the provider definition from the global config, preserving everything else.
        var newConfig = (JsonObject)plain.ConfigCopy.DeepClone()!;
        var newProviders = RemoveProviderProperty(GetValue(newConfig, "providers"), providerId);
        SetValue(newConfig, "providers", newProviders);
        var expectedRaw = newConfig.ToJsonString(WriteOptions);
        var newInfo = new ConfigDocument { Path = globalPath, Raw = plain.Raw, Config = newConfig };
        try { await _configFile.WriteAsync(newInfo, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OpenCodeException("保存全局配置失败，未删除任何凭据。加密备份：" + backup + "；" + _secrets.Redact(ex.Message));
        }

        ConfigDocument? written = null;
        try { written = await ReadConfigAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("API 删除后回读配置失败：" + ex.Message);
            return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                "配置写入后无法回读确认；未删除任何凭据，请手动检查磁盘。加密备份：" + backup);
        }
        if (written is null || !SamePath(written.Path, globalPath) || !Ordinal(written.Raw, expectedRaw))
        {
            return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                "配置写入后磁盘内容与预期不一致（可能被并发修改）；未删除任何凭据，也不回滚用户改动。加密备份：" + backup);
        }
        var writtenDefinition = ProviderProperty(GetValue(written.Config, "providers"), providerId);
        if (writtenDefinition is not null)
        {
            var rolled = await ConfigRollbackAsync(globalPath, originalCopy, expectedRaw, ct).ConfigureAwait(false);
            var note = "配置写入后回读仍发现该供应商定义；" + (rolled ? "已回滚全局配置。" : "检测到并发修改，未强行回滚。");
            return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup, note + "加密备份：" + backup);
        }

        // Re-check the credential immediately before deleting it.
        try { await AssertApiRemovalStillValidAsync(credential, snapshot, providerId, targetId, backup, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var rolled = await ConfigRollbackAsync(globalPath, originalCopy, expectedRaw, ct).ConfigureAwait(false);
            var note = "删除凭据前复核失败：凭据状态已变化；" + (rolled ? "已回滚全局配置。" : "检测到并发修改，未强行回滚。");
            return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup, note + "加密备份：" + backup);
        }

        if (credential is not null)
        {
            try { await _api.DeleteCredentialAsync(targetId, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var rolled = await ConfigRollbackAsync(globalPath, originalCopy, expectedRaw, ct).ConfigureAwait(false);
                var note = "删除凭据失败；" + (rolled ? "已回滚全局供应商配置。" : "检测到并发修改，未强行回滚。");
                return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup, note + "加密备份：" + backup);
            }

            List<JsonNode?> after;
            try { after = await GetFreshCredentialsAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                    "删除请求已发送但无法回读确认；未做回滚（无法确认状态），请手动检查。加密备份：" + backup);
            }
            if (after.Any(c => c is not null && Ordinal(GetString(c, "id"), targetId)))
            {
                var rolled = await ConfigRollbackAsync(globalPath, originalCopy, expectedRaw, ct).ConfigureAwait(false);
                var note = "删除后回读仍发现该凭据，无法确认已删除；" + (rolled ? "已回滚全局供应商配置。" : "检测到并发修改，未强行回滚。");
                return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup, note + "加密备份：" + backup);
            }
        }

        // Confirm from disk (fresh read, not the in-memory result) that the definition is gone.
        ConfigDocument? diskAfter = null;
        try { diskAfter = await ReadConfigAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                "删除后无法回读磁盘确认定义已移除；请手动检查。加密备份：" + backup);
        }
        if (diskAfter is null || ProviderProperty(GetValue(diskAfter.Config, "providers"), providerId) is not null)
        {
            return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                "删除后磁盘配置中仍存在该供应商定义，未完成；请手动检查。加密备份：" + backup);
        }

        List<JsonNode?> finalCredentials;
        try { finalCredentials = await GetFreshCredentialsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                "删除后无法回读凭据确认；请手动检查是否残留该供应商凭据。加密备份：" + backup);
        }
        if (ProviderCredentials(finalCredentials, providerId).Count != 0)
        {
            return RemoveResult(false, AccountKind.Api, targetId, providerId, label, backup,
                "删除后仍存在该供应商的本地凭据，未完成；请手动检查。加密备份：" + backup);
        }

        _log.Info("已删除 API 供应商配置：" + providerId);
        return RemoveResult(true, AccountKind.Api, targetId, providerId, label, backup, ConfigReloadWarning);
    }

    private async Task AssertApiRemovalStillValidAsync(JsonNode? credential, Snapshot? snapshot, string providerId, string targetId, string backup, CancellationToken ct)
    {
        var latest = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        var sameProvider = ProviderCredentials(latest, providerId);
        if (credential is not null)
        {
            if (sameProvider.Count != 1)
            {
                throw new OpenCodeException("删除前复核失败：该供应商的本地凭据数量已变化（存在 " + sameProvider.Count + " 份）；未删除。加密备份：" + backup);
            }
            if (!Ordinal(GetString(sameProvider[0], "id"), targetId))
            {
                throw new OpenCodeException("删除前复核失败：目标凭据已变化；未删除。加密备份：" + backup);
            }
            if (!CredentialSnapshotEqual(sameProvider[0], snapshot))
            {
                throw new OpenCodeException("删除前复核失败：目标凭据的类型/值/激活/元数据已变化（可能已被刷新）；未删除。加密备份：" + backup);
            }
        }
        else if (sameProvider.Count > 0)
        {
            throw new OpenCodeException("该供应商出现了本地凭据（含未知类型），删除供应商配置会影响它；已停止。加密备份：" + backup);
        }
    }

    // ------------------------------------------------------------------
    // Update helpers
    // ------------------------------------------------------------------

    private async Task<bool> RemoveNewCredentialIfSafeAsync(string newId, string originalId, string providerId,
        string expectedKey, List<string> preexistingIds, CancellationToken ct)
    {
        // Only remove the new credential when we can prove it was created by this run.
        if (string.IsNullOrEmpty(newId)) { return false; }
        if (Ordinal(newId, originalId)) { return false; }
        if (preexistingIds.Any(existing => Ordinal(existing, newId))) { return false; }

        try
        {
            var now = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(originalId))
            {
                if (now.Count(c => c is not null && Ordinal(GetString(c, "id"), originalId)) != 1) { return false; }
            }

            var found = now.Where(c => c is not null && Ordinal(GetString(c, "id"), newId)).ToList();
            if (found.Count != 1) { return false; }
            var candidate = found[0]!;
            if (!Ordinal(GetString(candidate, "integrationID"), providerId)) { return false; }
            var value = GetValue(candidate, "value");
            if (!EqIgnoreCase(GetString(value, "type"), "key")) { return false; }
            if (!Ordinal(GetString(value, "key"), expectedKey)) { return false; }

            await _api.DeleteCredentialAsync(newId, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private async Task AssertDefinitionSavedAsync(string globalPath, string providerId, string expectedBaseUrl,
        List<string> modelMap, string expectedPackage, string backup, CancellationToken ct)
    {
        var current = await ReadConfigAsync(ct).ConfigureAwait(false);
        if (current is null || !SamePath(current.Path, globalPath))
        {
            throw new OpenCodeException("配置回读失败：无法读取已写入的全局配置。加密备份：" + backup);
        }
        var definition = ProviderProperty(GetValue(current.Config, "providers"), providerId);
        if (definition is null) { throw new OpenCodeException("配置回读失败：全局供应商定义未保存。加密备份：" + backup); }
        if (!Ordinal(GetString(definition, "package"), expectedPackage))
        {
            throw new OpenCodeException("配置回读失败：API 协议与预期不一致。加密备份：" + backup);
        }

        var savedUrl = GetString(GetValue(definition, "settings"), "baseURL");
        if (!Ordinal(savedUrl, expectedBaseUrl)) { throw new OpenCodeException("配置回读失败：接口地址与预期不一致。加密备份：" + backup); }

        var models = GetValue(definition, "models");
        foreach (var modelId in modelMap)
        {
            if (ProviderProperty(models, modelId) is null) { throw new OpenCodeException("配置回读失败：缺少模型 " + modelId + "。加密备份：" + backup); }
        }
        if (ProviderKeys(models).Count != modelMap.Count) { throw new OpenCodeException("配置回读失败：模型数量与预期不一致。加密备份：" + backup); }
    }

    private async Task AssertApiKeyRemovedAsync(string globalPath, string providerId, string backup, CancellationToken ct)
    {
        var current = await ReadConfigAsync(ct).ConfigureAwait(false);
        if (current is null || !SamePath(current.Path, globalPath))
        {
            throw new OpenCodeException("配置回读失败：无法读取已写入的全局配置。加密备份：" + backup);
        }
        var definition = ProviderProperty(GetValue(current.Config, "providers"), providerId);
        if (definition is null) { throw new OpenCodeException("配置回读失败：全局供应商定义未保存。加密备份：" + backup); }
        if (GetValue(GetValue(definition, "settings"), "apiKey") is not null)
        {
            throw new OpenCodeException("配置回读失败：旧 settings.apiKey 未被移除。加密备份：" + backup);
        }
    }

    // ------------------------------------------------------------------
    // Config read / rollback
    // ------------------------------------------------------------------

    private Task<ConfigDocument> ReadConfigAsync(CancellationToken ct) => _configFile.ReadAsync(ct);

    private async Task<PlainConfig> GetPlainConfigAsync(CancellationToken ct)
    {
        ConfigDocument? info = await _configFile.ReadAsync(ct).ConfigureAwait(false);
        if (info is null) { throw new OpenCodeException("无法读取全局配置，已停止。"); }
        return new PlainConfig(info.Path, info.Raw, info.Config, (JsonObject)info.Config.DeepClone()!);
    }

    private async Task<bool> ConfigRollbackAsync(string globalPath, JsonObject originalConfigCopy, string expectedRaw, CancellationToken ct)
    {
        // Roll back only when the on-disk text still exactly equals what we wrote.
        try
        {
            var current = await _configFile.ReadAsync(ct).ConfigureAwait(false);
            if (current is null) { return false; }
            if (!SamePath(current.Path, globalPath)) { return false; }
            if (!Ordinal(current.Raw, expectedRaw)) { return false; }

            var info = new ConfigDocument { Path = globalPath, Raw = expectedRaw, Config = originalConfigCopy };
            await _configFile.WriteAsync(info, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private async Task<List<JsonNode?>> GetFreshCredentialsAsync(CancellationToken ct)
    {
        // Exactly one GET /api/credential; the API wrapper already registers secrets.
        var array = await _api.GetCredentialsAsync(ct).ConfigureAwait(false);
        _secrets.Register(array);
        return array.Select(node => (JsonNode?)node).ToList();
    }

    private async Task<JsonArray> GetConfigDocumentsAsync(CancellationToken ct)
    {
        // Exactly one GET /api/config; register provider-side secrets too.
        var entries = await _api.GetConfigEntriesAsync(ct).ConfigureAwait(false);
        _secrets.Register(entries);
        foreach (var entry in entries)
        {
            _secrets.RegisterProviderSecrets(JsonMerge.Get(entry, "info"));
        }
        return entries;
    }

    private static void AssertSingleGlobalDefinition(JsonArray documents, string providerId, string globalPath)
    {
        foreach (var entry in documents)
        {
            if (entry is null) { continue; }
            if (EqIgnoreCase(GetString(entry, "type"), "directory")) { continue; }
            var providers = GetValue(GetValue(entry, "info"), "providers");
            if (providers is null) { continue; }
            var definition = ProviderProperty(providers, providerId);
            if (definition is null) { continue; }

            var path = GetString(entry, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new OpenCodeException("该供应商存在无法确认路径的配置来源；无法安全判断覆盖关系，已停止，未修改凭据或配置。");
            }
            if (!SamePath(path, globalPath)) { throw new OpenCodeException(DuplicateMessage); }
        }
    }

    // ------------------------------------------------------------------
    // Model / snapshot / comparison helpers
    // ------------------------------------------------------------------

    private static List<string> ModelMap(IReadOnlyList<string>? modelIds)
    {
        var map = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in modelIds ?? Array.Empty<string>())
        {
            if (raw is null) { continue; }
            foreach (var line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) { continue; }
                foreach (var character in trimmed)
                {
                    if (char.IsControl(character)) { throw new OpenCodeException("模型 ID 含有控制字符，已停止。"); }
                }
                if (!seen.Add(trimmed)) { throw new OpenCodeException("模型 ID 重复：" + trimmed); }
                map.Add(trimmed);
            }
        }
        if (map.Count == 0) { throw new OpenCodeException("模型列表不能为空，请至少保留一个模型。"); }
        return map;
    }

    private static JsonObject MergeModelCatalog(JsonNode? oldModels, List<string> modelMap, bool useResponses, string? baseUrl)
    {
        var result = new JsonObject();
        foreach (var modelId in modelMap)
        {
            var oldEntry = ProviderProperty(oldModels, modelId);
            if (oldEntry is not null)
            {
                var kept = (JsonObject)oldEntry.DeepClone();
                // Backfill tiers for a documented model that has none yet, so an existing
                // Responses Ark plan provider gains them on re-save. Custom variants are kept.
                ModelVariantBuilder.TryAddMissingVariants(kept, modelId, useResponses, baseUrl);
                result[modelId] = kept;
            }
            else { result[modelId] = ModelVariantBuilder.NewModelEntry(modelId, useResponses, baseUrl); }
        }
        return result;
    }

    private static bool KnownPackage(string? package)
        => OpenAiApiProtocol.IsManagedPackage(package);

    private static bool ProviderKeyIsIndirect(JsonNode? definition)
    {
        if (definition is null) { return false; }

        // 1) Authorization header (fixed or templated) overrides settings.apiKey.
        var headers = GetValue(definition, "headers");
        if (headers is not null)
        {
            var authorization = GetString(headers, "Authorization");
            if (!string.IsNullOrWhiteSpace(authorization)) { return true; }
        }

        // 2) settings.apiKey that is an {env:...}/{file:...} substitution must not be rewritten.
        var settings = GetValue(definition, "settings");
        if (settings is not null)
        {
            var apiKey = GetValue(settings, "apiKey");
            if (apiKey is JsonValue apiKeyValue
                && apiKeyValue.TryGetValue<string>(out var apiKeyText)
                && IndirectExpression.IsMatch(apiKeyText))
            {
                return true;
            }
        }

        // 3) env: non-empty string / non-empty object / non-empty array is indirect.
        var env = GetValue(definition, "env");
        if (env is not null)
        {
            if (env is JsonValue envValue)
            {
                if (envValue.TryGetValue<string>(out var envText) && !string.IsNullOrWhiteSpace(envText)) { return true; }
            }
            else if (env is JsonObject envObject)
            {
                if (envObject.Count > 0) { return true; }
            }
            else if (env is JsonArray envArray)
            {
                foreach (var item in envArray)
                {
                    if (item is null) { continue; }
                    if (item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText) && string.IsNullOrWhiteSpace(itemText)) { continue; }
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record Snapshot(string Id, string Integration, bool Active, string Type, string MethodId,
        string Key, string Access, string Refresh, string Expires, JsonNode? Metadata);

    private static Snapshot? CredentialSnapshot(JsonNode? credential)
    {
        if (credential is null) { return null; }
        var value = GetValue(credential, "value");
        return new Snapshot(
            GetString(credential, "id"),
            GetString(credential, "integrationID"),
            JsonNum.ToBool(GetValue(credential, "active")),
            GetString(value, "type"),
            GetString(value, "methodID"),
            GetString(value, "key"),
            GetString(value, "access"),
            GetString(value, "refresh"),
            NodeText(GetValue(value, "expires")),
            GetValue(value, "metadata")?.DeepClone());
    }

    private static bool CredentialSnapshotEqual(JsonNode? credential, Snapshot? snapshot)
    {
        if (credential is null || snapshot is null) { return false; }
        if (!Ordinal(GetString(credential, "id"), snapshot.Id)) { return false; }
        if (!Ordinal(GetString(credential, "integrationID"), snapshot.Integration)) { return false; }
        if (JsonNum.ToBool(GetValue(credential, "active")) != snapshot.Active) { return false; }

        var value = GetValue(credential, "value");
        if (!Ordinal(GetString(value, "type"), snapshot.Type)) { return false; }
        if (!Ordinal(GetString(value, "methodID"), snapshot.MethodId)) { return false; }
        if (!Ordinal(GetString(value, "key"), snapshot.Key)) { return false; }
        if (!Ordinal(GetString(value, "access"), snapshot.Access)) { return false; }
        if (!Ordinal(GetString(value, "refresh"), snapshot.Refresh)) { return false; }
        if (!Ordinal(NodeText(GetValue(value, "expires")), snapshot.Expires)) { return false; }
        if (!DeepEqual(GetValue(value, "metadata"), snapshot.Metadata)) { return false; }
        return true;
    }

    private static bool NewCredentialMatches(JsonNode? credential, string newId, string providerId, string newKey, bool active, JsonNode? metadata)
    {
        if (credential is null) { return false; }
        if (!Ordinal(GetString(credential, "id"), newId)) { return false; }
        if (!Ordinal(GetString(credential, "integrationID"), providerId)) { return false; }
        if (JsonNum.ToBool(GetValue(credential, "active")) != active) { return false; }

        var value = GetValue(credential, "value");
        if (!EqIgnoreCase(GetString(value, "type"), "key")) { return false; }
        if (!Ordinal(GetString(value, "key"), newKey)) { return false; }
        if (!DeepEqual(GetValue(value, "metadata"), metadata)) { return false; }
        return true;
    }

    private static List<JsonNode> ProviderCredentials(List<JsonNode?> credentials, string providerId)
    {
        var list = new List<JsonNode>();
        foreach (var credential in credentials)
        {
            if (credential is null) { continue; }
            if (!Ordinal(GetString(credential, "integrationID"), providerId)) { continue; }
            list.Add(credential);
        }
        return list;
    }

    // ------------------------------------------------------------------
    // Low-level JSON node helpers
    // ------------------------------------------------------------------

    private static JsonNode? GetValue(JsonNode? obj, string name)
    {
        if (obj is not JsonObject root || string.IsNullOrEmpty(name)) { return null; }
        if (root.TryGetPropertyValue(name, out var exact)) { return exact; }
        foreach (var kv in root)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { return kv.Value; }
        }
        return null;
    }

    private static string GetString(JsonNode? obj, string name)
    {
        var value = GetValue(obj, name);
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : string.Empty;
    }

    private static void SetValue(JsonNode? obj, string name, JsonNode? value)
    {
        if (obj is not JsonObject root) { return; }
        // PowerShell wrote into a PSCustomObject via Add-Member -Force, which matches
        // property names case-insensitively; overwrite an existing key rather than
        // create a case-variant duplicate.
        if (root.ContainsKey(name)) { root[name] = value; return; }
        string? existing = null;
        foreach (var key in root)
        {
            if (string.Equals(key.Key, name, StringComparison.OrdinalIgnoreCase)) { existing = key.Key; break; }
        }
        if (existing is not null) { root[existing] = value; return; }
        root[name] = value;
    }

    private static void RemoveValue(JsonNode? obj, string name)
    {
        if (obj is JsonObject root) { root.Remove(name); }
    }

    private static JsonNode? ProviderProperty(JsonNode? providers, string providerId)
    {
        if (providers is not JsonObject root || string.IsNullOrEmpty(providerId)) { return null; }
        return root.TryGetPropertyValue(providerId, out var value) ? value : null;
    }

    private static JsonObject RemoveProviderProperty(JsonNode? providers, string providerId)
    {
        var result = new JsonObject();
        if (providers is JsonObject root)
        {
            foreach (var kv in root)
            {
                if (!string.Equals(kv.Key, providerId, StringComparison.Ordinal)) { result[kv.Key] = kv.Value?.DeepClone(); }
            }
        }
        return result;
    }

    private static List<string> ProviderKeys(JsonNode? providers)
    {
        var keys = new List<string>();
        if (providers is JsonObject root)
        {
            foreach (var kv in root) { keys.Add(kv.Key); }
        }
        return keys;
    }

    private static string NodeText(JsonNode? node) => node?.ToJsonString() ?? string.Empty;

    private static bool DeepEqual(JsonNode? left, JsonNode? right, int depth = 0)
    {
        if (depth > 40) { return false; }
        if (left is null && right is null) { return true; }
        if (left is null || right is null) { return false; }

        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            if (leftObject.Count != rightObject.Count) { return false; }
            foreach (var kv in leftObject)
            {
                if (!rightObject.TryGetPropertyValue(kv.Key, out var rightValue)) { return false; }
                if (!DeepEqual(kv.Value, rightValue, depth + 1)) { return false; }
            }
            return true;
        }

        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            if (leftArray.Count != rightArray.Count) { return false; }
            for (var index = 0; index < leftArray.Count; index++)
            {
                if (!DeepEqual(leftArray[index], rightArray[index], depth + 1)) { return false; }
            }
            return true;
        }

        return Ordinal(left.ToJsonString(), right.ToJsonString());
    }

    // ------------------------------------------------------------------
    // Result / misc helpers
    // ------------------------------------------------------------------

    private static bool Ordinal(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static bool EqIgnoreCase(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) { return false; }
        try
        {
            return EqIgnoreCase(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'));
        }
        catch
        {
            return EqIgnoreCase(left.Trim(), right.Trim());
        }
    }

    private static bool HttpsEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) { return false; }
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) { return false; }
        return uri.Scheme == "https"
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private string SafeCodexLabel(JsonNode credential)
    {
        try
        {
            var mapped = _mapper.ConvertCredential(credential);
            if (!string.IsNullOrWhiteSpace(mapped.Label)) { return _secrets.Redact(mapped.Label); }
        }
        catch
        {
            // Fall through to the raw label.
        }
        return _secrets.Redact(GetString(credential, "label"));
    }

    private static DeleteResult RemoveResult(bool removed, AccountKind kind, string credentialId, string providerId,
        string label, string backup, string warning) => new()
        {
            Removed = removed,
            Kind = kind,
            CredentialID = credentialId,
            ProviderID = providerId,
            Label = label,
            Backup = backup,
            Warning = warning,
        };

    private static ApiSaveResult UpdateResult(string credentialId, string providerId, string backup, bool complete, string warning) => new()
    {
        Updated = true,
        Complete = complete,
        CredentialID = credentialId,
        ProviderID = providerId,
        Backup = backup,
        Warning = warning,
    };

    private sealed record PlainConfig(string Path, string? Raw, JsonObject Config, JsonObject ConfigCopy);
}
