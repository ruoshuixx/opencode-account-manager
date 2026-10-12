using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;

namespace AccountManager.Core.Accounts;

/// <summary>
/// One cached account entry. Holds every non-secret field from the source Cockpit JSON plus a
/// sealed (encrypted) blob for credentials and private account metadata, so nothing OpenCode drops
/// is lost in plaintext.
/// </summary>
public sealed class SubscriptionRecord
{
    public string AccountId { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    /// <summary>Convenience copy of the real subscription expiry (ISO-8601 text).</summary>
    public string? ActiveUntil { get; init; }

    /// <summary>
    /// True when <see cref="ActiveUntil"/> came from an explicit live lookup, so it is definitive —
    /// including when it is null ("the account has no known subscription"). A definitive null must
    /// not be shadowed by an older credential-metadata value, so it is surfaced to the mapper as an
    /// empty-string sentinel. False = a plain imported fallback.
    /// </summary>
    public bool Authoritative { get; init; }

    public string? PlanType { get; init; }

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The full Cockpit entry with secret fields removed.</summary>
    public JsonObject? Cockpit { get; init; }

    /// <summary>Encrypted (DPAPI/AES) token &amp; API-key fields, base64; never plaintext.</summary>
    public string? SealedSecrets { get; init; }

    /// <summary>True when legacy mirror secrets were ambiguous and had to be stripped safely.</summary>
    public bool CockpitMirrorInvalid { get; init; }

    public string Identity => SubscriptionStore.IdentityOf(AccountId, Email);
}

/// <summary>
/// Durable local mirror of the Cockpit account JSON stored in the OpenCode data directory
/// (next to <c>auth.json</c> / <c>opencode.db</c>) as <see cref="FileName"/>. It keeps every
/// non-secret Cockpit field and seals tokens, API keys, passwords, 2FA data, agent identity and
/// proxy addresses with <see cref="SecretProtector"/>, so information OpenCode never persists
/// (id_token claim, plan, org, quota history, …) is not lost in plaintext. The credential
/// metadata stays the primary subscription source; this is a fallback.
/// </summary>
public sealed class SubscriptionStore
{
    public const string FileName = "account-manager-state.json";

    /// <summary>Cockpit fields that hold secrets and must be sealed, not kept in plaintext.</summary>
    private static readonly string[] SecretFields =
    {
        "tokens", "openai_api_key", "access_token", "refresh_token", "id_token", "api_key", "key",
        "two_factor_secret", "account_password", "agent_identity", "egress_proxy",
    };

    private readonly Func<CancellationToken, Task<string>> _directoryProvider;
    private readonly AppLog? _log;

    public SubscriptionStore(OpenCodeCli cli, AppLog? log = null)
        : this(ct => cli.GetDataDirectoryAsync(ct), log)
    {
    }

    /// <summary>Test seam: resolves the target directory directly.</summary>
    public SubscriptionStore(Func<CancellationToken, Task<string>> directoryProvider, AppLog? log = null)
    {
        _directoryProvider = directoryProvider;
        _log = log;
    }

    public static string IdentityOf(string accountId, string email)
        => accountId + "|" + (email ?? string.Empty).ToLowerInvariant();

    /// <summary>Splits a Cockpit entry into the plaintext part and the secret part.</summary>
    public static (JsonObject Cockpit, JsonObject? Secrets) SplitSecrets(JsonObject raw)
    {
        // Normalize casing and legacy names before any field reaches the durable mirror. This
        // also rejects conflicting non-empty aliases without including their values in errors.
        var cockpit = CockpitSecretFields.Canonicalize(raw);
        JsonObject? secrets = null;
        foreach (var field in SecretFields)
        {
            var actualName = cockpit.Select(p => p.Key)
                .FirstOrDefault(name => string.Equals(name, field, StringComparison.OrdinalIgnoreCase));
            if (actualName is null || !cockpit.TryGetPropertyValue(actualName, out var node)) { continue; }
            secrets ??= new JsonObject();
            secrets[field] = node?.DeepClone();
            cockpit.Remove(actualName);
        }

        return (cockpit, secrets);
    }

