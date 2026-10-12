using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AccountManager.Core.Json;

namespace AccountManager.Core.Accounts;

public sealed record IdentityInfo(string AccountID, string Email, string Identity, string AccountIDSource, string EmailSource);

public sealed record TokenExpiry(long ExpiresMs, bool FromJwt);

/// <summary>Credential value helpers shared by the account mapper and quota selector.</summary>
public static class CredentialHelpers
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public static bool IsCodexOAuthMethod(string? methodId)
        => !string.IsNullOrWhiteSpace(methodId) && Regex.IsMatch(methodId, "^chatgpt(-|$)");

    public static bool IsEmail(string? text)
        => !string.IsNullOrWhiteSpace(text) && EmailPattern.IsMatch(text);

    /// <summary>
    /// Exact identity: only builds an Identity when BOTH accountID and email are known
    /// (from metadata or JWT). Accepts a full credential or a bare value object.
    /// </summary>
    public static IdentityInfo GetIdentityInfo(JsonNode? credentialOrValue)
    {
        if (credentialOrValue is null) { return new IdentityInfo(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty); }

        var value = credentialOrValue;
        if (JsonMerge.Get(credentialOrValue, "access") is null)
        {
            var inner = JsonMerge.Get(credentialOrValue, "value");
            if (inner is not null) { value = inner; }
        }

        var access = JsonMerge.GetString(value, "access");
        var claims = Jwt.ReadPayload(access);
        var metadata = JsonMerge.Get(value, "metadata");

        var accountId = JsonMerge.GetString(metadata, "accountID") ?? string.Empty;
        var accountSource = accountId.Length > 0 ? "metadata" : string.Empty;
        if (accountId.Length == 0 && claims is not null)
        {
            accountId = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/auth"), "chatgpt_account_id") ?? string.Empty;
            if (accountId.Length > 0) { accountSource = "jwt"; }
        }

        var email = JsonMerge.GetString(metadata, "cockpitEmail") ?? string.Empty;
        var emailSource = email.Length > 0 ? "metadata" : string.Empty;
        if (email.Length == 0 && claims is not null)
        {
            email = JsonMerge.GetString(JsonMerge.Get(claims, "https://api.openai.com/profile"), "email") ?? string.Empty;
            if (email.Length > 0) { emailSource = "jwt-profile"; }
            if (email.Length == 0)
            {
                email = JsonMerge.GetString(claims, "email") ?? string.Empty;
                if (email.Length > 0) { emailSource = "jwt"; }
            }
        }

        var identity = accountId.Length > 0 && email.Length > 0
            ? accountId + "|" + email.ToLowerInvariant()
            : string.Empty;
        return new IdentityInfo(accountId, email, identity, accountSource, emailSource);
    }

    /// <summary>Valid JWT exp (seconds→ms) wins; only falls back to value.expires when absent.</summary>
    public static TokenExpiry GetTokenExpiryMs(JsonNode? value)
    {
        if (value is null) { return new TokenExpiry(0, false); }
        var access = JsonMerge.GetString(value, "access");
        if (!string.IsNullOrWhiteSpace(access))
        {
            var claims = Jwt.ReadPayload(access);
            if (claims is not null)
            {
                var exp = JsonNum.ToNumber(JsonMerge.Get(claims, "exp"));
                if (exp is > 0 and <= 253402300799)
                {
                    return new TokenExpiry((long)(exp.Value * 1000), true);
                }
            }
        }

        var expires = JsonNum.ToNumber(JsonMerge.Get(value, "expires"));
        if (expires is > 0 and <= 253402300799000)
        {
            return new TokenExpiry((long)expires.Value, false);
        }

        return new TokenExpiry(0, false);
    }
}
