using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Json;
using AccountManager.Core.Models;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;

namespace AccountManager.Core.Quota;

/// <summary>
/// Selects the correct token for one account row and queries its quota, exactly matching
/// the PowerShell <c>Get-AccountQuotaResult</c> / <c>Invoke-AccountQuotaBatch</c> contract.
/// The token must come from the same identity (accountID) so a workspace/user is never
/// crossed.
/// </summary>
public sealed class AccountQuotaSelector
{
    private readonly OpenCodeApi _api;
    private readonly QuotaService _quota;
    private readonly SecretRegistry _secrets;
    private readonly Logging.AppLog? _log;
    private readonly SubscriptionRefresher? _subscriptions;

    public AccountQuotaSelector(OpenCodeApi api, QuotaService quota, Security.SecretRegistry secrets, Logging.AppLog? log = null, SubscriptionRefresher? subscriptions = null)
    {
        _api = api;
        _quota = quota;
        _secrets = secrets;
        _log = log;
        _subscriptions = subscriptions;
    }

    public static string RowKey(AccountRow? row)
    {
        if (row is null) { return string.Empty; }
        return !string.IsNullOrWhiteSpace(row.Key) ? row.Key : row.Provider + "|" + row.Identity;
    }

    public async Task<AccountQuotaResult> GetResultAsync(AccountRow? row, IReadOnlyList<JsonNode>? credentials, CancellationToken cancellationToken = default, bool forceSubscription = false)
    {
        var key = RowKey(row);
        var checkedAt = DateTimeOffset.Now;
        if (row is null || row.Kind != AccountKind.Codex)
        {
            return new AccountQuotaResult { Key = key, State = QuotaResultState.Unsupported, Message = "仅支持 OpenAI Codex OAuth 账号；其他类型不查询。", CheckedAt = checkedAt };
        }

        var token = ResolveToken(row, credentials, checkedAt);
        if (token.Error is not null) { return token.Error; }

        QuotaUsage usage;
        try
        {
            usage = await _quota.GetQuotaAsync(token.Access, token.AccountId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var safe = _secrets.Redact(ex.Message);
            if (string.IsNullOrWhiteSpace(safe)) { safe = "查询失败：网络不可达或服务返回错误。"; }
            _log?.Error("限额查询失败：" + safe);
            return Error(key, safe, checkedAt);
        }

        var classified = Classify(key, usage, checkedAt);
        await AttachSubscriptionAsync(classified, row, token, checkedAt, forceSubscription, cancellationToken).ConfigureAwait(false);
        return classified;
    }

    /// <summary>
    /// Rides a subscription refresh along with a successful quota query. The refresh is gated and
    /// throttled by <see cref="SubscriptionRefresher"/>, so it only fires for a manual refresh or
    /// an account that is missing / near its expiry.
    /// </summary>
    private async Task AttachSubscriptionAsync(AccountQuotaResult result, AccountRow row, TokenResolution token, DateTimeOffset checkedAt, bool force, CancellationToken cancellationToken)
    {
        if (_subscriptions is null) { return; }
        try
        {
            var status = await _subscriptions
                .TryRefreshAsync(result.Key, token.Access, token.AccountId, row.SubscriptionExpires, checkedAt, force, cancellationToken)
                .ConfigureAwait(false);
            if (status is null) { return; } // not queried / failed → keep the current value

            if (status.ActiveUntil is { } until)
            {
                result.SubscriptionActiveUntil = until;
                result.SubscriptionRefresh = SubscriptionRefreshState.Refreshed;
            }
            else
            {
                // The lookup ran and found no expiry: authoritative empty, so show "—".
                result.SubscriptionRefresh = SubscriptionRefreshState.RefreshedEmpty;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn("订阅刷新失败：" + _secrets.Redact(ex.Message));
        }
    }

    /// <summary>
    /// Resolves the access token + ChatGPT account id for one Codex row, applying the same
    /// identity checks as a quota query so a workspace/user is never crossed. On failure the
    /// returned <see cref="TokenResolution.Error"/> carries a safe, user-facing message.
    /// </summary>
    public TokenResolution ResolveToken(AccountRow? row, IReadOnlyList<JsonNode>? credentials, DateTimeOffset? checkedAt = null)
    {
        var key = RowKey(row);
        var now = checkedAt ?? DateTimeOffset.Now;
        if (row is null || row.Kind != AccountKind.Codex)
        {
            return TokenResolution.Failed(key, QuotaResultState.Unsupported, "仅支持 OpenAI Codex OAuth 账号；其他类型不查询。", now);
        }

        var rowValue = row.Value;
        var rowInfo = CredentialHelpers.GetIdentityInfo(rowValue);
        var rowIdentity = string.IsNullOrWhiteSpace(row.Identity) ? rowInfo.Identity : row.Identity;
        var rowAccountId = string.IsNullOrWhiteSpace(rowInfo.AccountID) ? row.AccountId : rowInfo.AccountID;
        var credentialId = row.CredentialId;
        var credentialsAvailable = credentials is not null;

        var matchedValues = new List<JsonNode>();
        if (!string.IsNullOrWhiteSpace(credentialId))
        {
            if (!credentialsAvailable)
            {
                return TokenResolution.Failed(key, QuotaResultState.Error, "无法读取 OpenCode 凭据列表，已跳过该账号以免使用过期令牌。", now);
            }

            var idMatches = credentials!.Where(c => c is not null && JsonMerge.GetString(c, "id") == credentialId).ToList();
            if (idMatches.Count == 0) { return TokenResolution.Failed(key, QuotaResultState.MissingToken, "该账号已不在 OpenCode 中，请刷新账号列表后重试。", now); }
            if (idMatches.Count != 1) { return TokenResolution.Failed(key, QuotaResultState.Error, "账号 ID 不唯一，请刷新账号列表后重试。", now); }

            foreach (var item in idMatches)
            {
                var itemValue = JsonMerge.Get(item, "value");
                if (JsonMerge.GetString(item, "integrationID") != "openai"
                    || JsonMerge.GetString(itemValue, "type") != "oauth"
                    || !CredentialHelpers.IsCodexOAuthMethod(JsonMerge.GetString(itemValue, "methodID")))
                {
                    return TokenResolution.Failed(key, QuotaResultState.Error, "账号类型已变化，请刷新账号列表后重试。", now);
                }

                if (string.IsNullOrWhiteSpace(rowIdentity)) { matchedValues.Add(itemValue!); continue; }
                var itemInfo = CredentialHelpers.GetIdentityInfo(itemValue);
                if (string.IsNullOrWhiteSpace(itemInfo.Identity)) { continue; }
                if (itemInfo.Identity != rowIdentity) { return TokenResolution.Failed(key, QuotaResultState.Error, "账号身份已变化，请刷新账号列表后重试。", now); }
                matchedValues.Add(itemValue!);
            }
        }
        else if (credentialsAvailable)
        {
            foreach (var item in FindMatches(row, credentials!))
            {
                var itemValue = JsonMerge.Get(item, "value");
                if (itemValue is null || string.IsNullOrWhiteSpace(rowIdentity)) { continue; }
                var itemInfo = CredentialHelpers.GetIdentityInfo(itemValue);
                if (string.IsNullOrWhiteSpace(itemInfo.Identity) || itemInfo.Identity != rowIdentity) { continue; }
                matchedValues.Add(itemValue);
            }
        }

        var candidateValues = new List<JsonNode>();
        if (string.IsNullOrWhiteSpace(credentialId) && rowValue is not null) { candidateValues.Add(rowValue); }
        candidateValues.AddRange(matchedValues);

        _secrets.Register(rowValue);
        foreach (var v in candidateValues) { _secrets.Register(v); }

        var candidates = new List<Candidate>();
        var seenAccess = new HashSet<string>(StringComparer.Ordinal);
        foreach (var itemValue in candidateValues)
        {
            var access = JsonMerge.GetString(itemValue, "access");
            if (string.IsNullOrWhiteSpace(access) || !seenAccess.Add(access)) { continue; }
            var info = CredentialHelpers.GetIdentityInfo(itemValue);
            var expiry = CredentialHelpers.GetTokenExpiryMs(itemValue);
            candidates.Add(new Candidate(access, info.AccountID, info.Identity, expiry.ExpiresMs, expiry.FromJwt));
        }

        candidates = candidates.Where(c =>
            !string.IsNullOrWhiteSpace(c.AccountID)
            && (string.IsNullOrWhiteSpace(rowAccountId) || c.AccountID == rowAccountId)
            && (string.IsNullOrWhiteSpace(rowIdentity) || c.Identity == rowIdentity)).ToList();

        if (candidates.Count == 0)
        {
            return TokenResolution.Failed(key, QuotaResultState.MissingToken, "缺少可用的访问令牌，无法查询；请在 OpenCode 重新连接该账号。", now);
        }

        var chosen = candidates.OrderByDescending(c => c.ExpiresMs).ThenByDescending(c => c.FromJwt).First();
        if (chosen.ExpiresMs > 0 && chosen.ExpiresMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            return TokenResolution.Failed(key, QuotaResultState.Expired, "访问令牌已过期，请在 OpenCode 重新连接", now);
        }

        return new TokenResolution { Access = chosen.Access, AccountId = chosen.AccountID };
    }

    private static AccountQuotaResult Classify(string key, QuotaUsage? usage, DateTimeOffset checkedAt)
    {
        if (usage is null) { return Error(key, "查询失败：未返回任何数据。", checkedAt); }

        if (usage.PrimaryState == "Unlimited" || usage.SecondaryState == "Unlimited")
        {
            var message = string.IsNullOrWhiteSpace(usage.Message) ? "套餐不限制用量。" : usage.Message;
            return new AccountQuotaResult { Key = key, State = QuotaResultState.Success, Message = message, Usage = usage, CheckedAt = checkedAt };
        }

        if (usage.PrimaryPercent is null && usage.SecondaryPercent is null)
        {
            var message = string.IsNullOrWhiteSpace(usage.Message) ? "接口未返回 5 小时或周窗口数据（接口可能已变更）。" : usage.Message;
            return new AccountQuotaResult { Key = key, State = QuotaResultState.NoData, Message = message, Usage = usage, CheckedAt = checkedAt };
        }

        if (usage.PrimaryPercent is null || usage.SecondaryPercent is null)
        {
            var missing = usage.PrimaryPercent is null ? "5 小时" : "本周";
            return new AccountQuotaResult { Key = key, State = QuotaResultState.Partial, Message = "仅返回部分窗口数据，缺少" + missing + "窗口。", Usage = usage, CheckedAt = checkedAt };
        }

        return new AccountQuotaResult { Key = key, State = QuotaResultState.Success, Message = "查询成功。", Usage = usage, CheckedAt = checkedAt };
    }

    private static AccountQuotaResult Error(string key, string message, DateTimeOffset checkedAt)
        => new() { Key = key, State = QuotaResultState.Error, Message = message, CheckedAt = checkedAt };

    private static IEnumerable<JsonNode> FindMatches(AccountRow row, IReadOnlyList<JsonNode> credentials)
    {
        foreach (var credential in credentials)
        {
            if (credential is null || JsonMerge.GetString(credential, "integrationID") != row.Provider) { continue; }
            if (row.Kind == AccountKind.Codex)
            {
                var info = CredentialHelpers.GetIdentityInfo(credential);
                if (string.IsNullOrWhiteSpace(info.Identity)) { continue; }
                if (info.Identity == row.Identity) { yield return credential; }
                continue;
            }

            if (JsonMerge.GetString(JsonMerge.Get(JsonMerge.Get(credential, "value"), "metadata"), "cockpitID") == row.Identity) { yield return credential; }
        }
    }

    /// <summary>
    /// How many quota requests may run at once. Each row is an independent read-only GET,
    /// so a small amount of concurrency cuts the wall-clock time sharply without hammering
    /// the endpoint.
    /// </summary>
    public const int MaxConcurrency = 5;

    /// <summary>
    /// Reads credentials once, then queries each row independently (one failure never blocks
    /// others). Rows run with bounded parallelism; the returned list keeps the input order.
    /// </summary>
    public async Task<List<AccountQuotaResult>> RunBatchAsync(
        IReadOnlyList<AccountRow> rows,
        IProgress<AccountQuotaResult>? progress = null,
        CancellationToken cancellationToken = default,
        bool forceSubscription = false)
    {
        List<JsonNode>? credentials = null;
        var readFailed = false;
        try
        {
            var response = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
            credentials = response.Where(c => c is not null).Select(c => c!).ToList();
            _secrets.Register(response);
        }
        catch (Exception ex)
        {
            readFailed = true;
            _log?.Warn("读取本地凭据列表失败：" + _secrets.Redact(ex.Message));
        }

        if (rows.Count == 0) { return new List<AccountQuotaResult>(); }

        var results = new AccountQuotaResult[rows.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(rows.Count, MaxConcurrency),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, rows.Count), options, async (index, ct) =>
        {
            var row = rows[index];
            AccountQuotaResult result;
            try
            {
                if (readFailed)
                {
                    if (!string.IsNullOrWhiteSpace(row.CredentialId))
                    {
                        result = Error(RowKey(row), "无法读取 OpenCode 凭据列表，已跳过该已存账号以免使用过期令牌。", DateTimeOffset.Now);
                    }
                    else
                    {
                        result = await GetResultAsync(row, null, ct, forceSubscription).ConfigureAwait(false);
                        result.Message = "本地凭据列表读取失败，仅使用文件中的令牌。 " + result.Message;
                    }
                }
                else
                {
                    result = await GetResultAsync(row, credentials, ct, forceSubscription).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                var safe = _secrets.Redact(ex.Message);
                if (string.IsNullOrWhiteSpace(safe)) { safe = "查询失败。"; }
                _log?.Error("限额批处理单项失败：" + safe);
                result = Error(RowKey(row), safe, DateTimeOffset.Now);
            }

            results[index] = result;
            try { progress?.Report(result); }
            catch (Exception ex) { _log?.Error("限额进度回调失败：" + _secrets.Redact(ex.Message)); }
        }).ConfigureAwait(false);

        return results.ToList();
    }

    private sealed record Candidate(string Access, string AccountID, string Identity, long ExpiresMs, bool FromJwt);
}

/// <summary>Resolved Codex access token + account id, or a safe error result.</summary>
public sealed class TokenResolution
{
    public string Access { get; init; } = string.Empty;

    public string AccountId { get; init; } = string.Empty;

    public AccountQuotaResult? Error { get; init; }

    public bool Ok => Error is null && !string.IsNullOrWhiteSpace(Access);

    public static TokenResolution Failed(string key, QuotaResultState state, string message, DateTimeOffset checkedAt)
        => new() { Error = new AccountQuotaResult { Key = key, State = state, Message = message, CheckedAt = checkedAt } };
}