    public static string? SealSecrets(JsonObject? secrets)
    {
        if (secrets is null || secrets.Count == 0) { return null; }
        var bytes = Encoding.UTF8.GetBytes(secrets.ToJsonString());
        try
        {
            return Convert.ToBase64String(SecretProtector.Protect(bytes));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Decrypts a sealed secret blob. Returns null on failure; never logs plaintext.</summary>
    public static JsonObject? OpenSecrets(string? sealedText)
    {
        if (string.IsNullOrWhiteSpace(sealedText)) { return null; }
        byte[]? plaintext = null;
        try
        {
            plaintext = SecretProtector.Unprotect(Convert.FromBase64String(sealedText));
            return JsonNode.Parse(plaintext) as JsonObject;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (plaintext is not null) { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    /// <summary>
    /// Builds an export-only in-memory mirror. Restores source metadata secrets, but deliberately
    /// never restores cached tokens or API-key aliases; ExportService overlays those from the live
    /// OpenCode credential. The returned object contains secrets and must not be logged or shown.
    /// </summary>
    internal static JsonObject GetExportMirror(SubscriptionRecord record)
    {
        if (record.CockpitMirrorInvalid)
        {
            throw new OpenCodeException("本地 Cockpit 镜像曾包含冲突的敏感字段；请重新导入有效备份后再导出。");
        }
        if (record.Cockpit is null) { throw new OpenCodeException("本地 Cockpit 镜像缺少账号对象，请重新导入有效备份。"); }

        var (cockpit, embeddedSecrets) = SplitSecrets(record.Cockpit);
        JsonObject? restored = null;
        if (!string.IsNullOrWhiteSpace(record.SealedSecrets))
        {
            var opened = OpenSecrets(record.SealedSecrets);
            if (opened is null)
            {
                throw new OpenCodeException("本地 Cockpit 镜像的密封字段无法读取；请重新导入有效备份后再导出。");
            }

            restored = CockpitSecretFields.Canonicalize(opened);
        }

        if (embeddedSecrets is not null)
        {
            restored ??= new JsonObject();
            foreach (var (name, value) in embeddedSecrets)
            {
                if (!restored.ContainsKey(name)) { restored[name] = value?.DeepClone(); }
            }
        }

        if (restored is not null)
        {
            restored = CockpitSecretFields.Canonicalize(restored);
            foreach (var field in CockpitSecretFields.ExportableAdditionalSecretFields)
            {
                if (restored.TryGetPropertyValue(field, out var value)) { cockpit[field] = value?.DeepClone(); }
            }
        }

        return cockpit;
    }

    public async Task<string> GetPathAsync(CancellationToken cancellationToken = default)
    {
        var dir = await _directoryProvider(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(dir)) { throw new OpenCodeException("OpenCode 数据目录为空，无法定位订阅缓存。"); }
        return Path.Combine(dir, FileName);
    }

    public async Task<Dictionary<string, SubscriptionRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        string path;
        try { path = await GetPathAsync(cancellationToken).ConfigureAwait(false); }
        catch (OpenCodeException) { return new(StringComparer.Ordinal); }

        if (!File.Exists(path)) { return new(StringComparer.Ordinal); }

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Warn("读取账户缓存失败，已忽略：" + ex.Message);
            return new(StringComparer.Ordinal);
        }

        return Parse(raw, _log);
    }

    /// <summary>
    /// Identity → raw subscription expiry, for use as a display fallback. A live lookup that
    /// authoritatively found no expiry is surfaced as an empty string, so the mapper can suppress a
    /// stale credential-metadata date instead of showing a value that no longer applies.
    /// </summary>
    public async Task<Dictionary<string, string>> LoadActiveUntilAsync(CancellationToken cancellationToken = default)
    {
        var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (identity, record) in all)
        {
            if (record.Authoritative) { result[identity] = record.ActiveUntil ?? string.Empty; }
            else if (!string.IsNullOrWhiteSpace(record.ActiveUntil)) { result[identity] = record.ActiveUntil!; }
        }

        return result;
    }

    /// <summary>Merges the given records into the cache; last write wins per identity.</summary>
    public async Task SaveAsync(IEnumerable<SubscriptionRecord> records, CancellationToken cancellationToken = default)
    {
        var updates = records.Where(r => !string.IsNullOrWhiteSpace(r.Identity)).ToList();
        if (updates.Count == 0) { return; }

        var existing = await LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in updates)
        {
            var record = item;
            // Keep previously sealed secrets when the caller only refreshes metadata.
            if (record.SealedSecrets is null && existing.TryGetValue(record.Identity, out var prior))
            {
                record = CopyWithSecrets(record, prior.SealedSecrets, prior.CockpitMirrorInvalid);
            }

            existing[record.Identity] = record;
        }

        string path;
        try { path = await GetPathAsync(cancellationToken).ConfigureAwait(false); }
        catch (OpenCodeException ex)
        {
            _log?.Warn("写入账户缓存失败（不影响账号）：" + ex.Message);
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, Serialize(existing.Values), cancellationToken).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            _log?.Info("已更新账户缓存：" + path);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Warn("写入账户缓存失败（不影响账号）：" + ex.Message);
        }
    }

