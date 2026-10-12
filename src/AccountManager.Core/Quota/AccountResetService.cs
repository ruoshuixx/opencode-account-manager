using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Models;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;

namespace AccountManager.Core.Quota;

/// <summary>
/// Lists and redeems Codex banked reset credits for a saved account row. The row's access
/// token is resolved with the same identity checks as a quota query, so a workspace/user is
/// never crossed. Failures surface as <see cref="InvalidOperationException"/> with a safe,
/// already-redacted message.
/// </summary>
public sealed class AccountResetService
{
    private readonly OpenCodeApi _api;
    private readonly ResetCreditService _reset;
    private readonly AccountQuotaSelector _selector;
    private readonly SecretRegistry _secrets;
    private readonly Logging.AppLog? _log;

    public AccountResetService(OpenCodeApi api, ResetCreditService reset, AccountQuotaSelector selector, SecretRegistry secrets, Logging.AppLog? log = null)
    {
        _api = api;
        _reset = reset;
        _selector = selector;
        _secrets = secrets;
        _log = log;
    }

    public async Task<ResetCreditList> GetListAsync(AccountRow row, CancellationToken cancellationToken = default)
    {
        var token = await ResolveAsync(row, cancellationToken).ConfigureAwait(false);
        return await _reset.GetListAsync(token.Access, token.AccountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResetConsumeResult> ConsumeAsync(AccountRow row, string creditId, CancellationToken cancellationToken = default)
    {
        var token = await ResolveAsync(row, cancellationToken).ConfigureAwait(false);
        return await _reset.ConsumeAsync(token.Access, token.AccountId, creditId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TokenResolution> ResolveAsync(AccountRow row, CancellationToken cancellationToken)
    {
        List<JsonNode>? credentials = null;
        try
        {
            var response = await _api.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
            credentials = response.Where(c => c is not null).Select(c => c!).ToList();
            _secrets.Register(response);
        }
        catch (Exception ex)
        {
            _log?.Warn("读取本地凭据列表失败（重置券）：" + _secrets.Redact(ex.Message));
        }

        var token = _selector.ResolveToken(row, credentials);
        if (!token.Ok)
        {
            var message = _secrets.Redact(token.Error?.Message ?? "无法解析账号令牌，请刷新账号列表后重试。");
            _log?.Warn("重置券令牌解析失败：" + message);
            throw new InvalidOperationException(message);
        }

        return token;
    }
}
