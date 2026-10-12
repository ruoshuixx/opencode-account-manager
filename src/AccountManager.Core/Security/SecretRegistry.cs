using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AccountManager.Core.Security;

/// <summary>
/// Central registry of secret strings seen during a run. Everything written to logs or
/// the UI must pass through <see cref="Redact"/> first. Equivalent to the PowerShell
/// <c>Register-Secrets</c> / <c>Protect-LogText</c> pair.
/// </summary>
public sealed class SecretRegistry
{
    private static readonly Regex[] ExtraPatterns =
    {
        new(@"(?i)Bearer\s+[^\s,;""']+", RegexOptions.Compiled),
        new(@"\beyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]*)?", RegexOptions.Compiled),
        new(@"\bsk-[A-Za-z0-9_-]+", RegexOptions.Compiled),
        new(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{40,}(?![A-Za-z0-9_-])", RegexOptions.Compiled),
    };

    private const string SecretFieldPattern =
        "(?i)^(key|access|refresh|access_token|refresh_token|id_token|openai_api_key|api_key|apiKey|personal_access_token|two_factor_secret|twoFactorSecret|accountTwoFactorSecret|account_password|accountPassword|password|agent_private_key|agentPrivateKey|egress_proxy|egress_proxy_url)$";

    private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);
    private readonly bool _applyLongValueFilter;

    /// <param name="applyLongValueFilter">
    /// The aggressive "any 40+ char opaque token" filter. On for the real app; tests can
    /// disable it to assert exact strings.
    /// </param>
    public SecretRegistry(bool applyLongValueFilter = true) => _applyLongValueFilter = applyLongValueFilter;

    public void RegisterValue(string? value)
    {
        if (!string.IsNullOrEmpty(value)) { _secrets.TryAdd(value, 0); }
    }

    /// <summary>Walk an arbitrary JSON object and register every secret-looking field value.</summary>
    public void Register(JsonNode? node, int depth = 0)
    {
        if (node is null || depth > 20) { return; }
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    if (Regex.IsMatch(kv.Key, SecretFieldPattern)
                        && kv.Value is JsonValue value
                        && value.TryGetValue<string>(out var s)
                        && s.Length > 0)
                    {
                        RegisterValue(s);
                    }
                    else
                    {
                        Register(kv.Value, depth + 1);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr) { Register(item, depth + 1); }
                break;
        }
    }

    /// <summary>Register the value of a known secret-bearing key (handles "Bearer x" values).</summary>
    public bool RegisterKnownSecret(string name, JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var s) || string.IsNullOrEmpty(s))
        {
            return false;
        }

        var bearer = Regex.Match(s, @"^Bearer\s+(.+)$", RegexOptions.IgnoreCase);
        var isSecretName = Regex.IsMatch(name, @"(?i)(api[_-]?key|access[_-]?token|refresh[_-]?token|secret|password|bearer)")
            || Regex.IsMatch(name, @"(?i)^(authorization|key|token)$");
        if (!isSecretName && !bearer.Success) { return false; }

        RegisterValue(s);
        if (bearer.Success) { RegisterValue(bearer.Groups[1].Value.Trim()); }
        return true;
    }

    /// <summary>Register secret-bearing values inside a provider <c>info</c> subtree.</summary>
    public void RegisterProviderSecrets(JsonNode? node, int depth = 0)
    {
        if (node is null || depth > 30) { return; }
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    if (RegisterKnownSecret(kv.Key, kv.Value)) { continue; }
                    RegisterProviderSecrets(kv.Value, depth + 1);
                }

                break;
            case JsonArray arr:
                foreach (var item in arr) { RegisterProviderSecrets(item, depth + 1); }
                break;
        }
    }

    public string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) { return string.Empty; }

        foreach (var secret in _secrets.Keys.OrderByDescending(s => s.Length))
        {
            message = message.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        message = ExtraPatterns[0].Replace(message, "Bearer [REDACTED]");
        message = ExtraPatterns[1].Replace(message, "[REDACTED-JWT]");
        message = ExtraPatterns[2].Replace(message, "[REDACTED-KEY]");
        if (_applyLongValueFilter) { message = ExtraPatterns[3].Replace(message, "[REDACTED-LONG-VALUE]"); }

        return message.Replace("\r", " ").Replace("\n", " ");
    }
}