    /// <summary>
    /// Updates only the cached subscription expiry for one account (used after a live lookup),
    /// preserving every other field and the sealed secrets. Creates a minimal record when none
    /// exists yet.
    /// </summary>
    public async Task UpdateActiveUntilAsync(string accountId, string email, DateTimeOffset activeUntil, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) { return; }

        var identity = IdentityOf(accountId, email);
        var text = activeUntil.ToUniversalTime().ToString("O");
        var existing = await LoadAsync(cancellationToken).ConfigureAwait(false);

        SubscriptionRecord record;
        if (existing.TryGetValue(identity, out var prior))
        {
            JsonObject? cockpit = null;
            if (prior.Cockpit is not null)
            {
                cockpit = (JsonObject)prior.Cockpit.DeepClone();
                cockpit["subscription_active_until"] = text;
            }

            record = new SubscriptionRecord
            {
                AccountId = prior.AccountId,
                Email = prior.Email,
                ActiveUntil = text,
                PlanType = prior.PlanType,
                Source = prior.Source,
                UpdatedAt = DateTimeOffset.UtcNow,
                Authoritative = true,
                Cockpit = cockpit,
                CockpitMirrorInvalid = prior.CockpitMirrorInvalid,
            };
        }
        else
        {
            record = new SubscriptionRecord
            {
                AccountId = accountId,
                Email = email,
                ActiveUntil = text,
                Source = "live",
                UpdatedAt = DateTimeOffset.UtcNow,
                Authoritative = true,
            };
        }

        await SaveAsync(new[] { record }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that a live lookup ran and found <em>no</em> subscription expiry. The empty marker is
    /// authoritative: the mapper must not fall back to an older credential-metadata date, so the row
    /// shows "—" instead of a value that no longer applies. Everything else is preserved.
    /// </summary>
    public async Task ClearActiveUntilAsync(string accountId, string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) { return; }

        var identity = IdentityOf(accountId, email);
        var existing = await LoadAsync(cancellationToken).ConfigureAwait(false);

        SubscriptionRecord record;
        if (existing.TryGetValue(identity, out var prior))
        {
            JsonObject? cockpit = null;
            if (prior.Cockpit is not null)
            {
                cockpit = (JsonObject)prior.Cockpit.DeepClone();
                cockpit["subscription_active_until"] = null;
            }

            record = new SubscriptionRecord
            {
                AccountId = prior.AccountId,
                Email = prior.Email,
                ActiveUntil = null,
                PlanType = prior.PlanType,
                Source = prior.Source,
                UpdatedAt = DateTimeOffset.UtcNow,
                Authoritative = true,
                Cockpit = cockpit,
                CockpitMirrorInvalid = prior.CockpitMirrorInvalid,
            };
        }
        else
        {
            record = new SubscriptionRecord
            {
                AccountId = accountId,
                Email = email,
                ActiveUntil = null,
                Source = "live",
                UpdatedAt = DateTimeOffset.UtcNow,
                Authoritative = true,
            };
        }

        await SaveAsync(new[] { record }, cancellationToken).ConfigureAwait(false);
    }

