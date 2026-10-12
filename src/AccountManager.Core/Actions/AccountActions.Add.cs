using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Import;
using AccountManager.Core.Json;
using AccountManager.Core.Models;
using AccountManager.Core.OpenCode;

namespace AccountManager.Core.Actions;

/// <summary>A secret-free public preview with an internal authorization payload. Never log the payload.</summary>
public sealed class CodexAddPreview
{
    public string Label { get; internal init; } = string.Empty;
    public string Workspace { get; internal init; } = string.Empty;
    public bool HasExistingAccount => OriginalCredential is not null;
    internal ImportService.ImportRow Row { get; init; } = new();
    internal JsonNode? OriginalCredential { get; init; }
    internal string NewCredentialId { get; } = "cred_" + Guid.NewGuid().ToString("N");
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal AccountAddResult? Result { get; set; }
}

public sealed partial class AccountActions
{
    private readonly SemaphoreSlim _additionGate = new(1, 1);

    private static string ValidateProviderName(string name) => ApiProviderValidation.Name(name);

    public static string NewApiProviderId() => "api-" + Guid.NewGuid().ToString("N");

    private static JsonObject NewAccountMetadata() => new()
    {
        ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["last_used"] = 0L, // No model request has been made for a newly added account.
    };

    /// <summary>Add a unique provider and key; never overwrite an existing provider or credential.</summary>
    public async Task<AccountAddResult> CreateManagedApiProviderAsync(string providerId, string displayName,
        string baseUrl, IReadOnlyList<string> modelIds, string key, bool useResponses = false, CancellationToken ct = default)
    {
        await _additionGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await CreateApiProviderCoreAsync(providerId, displayName, baseUrl, modelIds, key, useResponses, ct).ConfigureAwait(false); }
        finally { _additionGate.Release(); }
    }

    private async Task<AccountAddResult> CreateApiProviderCoreAsync(string providerId, string displayName,
        string baseUrl, IReadOnlyList<string> modelIds, string key, bool useResponses, CancellationToken ct)
    {
        _secrets.RegisterValue(key);
        var name = ApiProviderValidation.Name(displayName);
        var url = ApiProviderValidation.Endpoint(baseUrl);
        var apiKey = ApiProviderValidation.Key(key, required: true);
        _secrets.RegisterValue(apiKey);
        var models = ModelMap(modelIds);
        if (providerId is null || !System.Text.RegularExpressions.Regex.IsMatch(providerId, "^api-[a-f0-9]{32}$"))
        {
            throw new OpenCodeException("新供应商 ID 无效；请重新打开添加窗口。");
        }

        var plain = await GetPlainConfigAsync(ct).ConfigureAwait(false);
        var credentials = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        await AssertNewProviderAsync(providerId, plain.ConfigCopy, credentials, ct).ConfigureAwait(false);
        var backup = _backups.Save(credentials, plain.Path, plain.Raw);
        _log.Info("已生成加密备份（添加 API）：" + backup);

        var latest = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        await AssertNewProviderAsync(providerId, plain.ConfigCopy, latest, ct).ConfigureAwait(false);
        var configBefore = await ReadConfigAsync(ct).ConfigureAwait(false);
        if (!SamePath(configBefore.Path, plain.Path) || !Ordinal(configBefore.Raw, plain.Raw))
        {
            throw new OpenCodeException("全局配置已被并发修改，未添加 API；请重新读取。");
        }
        ct.ThrowIfCancellationRequested();

        var newConfig = (JsonObject)plain.ConfigCopy.DeepClone();
        if (newConfig["providers"] is null) { newConfig["providers"] = new JsonObject(); }
        if (newConfig["providers"] is not JsonObject providers) { throw new OpenCodeException("providers 配置不是对象，未添加。"); }
        var catalog = new JsonObject();
        foreach (var model in models) { catalog[model] = ModelVariantBuilder.NewModelEntry(model, useResponses, url); }
        var settings = new JsonObject { ["baseURL"] = url };
        if (useResponses) { settings["transport"] = "http"; }
        providers[providerId] = new JsonObject
        {
            ["name"] = name,
            ["package"] = OpenAiApiProtocol.PackageFor(useResponses),
            ["settings"] = settings,
            ["models"] = catalog,
        };
        var expectedRaw = newConfig.ToJsonString(WriteOptions);
        var newId = "cred_" + Guid.NewGuid().ToString("N");
        var value = new JsonObject { ["type"] = "key", ["key"] = apiKey, ["metadata"] = NewAccountMetadata() };
        var existingIds = latest.Select(c => GetString(c, "id")).ToHashSet(StringComparer.Ordinal);
        var configAttempted = false;

        // Once a mutation starts, finish verification/compensation even when the dialog is cancelled.
        // The CLI itself has a bounded timeout; a cancelled caller must not abandon a half-write.
        try
        {
            await CreateExactCredentialAsync(newId, providerId, name, value, true, CancellationToken.None).ConfigureAwait(false);
            var verify = await GetFreshCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
            if (!ExactNewCredential(verify, newId, providerId, value, true))
            {
                throw new OpenCodeException("新 API 凭据回读未通过精确校验。");
            }
            await AssertNewProviderDefinitionAsync(providerId, CancellationToken.None).ConfigureAwait(false);
            configAttempted = true;
            await _configFile.WriteAsync(new ConfigDocument { Path = plain.Path, Raw = plain.Raw, Config = newConfig }, CancellationToken.None).ConfigureAwait(false);
            var disk = await ReadConfigAsync(CancellationToken.None).ConfigureAwait(false);
            if (!SamePath(disk.Path, plain.Path) || !Ordinal(disk.Raw, expectedRaw))
            {
                return Added(AccountKind.Api, newId, providerId, name, backup, false,
                    "凭据已保存，但配置回读与预期不一致；未覆盖并发改动，请先读取并检查，不要重复添加。" + ConfigReloadWarning);
            }
            var final = await GetFreshCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
            if (!ExactNewCredential(final, newId, providerId, value, true))
            {
                return Added(AccountKind.Api, newId, providerId, name, backup, false, "配置已保存，但凭据最终状态未确认；请先读取并检查。" + ConfigReloadWarning);
            }
            _log.Info("已添加 API 供应商：" + providerId);
            return Added(AccountKind.Api, newId, providerId, name, backup, true, ConfigReloadWarning);
        }
        catch (Exception ex)
        {
            if (ex is CredentialCreationUncertainException)
            {
                return Added(AccountKind.Api, newId, providerId, name, backup, false,
                    "创建请求无法按本次凭据 ID 确认；未修改配置、未删除任何不明凭据，也不自动重试，请先读取并检查。加密备份：" + backup, saved: false);
            }
            // Only compensate our exact config and our exact credential. An ambiguous response
            // never authorizes removing someone else's data or overwriting concurrent edits.
            var configSafe = !configAttempted;
            if (configAttempted)
            {
                try
                {
                    var disk = await ReadConfigAsync(CancellationToken.None).ConfigureAwait(false);
                    if (SamePath(disk.Path, plain.Path) && Ordinal(disk.Raw, plain.Raw)) { configSafe = true; }
                    else if (SamePath(disk.Path, plain.Path) && Ordinal(disk.Raw, expectedRaw))
                    {
                        configSafe = await ConfigRollbackAsync(plain.Path, plain.ConfigCopy, expectedRaw, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch { /* An unreadable config cannot safely be rolled back. */ }
            }
            var cleanup = configSafe && await RemoveAddedCredentialIfSafeAsync(newId, providerId, name, value, existingIds).ConfigureAwait(false);
            if (cleanup)
            {
                throw new OpenCodeException("添加 API 失败，已安全清理本次写入，保留原供应商数据。加密备份：" + backup + "；" + _secrets.Redact(ex.Message));
            }
            return Added(AccountKind.Api, newId, providerId, name, backup, false,
                "添加结果无法完整确认，未强行回滚或重复写入；请先读取并检查。加密备份：" + backup + "；" + _secrets.Redact(ex.Message));
        }
    }

    public async Task<CodexAddPreview> PreviewCodexAuthorizationAsync(JsonObject account, CancellationToken ct = default)
    {
        _secrets.Register(account);
        var integration = await _api.GetIntegrationAsync("openai", ct).ConfigureAwait(false);
        var methods = GetValue(GetValue(integration, "data"), "methods") as JsonArray;
        if (methods is null || !methods.Any(m => GetString(m, "id") == "chatgpt-browser" && GetString(m, "type") == "oauth"))
        {
            throw new OpenCodeException("此 OpenCode 没有 chatgpt-browser OAuth 方法，请使用兼容的 V2 版本。");
        }
        var row = ImportService.ConvertAccount(account, "chatgpt-browser");
        var identity = CredentialHelpers.GetIdentityInfo(row.Value);
        if (!CredentialHelpers.IsEmail(identity.Email) || string.IsNullOrWhiteSpace(identity.AccountID))
        {
            throw new OpenCodeException("授权结果缺少可确认的邮箱或工作区，未保存账号。");
        }
        var idToken = GetString(GetValue(account, "tokens"), "id_token");
        if (!string.IsNullOrWhiteSpace(idToken)) { ((JsonObject)row.Value["metadata"]!)["id_token"] = idToken; }
        if (!string.IsNullOrWhiteSpace(row.Plan)) { ((JsonObject)row.Value["metadata"]!)["planType"] = row.Plan; }
        var current = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
        var matches = MatchingCodex(current, row.Identity);
        if (matches.Count > 1) { throw new OpenCodeException("该身份已对应多份凭据，请先在 OpenCode 中整理重复账号，未保存。"); }
        return new CodexAddPreview
        {
            Label = _secrets.Redact(row.Label),
            Workspace = _secrets.Redact(row.Workspace),
            Row = row,
            OriginalCredential = matches.SingleOrDefault()?.DeepClone(),
        };
    }

    /// <summary>Save explicitly confirmed authorization, preserving the current integration account.</summary>
    public async Task<AccountAddResult> SaveCodexAuthorizationAsync(CodexAddPreview preview, bool replaceExisting, CancellationToken ct = default)
    {
        await _additionGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await SaveCodexCoreAsync(preview, replaceExisting, ct).ConfigureAwait(false); }
        finally { _additionGate.Release(); }
    }

    private async Task<AccountAddResult> SaveCodexCoreAsync(CodexAddPreview preview, bool replaceExisting, CancellationToken ct)
    {
        await preview.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (preview.Result is not null) { return preview.Result; }
            if (preview.HasExistingAccount && !replaceExisting) { throw new OpenCodeException("该账号已存在，须确认更新后才能保存。"); }
            var row = preview.Row;
            var old = preview.OriginalCredential;
            var oldSnapshot = CredentialSnapshot(old);
            var before = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
            var matches = MatchingCodex(before, row.Identity);
            if ((old is null && matches.Count != 0)
                || (old is not null && (matches.Count != 1 || !CredentialSnapshotEqual(matches[0], oldSnapshot))))
            {
                throw new OpenCodeException("账号状态在确认期间已变化，未保存；请重新授权并核对。");
            }
            var backup = _backups.Save(before, null, null);
            _log.Info("已生成加密备份（添加 Codex）：" + backup);
            var latest = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
            if (!SameIntegrationSnapshot(before, latest, "openai"))
            {
                throw new OpenCodeException("保存前账号状态已被并发修改，未保存；请重新核对。");
            }
            ct.ThrowIfCancellationRequested();
            var newId = preview.NewCredentialId;
            if (latest.Any(c => Ordinal(GetString(c, "id"), newId))) { throw new OpenCodeException("新凭据 ID 已存在，未重复写入；请先读取账号。"); }
            var activate = old is not null && JsonNum.ToBool(GetValue(old, "active"));
            var firstAccount = !latest.Any(c => Ordinal(GetString(c, "integrationID"), "openai") && JsonNum.ToBool(GetValue(c, "active")));
            var expectedActive = activate || firstAccount;
            var warning = firstAccount ? "这是首个 OpenAI 账号，OpenCode 会自动将其设为当前账号。" : string.Empty;
            // Capture the real creation time at save, not at OAuth preview/export. For an update,
            // preserve known account history rather than replacing it with a new credential's date.
            var metadata = (JsonObject)row.Value["metadata"]!;
            var oldMetadata = GetValue(GetValue(old, "value"), "metadata");
            foreach (var field in new[] { "created_at", "last_used" })
            {
                metadata[field] = GetValue(oldMetadata, field)?.DeepClone()
                    ?? (field == "created_at" && old is null ? JsonValue.Create(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : JsonValue.Create(0L));
            }
            try
            {
                await CreateExactCredentialAsync(newId, "openai", row.Label + " [" + row.Workspace + "]", row.Value, activate, CancellationToken.None).ConfigureAwait(false);
                var verify = await GetFreshCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
                if (!ExactNewCredential(verify, newId, "openai", row.Value, expectedActive)
                    || !PreservedActiveAccounts(latest, verify, old, newId, expectedActive))
                {
                    preview.Result = Added(AccountKind.Codex, newId, "openai", preview.Label, backup, false,
                        "新凭据或当前账号状态未通过回读校验；保留原凭据，不自动切号或重试，请先读取并检查。");
                    return preview.Result;
                }
                if (old is not null)
                {
                    var expectedOld = old.DeepClone();
                    if (activate) { ((JsonObject)expectedOld)["active"] = false; }
                    var found = verify.Where(c => Ordinal(GetString(c, "id"), GetString(old, "id"))).ToList();
                    if (found.Count != 1 || !CredentialSnapshotEqual(found[0], CredentialSnapshot(expectedOld)))
                    {
                        preview.Result = Added(AccountKind.Codex, newId, "openai", preview.Label, backup, false,
                            "新凭据已保存，但原凭据被并发修改，未删除它；请检查重复项。");
                        return preview.Result;
                    }
                    await _api.DeleteCredentialAsync(GetString(old, "id"), CancellationToken.None).ConfigureAwait(false);
                }
                var final = await GetFreshCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
                var complete = ExactNewCredential(final, newId, "openai", row.Value, expectedActive)
                    && PreservedActiveAccounts(latest, final, old, newId, expectedActive)
                    && (old is null || !final.Any(c => Ordinal(GetString(c, "id"), GetString(old, "id"))));
                preview.Result = Added(AccountKind.Codex, newId, "openai", preview.Label, backup, complete,
                    complete ? warning : "保存请求已完成，但最终回读未通过；请检查凭据和当前账号状态，不要重复添加。");
                _log.Info(complete ? "已保存 Codex 授权账号。" : "Codex 授权保存需手动检查。");
                return preview.Result;
            }
            catch (Exception ex)
            {
                preview.Result = Added(AccountKind.Codex, newId, "openai", preview.Label, backup, false,
                    "保存结果未完整确认；旧凭据是否残留需检查，不自动重试，请先读取并核对。加密备份：" + backup + "；" + _secrets.Redact(ex.Message),
                    saved: ex is not CredentialCreationUncertainException);
                return preview.Result;
            }
        }
        finally { preview.Gate.Release(); }
    }

    private async Task CreateExactCredentialAsync(string id, string provider, string label, JsonObject value, bool activate, CancellationToken ct)
    {
        try
        {
            var returnedId = await _api.CreateCredentialAsync(provider, label, value, activate, ct, id).ConfigureAwait(false);
            if (!Ordinal(returnedId, id)) { throw new CredentialCreationUncertainException(); }
        }
        catch
        {
            // A lost POST response is not proof of failure. Reconcile by the ID chosen before
            // sending, without retrying POST or deleting a credential with an unrelated ID.
            try
            {
                var current = await GetFreshCredentialsAsync(ct).ConfigureAwait(false);
                var found = current.Where(c => Ordinal(GetString(c, "id"), id)).ToList();
                if (found.Count == 1 && Ordinal(GetString(found[0], "integrationID"), provider)
                    && DeepEqual(GetValue(found[0], "value"), value)) { return; }
            }
            catch { /* Failure to reconcile is not proof that POST did not reach the server. */ }
            throw new CredentialCreationUncertainException();
        }
    }

    private async Task AssertNewProviderAsync(string id, JsonObject config, List<JsonNode?> credentials, CancellationToken ct)
    {
        if (GetValue(config, "providers") is JsonObject providers && providers.ContainsKey(id))
        {
            throw new OpenCodeException("供应商 ID 已存在，未覆盖；请重新打开添加窗口。");
        }
        if (ProviderCredentials(credentials, id).Count != 0) { throw new OpenCodeException("供应商已有凭据，未覆盖；请重新读取。"); }
        await AssertNewProviderDefinitionAsync(id, ct).ConfigureAwait(false);
    }

    private async Task AssertNewProviderDefinitionAsync(string id, CancellationToken ct)
    {
        var documents = await GetConfigDocumentsAsync(ct).ConfigureAwait(false);
        if (documents.Any(d => GetValue(GetValue(d, "info"), "providers") is JsonObject p && p.ContainsKey(id)))
        {
            throw new OpenCodeException("其它配置文档已定义此供应商 ID，未覆盖任何配置。");
        }
    }

    private async Task<bool> RemoveAddedCredentialIfSafeAsync(string id, string provider, string label, JsonObject value, HashSet<string> existingIds)
    {
        if (existingIds.Contains(id)) { return false; }
        try
        {
            var now = await GetFreshCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
            var found = now.Where(c => Ordinal(GetString(c, "id"), id)).ToList();
            if (found.Count == 0) { return true; }
            if (found.Count != 1 || !Ordinal(GetString(found[0], "integrationID"), provider)
                || !Ordinal(GetString(found[0], "label"), label) || !JsonNum.ToBool(GetValue(found[0], "active"))
                || !DeepEqual(GetValue(found[0], "value"), value)) { return false; }
            await _api.DeleteCredentialAsync(id, CancellationToken.None).ConfigureAwait(false);
            return !(await GetFreshCredentialsAsync(CancellationToken.None).ConfigureAwait(false)).Any(c => Ordinal(GetString(c, "id"), id));
        }
        catch { return false; }
    }

    private static bool ExactNewCredential(List<JsonNode?> credentials, string id, string provider, JsonObject value, bool active)
    {
        var found = credentials.Where(c => Ordinal(GetString(c, "id"), id)).ToList();
        return found.Count == 1 && Ordinal(GetString(found[0], "integrationID"), provider)
            && JsonNum.ToBool(GetValue(found[0], "active")) == active && DeepEqual(GetValue(found[0], "value"), value);
    }

    private static List<JsonNode> MatchingCodex(List<JsonNode?> credentials, string identity)
        => credentials.Where(c => c is not null && Ordinal(GetString(c, "integrationID"), "openai")
            && GetString(GetValue(c, "value"), "type") == "oauth"
            && CredentialHelpers.IsCodexOAuthMethod(GetString(GetValue(c, "value"), "methodID"))
            && Ordinal(CredentialHelpers.GetIdentityInfo(c).Identity, identity)).Select(c => c!).ToList();

    private static bool SameIntegrationSnapshot(List<JsonNode?> before, List<JsonNode?> after, string provider)
    {
        var left = ProviderCredentials(before, provider);
        var right = ProviderCredentials(after, provider);
        return left.Count == right.Count && left.All(c => right.Count(x => CredentialSnapshotEqual(x, CredentialSnapshot(c))) == 1);
    }

    private static bool PreservedActiveAccounts(List<JsonNode?> before, List<JsonNode?> after, JsonNode? old, string newId, bool expectedActive)
    {
        var expected = before.Where(c => Ordinal(GetString(c, "integrationID"), "openai") && JsonNum.ToBool(GetValue(c, "active")))
            .Select(c => GetString(c, "id")).ToHashSet(StringComparer.Ordinal);
        if (old is not null) { expected.Remove(GetString(old, "id")); }
        if (expectedActive) { expected.Add(newId); }
        var actual = after.Where(c => Ordinal(GetString(c, "integrationID"), "openai") && JsonNum.ToBool(GetValue(c, "active")))
            .Select(c => GetString(c, "id")).ToHashSet(StringComparer.Ordinal);
        return expected.SetEquals(actual);
    }

    private AccountAddResult Added(AccountKind kind, string id, string provider, string label, string backup, bool complete, string warning, bool saved = true)
        => new() { Saved = saved, Complete = complete, Kind = kind, CredentialID = id, ProviderID = provider, Label = _secrets.Redact(label), Backup = backup, Warning = _secrets.Redact(warning) };

    private sealed class CredentialCreationUncertainException : Exception
    {
        public CredentialCreationUncertainException() : base("无法按本次凭据 ID 确认创建结果，请先读取并检查。") { }
    }
}