    private static SubscriptionRecord CopyWithSecrets(SubscriptionRecord record, string? sealedSecrets, bool priorInvalid = false) => new()
    {
        AccountId = record.AccountId,
        Email = record.Email,
        ActiveUntil = record.ActiveUntil,
        Authoritative = record.Authoritative,
        PlanType = record.PlanType,
        Source = record.Source,
        UpdatedAt = record.UpdatedAt,
        Cockpit = record.Cockpit,
        SealedSecrets = sealedSecrets,
        CockpitMirrorInvalid = record.CockpitMirrorInvalid || priorInvalid,
    };

    public static Dictionary<string, SubscriptionRecord> Parse(string raw, AppLog? log = null)
    {
        var result = new Dictionary<string, SubscriptionRecord>(StringComparer.Ordinal);
        JsonNode? root;
        try { root = JsonNode.Parse(raw); }
        catch (JsonException)
        {
            log?.Warn("账户缓存不是合法 JSON，已忽略。");
            return result;
        }

        if (JsonMerge.Get(root, "accounts") is not JsonObject accounts) { return result; }
        foreach (var (identity, node) in accounts)
        {
            if (node is not JsonObject o) { continue; }
            var accountId = JsonMerge.GetString(o, "accountID") ?? string.Empty;
            var email = JsonMerge.GetString(o, "email") ?? string.Empty;
            var cockpitSource = JsonMerge.Get(o, "cockpit") as JsonObject;
            JsonObject? cockpit = cockpitSource is null ? null : (JsonObject)cockpitSource.DeepClone();
            var sealedSecrets = JsonMerge.GetString(o, "secrets");
            var cockpitMirrorInvalid = JsonNum.ToBool(JsonMerge.Get(o, "cockpitMirrorInvalid"));
            if (cockpit is not null)
            {
                try
                {
                    var (safeCockpit, legacySecrets) = SplitSecrets(cockpit);
                    cockpit = safeCockpit;
                    if (legacySecrets is not null)
                    {
                        var prior = OpenSecrets(sealedSecrets);
                        if (!string.IsNullOrWhiteSpace(sealedSecrets) && prior is null)
                        {
                            throw new OpenCodeException("旧镜像密封字段无法读取，不能用部分明文数据替换。");
                        }
                        var combined = prior is not null ? CockpitSecretFields.Canonicalize(prior) : new JsonObject();
                        foreach (var (name, value) in legacySecrets)
                        {
                            // Existing sealed material wins if a broken old cache has duplicate
                            // data. Export overlays live credentials and never uses these values.
                            if (!combined.ContainsKey(name)) { combined[name] = value?.DeepClone(); }
                        }

                        sealedSecrets = SealSecrets(CockpitSecretFields.Canonicalize(combined));
                    }
                }
                catch
                {
                    // A malformed legacy cache is untrusted. Strip every recognized secret
                    // alias rather than retaining it in plaintext; keep an older sealed blob if
                    // one existed. Never include the values in diagnostics.
                    cockpit = (JsonObject)cockpit.DeepClone();
                    CockpitSecretFields.RemoveAll(cockpit);
                    cockpitMirrorInvalid = true;
                }
            }
            var activeUntil = JsonMerge.GetString(o, "subscriptionActiveUntil")
                ?? JsonMerge.GetString(cockpit, "subscription_active_until");
            if (string.IsNullOrWhiteSpace(accountId)) { continue; }

            result[identity] = new SubscriptionRecord
            {
                AccountId = accountId,
                Email = email,
                ActiveUntil = activeUntil,
                Authoritative = JsonNum.ToBool(JsonMerge.Get(o, "subscriptionAuthoritative")),
                PlanType = JsonMerge.GetString(o, "planType") ?? JsonMerge.GetString(cockpit, "plan_type"),
                Source = JsonMerge.GetString(o, "source") ?? string.Empty,
                Cockpit = cockpit,
                SealedSecrets = sealedSecrets,
                CockpitMirrorInvalid = cockpitMirrorInvalid,
            };
        }

        return result;
    }

    public static string Serialize(IEnumerable<SubscriptionRecord> records)
    {
        var accounts = new JsonObject();
        foreach (var record in records.OrderBy(r => r.Identity, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(record.AccountId)) { continue; }
            var safeRecord = SanitizeRecordForStorage(record);
            var entry = new JsonObject
            {
                ["accountID"] = safeRecord.AccountId,
                ["email"] = safeRecord.Email,
                ["planType"] = safeRecord.PlanType,
                ["subscriptionActiveUntil"] = safeRecord.ActiveUntil,
                ["source"] = safeRecord.Source,
                ["updatedAt"] = safeRecord.UpdatedAt.ToUniversalTime().ToString("O"),
            };
            if (safeRecord.Authoritative) { entry["subscriptionAuthoritative"] = true; }
            if (safeRecord.Cockpit is not null) { entry["cockpit"] = safeRecord.Cockpit.DeepClone(); }
            if (!string.IsNullOrWhiteSpace(safeRecord.SealedSecrets)) { entry["secrets"] = safeRecord.SealedSecrets; }
            if (safeRecord.CockpitMirrorInvalid) { entry["cockpitMirrorInvalid"] = true; }
            accounts[safeRecord.Identity] = entry;
        }

        var root = new JsonObject
        {
            ["version"] = 2,
            ["updatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["accounts"] = accounts,
        };
        return root.ToJsonString(CoreJson.Indented);
    }

    private static SubscriptionRecord SanitizeRecordForStorage(SubscriptionRecord record)
    {
        if (record.Cockpit is null) { return record; }
        var (cockpit, extractedSecrets) = SplitSecrets(record.Cockpit);
        if (extractedSecrets is null)
        {
            return CopyWithCockpitAndSecrets(record, cockpit, record.SealedSecrets);
        }

        var prior = OpenSecrets(record.SealedSecrets);
        if (!string.IsNullOrWhiteSpace(record.SealedSecrets) && prior is null)
        {
            throw new OpenCodeException("镜像密封字段无法读取，未用部分明文数据覆盖原缓存。");
        }
        var combined = prior is not null ? CockpitSecretFields.Canonicalize(prior) : new JsonObject();
        foreach (var (name, value) in extractedSecrets)
        {
            // Existing sealed values win on collision; normal import always provides the current
            // values directly in SealedSecrets, while this path is a defense against unsafe callers.
            if (!combined.ContainsKey(name)) { combined[name] = value?.DeepClone(); }
        }

        return CopyWithCockpitAndSecrets(record, cockpit, SealSecrets(CockpitSecretFields.Canonicalize(combined)));
    }

    private static SubscriptionRecord CopyWithCockpitAndSecrets(SubscriptionRecord record, JsonObject cockpit, string? sealedSecrets) => new()
    {
        AccountId = record.AccountId,
        Email = record.Email,
        ActiveUntil = record.ActiveUntil,
        Authoritative = record.Authoritative,
        PlanType = record.PlanType,
        Source = record.Source,
        UpdatedAt = record.UpdatedAt,
        Cockpit = cockpit,
        SealedSecrets = sealedSecrets,
        CockpitMirrorInvalid = record.CockpitMirrorInvalid,
    };
}
