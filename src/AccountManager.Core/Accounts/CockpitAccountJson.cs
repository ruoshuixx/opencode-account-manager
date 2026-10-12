using System.Text.Json.Nodes;
using AccountManager.Core.OpenCode;

namespace AccountManager.Core.Accounts;

/// <summary>
/// Secret-name handling for Cockpit Codex JSON. Cockpit's native field is
/// <c>openai_api_key</c>; legacy auth.json-style exports also used uppercase and generic aliases.
/// Aliases are accepted on input, but are collapsed before they can enter the local mirror.
/// </summary>
internal static class CockpitSecretFields
{
    private static readonly string[] ApiKeyAliases = { "openai_api_key", "OPENAI_API_KEY", "api_key", "key" };
    private static readonly string[] TokenAliases = { "id_token", "access_token", "refresh_token" };
    // serde aliases pinned from Cockpit v1.3.65 CodexAccount / CodexAgentIdentity. The
    // agent_identity object is sealed as a unit, after its declared camelCase aliases normalize.
    private static readonly string[] TwoFactorAliases = { "two_factor_secret", "twoFactorSecret", "accountTwoFactorSecret" };
    private static readonly string[] PasswordAliases = { "account_password", "accountPassword", "password" };
    private static readonly string[] AgentIdentityAliases = { "agent_identity", "agentIdentity" };
    private static readonly string[] EgressProxyAliases = { "egress_proxy", "egress_proxy_url" };
    private static readonly string[] AdditionalSecretFields = { "two_factor_secret", "account_password", "agent_identity", "egress_proxy" };

    public static JsonObject Canonicalize(JsonObject source)
    {
        var result = (JsonObject)source.DeepClone();
        CanonicalizeGroup(result, ApiKeyAliases, "openai_api_key", keepEmpty: false,
            "账号包含互相冲突的 API Key 别名，已拒绝导入。");

        CanonicalizeGroup(result, new[] { "id_token" }, "id_token", keepEmpty: true,
            "账号包含互相冲突的令牌字段，已拒绝导入。");
        CanonicalizeGroup(result, new[] { "access_token" }, "access_token", keepEmpty: true,
            "账号包含互相冲突的令牌字段，已拒绝导入。");
        CanonicalizeGroup(result, new[] { "refresh_token" }, "refresh_token", keepEmpty: true,
            "账号包含互相冲突的令牌字段，已拒绝导入。");

        CanonicalizeGroup(result, TwoFactorAliases, "two_factor_secret", keepEmpty: true,
            "账号包含互相冲突的两步验证字段，已拒绝导入。");
        CanonicalizeGroup(result, PasswordAliases, "account_password", keepEmpty: true,
            "账号包含互相冲突的密码字段，已拒绝导入。");
        CanonicalizeGroup(result, EgressProxyAliases, "egress_proxy", keepEmpty: true,
            "账号包含互相冲突的出口代理字段，已拒绝导入。");
        CanonicalizeAgentIdentity(result);

        var tokenProperties = result.Where(p => string.Equals(p.Key, "tokens", StringComparison.OrdinalIgnoreCase)).ToList();
        if (tokenProperties.Count > 0)
        {
            var chosen = ChooseEquivalent(tokenProperties.Select(p => p.Value), "账号包含互相冲突的令牌对象，已拒绝导入。");
            foreach (var property in tokenProperties) { result.Remove(property.Key); }

            if (chosen is JsonObject tokenObject)
            {
                var normalizedTokens = (JsonObject)tokenObject.DeepClone();
                foreach (var tokenName in TokenAliases)
                {
                    CanonicalizeGroup(normalizedTokens, new[] { tokenName }, tokenName, keepEmpty: true,
                        "账号包含互相冲突的令牌字段，已拒绝导入。");
                }

                result["tokens"] = normalizedTokens;
            }
            else
            {
                result["tokens"] = chosen?.DeepClone();
            }
        }

        return result;
    }

    /// <summary>Removes every accepted secret alias, including aliases in older plaintext mirrors.</summary>
    public static void RemoveAll(JsonObject account)
    {
        foreach (var property in account.Select(p => p.Key).ToList())
        {
            if (IsApiKeyAlias(property)
                || string.Equals(property, "tokens", StringComparison.OrdinalIgnoreCase)
                || TokenAliases.Contains(property, StringComparer.OrdinalIgnoreCase)
                || IsAdditionalSecretAlias(property))
            {
                account.Remove(property);
            }
        }
    }

    /// <summary>Removes stale live credentials while retaining restored source metadata secrets.</summary>
    public static void RemoveLiveCredentials(JsonObject account)
    {
        foreach (var property in account.Select(p => p.Key).ToList())
        {
            if (IsApiKeyAlias(property)
                || string.Equals(property, "tokens", StringComparison.OrdinalIgnoreCase)
                || TokenAliases.Contains(property, StringComparer.OrdinalIgnoreCase))
            {
                account.Remove(property);
            }
        }
    }

    public static bool IsAdditionalSecretAlias(string name)
        => TwoFactorAliases.Contains(name, StringComparer.OrdinalIgnoreCase)
            || PasswordAliases.Contains(name, StringComparer.OrdinalIgnoreCase)
            || AgentIdentityAliases.Contains(name, StringComparer.OrdinalIgnoreCase)
            || EgressProxyAliases.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsCanonicalAdditionalSecretField(string name)
        => AdditionalSecretFields.Contains(name, StringComparer.Ordinal);

    public static bool HasNonCanonicalAgentIdentityField(JsonObject identity)
    {
        var canonicalFields = new[]
        {
            "agent_runtime_id", "agent_private_key", "task_id", "account_id", "chatgpt_user_id",
            "plan_type", "chatgpt_account_is_fedramp",
        };
        var aliases = new[]
        {
            "agent_runtime_id", "agentRuntimeId", "agent_private_key", "agentPrivateKey", "task_id", "taskId",
            "account_id", "accountId", "chatgpt_user_id", "chatgptUserId", "plan_type", "planType",
            "chatgpt_account_is_fedramp", "chatgptAccountIsFedramp",
        };
        return identity.Select(p => p.Key).Any(name => aliases.Contains(name, StringComparer.OrdinalIgnoreCase)
            && !canonicalFields.Contains(name, StringComparer.Ordinal));
    }

    public static IReadOnlyList<string> ExportableAdditionalSecretFields => AdditionalSecretFields;

    public static bool IsApiKeyAlias(string name)
        => ApiKeyAliases.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsTokenAlias(string name)
        => TokenAliases.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void CanonicalizeAgentIdentity(JsonObject account)
    {
        var properties = account.Where(p => AgentIdentityAliases.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (properties.Count == 0) { return; }

        var normalized = properties.Select(p => p.Value is JsonObject identity
            ? (JsonNode?)CanonicalizeAgentIdentityObject(identity)
            : p.Value?.DeepClone()).ToList();
        var chosen = ChooseEquivalent(normalized, "账号包含互相冲突的 Agent Identity 别名，已拒绝导入。");
        foreach (var property in properties) { account.Remove(property.Key); }
        if (chosen is not null) { account["agent_identity"] = chosen.DeepClone(); }
    }

    private static JsonObject CanonicalizeAgentIdentityObject(JsonObject source)
    {
        var identity = (JsonObject)source.DeepClone();
        CanonicalizeGroup(identity, new[] { "agent_runtime_id", "agentRuntimeId" }, "agent_runtime_id", keepEmpty: true,
            "Agent Identity 包含互相冲突的 runtime ID 别名，已拒绝导入。");
        CanonicalizeGroup(identity, new[] { "agent_private_key", "agentPrivateKey" }, "agent_private_key", keepEmpty: true,
            "Agent Identity 包含互相冲突的私钥别名，已拒绝导入。");
        CanonicalizeGroup(identity, new[] { "task_id", "taskId" }, "task_id", keepEmpty: true,
            "Agent Identity 包含互相冲突的 task ID 别名，已拒绝导入。");
        CanonicalizeGroup(identity, new[] { "account_id", "accountId" }, "account_id", keepEmpty: true,
            "Agent Identity 包含互相冲突的 account ID 别名，已拒绝导入。");
        CanonicalizeGroup(identity, new[] { "chatgpt_user_id", "chatgptUserId" }, "chatgpt_user_id", keepEmpty: true,
            "Agent Identity 包含互相冲突的 user ID 别名，已拒绝导入。");
        CanonicalizeGroup(identity, new[] { "plan_type", "planType" }, "plan_type", keepEmpty: true,
            "Agent Identity 包含互相冲突的 plan 别名，已拒绝导入。");
        CanonicalizeGroup(identity, new[] { "chatgpt_account_is_fedramp", "chatgptAccountIsFedramp" }, "chatgpt_account_is_fedramp", keepEmpty: true,
            "Agent Identity 包含互相冲突的 FedRAMP 字段，已拒绝导入。");
        return identity;
    }

    private static void CanonicalizeGroup(JsonObject obj, IEnumerable<string> aliases, string canonical,
        bool keepEmpty, string conflictMessage)
    {
        var names = aliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var properties = obj.Where(p => names.Contains(p.Key)).ToList();
        if (properties.Count == 0) { return; }

        var chosen = ChooseNonEmpty(properties.Select(p => p.Value), keepEmpty, conflictMessage);
        foreach (var property in properties) { obj.Remove(property.Key); }
        if (chosen is not null) { obj[canonical] = chosen.DeepClone(); }
    }

    private static JsonNode? ChooseNonEmpty(IEnumerable<JsonNode?> values, bool keepEmpty, string conflictMessage)
    {
        var list = values.ToList();
        var nonEmpty = list.Where(v => !IsEmpty(v)).ToList();
        var chosen = nonEmpty.FirstOrDefault();
        if (chosen is not null)
        {
            if (nonEmpty.Skip(1).Any(v => !Equivalent(chosen, v))) { throw new OpenCodeException(conflictMessage); }
            return chosen;
        }

        return keepEmpty ? list.FirstOrDefault(v => v is not null) : null;
    }

    private static JsonNode? ChooseEquivalent(IEnumerable<JsonNode?> values, string conflictMessage)
    {
        var list = values.Where(v => v is not null).ToList();
        if (list.Count == 0) { return null; }
        var chosen = list[0];
        if (list.Skip(1).Any(v => !Equivalent(chosen, v))) { throw new OpenCodeException(conflictMessage); }
        return chosen;
    }

    private static bool IsEmpty(JsonNode? value)
    {
        if (value is null) { return true; }
        return value is JsonValue scalar
            && scalar.TryGetValue<string>(out var text)
            && string.IsNullOrWhiteSpace(text);
    }

    private static bool Equivalent(JsonNode? left, JsonNode? right)
    {
        if (left is JsonValue l && l.TryGetValue<string>(out var leftText)
            && right is JsonValue r && r.TryGetValue<string>(out var rightText))
        {
            return string.Equals(leftText, rightText, StringComparison.Ordinal);
        }

        return JsonNode.DeepEquals(left, right);
    }
}

/// <summary>
/// Structural contract snapshot from Cockpit v1.3.65's public
/// <c>src-tauri/src/models/codex.rs</c> <c>CodexAccount</c>/<c>CodexTokens</c> serde model.
/// Required timestamps are signed i64 values; defaulted bools/enums and typed containers are
/// checked when present. Unknown fields remain permitted, as serde's default does. This is a
/// compile-time contract; no runtime network lookup is used.
/// </summary>
internal static class CockpitAccountSchema
{
    private static readonly string[] OptionalStringFields =
    {
        "openai_api_key", "api_base_url", "api_provider_id", "api_provider_name", "api_wire_api",
        "api_vision_routing_model", "api_instance_access_mode", "api_startup_model", "bound_oauth_account_id",
        "upstream_grok_account_id", "user_id", "plan_type", "subscription_active_until", "auth_file_plan_type",
        "account_id", "organization_id", "account_name", "account_structure", "account_note", "egress_proxy",
        "codex_fingerprint_mode", "two_factor_secret", "account_password", "phone_number", "mail_url",
        "token_source_mode", "authorization_status", "reauth_reason", "client_auth_status",
        "last_client_auth_instance_id", "subscription_query_last_error",
    };

    private static readonly string[] OptionalInt64Fields =
    {
        "token_updated_at", "last_client_auth_observed_at", "last_client_login_redirect_at", "last_client_launch_at",
        "usage_updated_at", "subscription_query_last_attempt_at", "subscription_query_last_success_at",
        "subscription_query_next_retry_at",
    };

    private static readonly string[] BoolFields =
    {
        "api_sync_model_catalog_to_codex", "api_supports_websockets", "api_supports_vision",
        "egress_proxy_disabled", "codex_cli_only", "codex_cli_only_allow_app_server", "requires_reauth",
        "bound_oauth_use_local_gateway",
    };

    public static bool IsInt64Value(JsonNode? node) => IsInt64(node);
    public static string? GetOptionalFieldInvalidReason(JsonObject account) => OptionalFieldInvalidReason(account);

    public static bool IsValid(JsonNode? node)
    {
        try { return GetInvalidReason(node) is null; }
        catch { return false; }
    }

    /// <summary>Returns a field-only diagnostic for tests; never includes account values.</summary>
    internal static string? GetInvalidReason(JsonNode? node)
    {
        if (node is not JsonObject account) { return "account object"; }
        if (!IsString(account["id"]) || !IsString(account["email"])) { return "id/email"; }
        if (!TryString(account["auth_mode"], out var authMode) || authMode is not ("oauth" or "apikey")) { return "auth_mode"; }
        if (!IsInt64(account["created_at"]) || !IsInt64(account["last_used"])) { return "created_at/last_used i64"; }

        if (account["tokens"] is not JsonObject tokens
            || !IsString(tokens["id_token"])
            || !IsString(tokens["access_token"])) { return "tokens.id_token/access_token"; }
        if (tokens.ContainsKey("refresh_token") && !IsNullableString(tokens["refresh_token"])) { return "tokens.refresh_token"; }
        if (HasNonCanonicalTokenAlias(tokens)) { return "non-canonical token field"; }

        if (HasNonCanonicalTopLevelSecretAlias(account, authMode)) { return "non-canonical secret alias"; }
        if (account["agent_identity"] is JsonObject agentIdentity && CockpitSecretFields.HasNonCanonicalAgentIdentityField(agentIdentity))
        {
            return "non-canonical agent_identity field";
        }
        if (authMode == "oauth")
        {
            if (account["account_id"] is not null && !IsNullableString(account["account_id"])) { return "account_id"; }
        }
        else
        {
            if (!TryString(account["openai_api_key"], out var apiKey) || string.IsNullOrWhiteSpace(apiKey)) { return "openai_api_key"; }
            if (!TryString(account["api_provider_mode"], out var providerMode) || providerMode is not ("custom" or "openai_builtin")) { return "api_provider_mode"; }
            if (providerMode != "custom") { return "API provider must be custom"; }
            if (!TryString(account["api_base_url"], out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl)) { return "api_base_url"; }
            if (!TryString(account["api_provider_id"], out var providerId) || string.IsNullOrWhiteSpace(providerId)) { return "api_provider_id"; }
            if (!TryString(account["api_provider_name"], out var providerName) || string.IsNullOrWhiteSpace(providerName)) { return "api_provider_name"; }
            if (!TryString(account["api_wire_api"], out var wire) || wire is not ("responses" or "chat_completions")) { return "api_wire_api"; }
            if (account["api_model_catalog"] is not JsonArray models || models.Count == 0 || models.Any(m => !IsString(m))) { return "api_model_catalog"; }
        }

        return OptionalFieldInvalidReason(account);
    }

    private static string? OptionalFieldInvalidReason(JsonObject account)
    {
        foreach (var field in OptionalStringFields)
        {
            if (account.ContainsKey(field) && !IsNullableString(account[field])) { return field; }
        }
        foreach (var field in OptionalInt64Fields)
        {
            if (account.ContainsKey(field) && !IsNullableInt64(account[field])) { return field + " i64"; }
        }
        foreach (var field in BoolFields)
        {
            if (account.ContainsKey(field) && !IsBoolean(account[field])) { return field + " bool"; }
        }

        if (account.ContainsKey("api_provider_mode")
            && (!TryString(account["api_provider_mode"], out var mode) || mode is not ("custom" or "openai_builtin"))) { return "api_provider_mode enum"; }
        if (account.ContainsKey("api_wire_api")
            && account["api_wire_api"] is not null
            && (!TryString(account["api_wire_api"], out var wireValue) || wireValue is not ("responses" or "chat_completions"))) { return "api_wire_api enum"; }
        if (account.ContainsKey("app_speed")
            && (!TryString(account["app_speed"], out var speed) || speed is not ("standard" or "fast" or "ultrafast"))) { return "app_speed enum"; }
        if (account.ContainsKey("token_generation") && !IsUInt64(account["token_generation"])) { return "token_generation u64"; }
        if (account.ContainsKey("api_model_catalog") && !IsStringArray(account["api_model_catalog"])) { return "api_model_catalog strings"; }
        if (account.ContainsKey("api_image_generation_account_ids") && !IsStringArray(account["api_image_generation_account_ids"])) { return "api_image_generation_account_ids strings"; }
        if (account.ContainsKey("api_model_vision_support") && !IsBooleanMap(account["api_model_vision_support"])) { return "api_model_vision_support bool map"; }
        if (account.ContainsKey("api_model_context_windows") && !IsInt64Map(account["api_model_context_windows"])) { return "api_model_context_windows i64 map"; }
        if (account.ContainsKey("api_model_mappings") && !IsModelMappings(account["api_model_mappings"])) { return "api_model_mappings"; }
        if (account.ContainsKey("tags") && account["tags"] is not null && !IsStringArray(account["tags"])) { return "tags strings"; }
        if (account.ContainsKey("quota") && !IsQuota(account["quota"])) { return "quota"; }
        if (account.ContainsKey("team_quota_history") && !IsTeamQuotaHistory(account["team_quota_history"])) { return "team_quota_history"; }
        if (account.ContainsKey("quota_error") && !IsQuotaError(account["quota_error"])) { return "quota_error"; }
        if (account.ContainsKey("agent_identity") && !IsAgentIdentity(account["agent_identity"])) { return "agent_identity"; }
        return null;
    }

    private static bool HasNonCanonicalTopLevelSecretAlias(JsonObject account, string authMode)
    {
        foreach (var name in account.Select(p => p.Key))
        {
            if (CockpitSecretFields.IsApiKeyAlias(name)
                && !(authMode == "apikey" && string.Equals(name, "openai_api_key", StringComparison.Ordinal))) { return true; }
            if (CockpitSecretFields.IsAdditionalSecretAlias(name) && !CockpitSecretFields.IsCanonicalAdditionalSecretField(name)) { return true; }
            if (CockpitSecretFields.IsTokenAlias(name)) { return true; }
            if (string.Equals(name, "tokens", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "tokens", StringComparison.Ordinal)) { return true; }
        }

        return false;
    }

    private static bool HasNonCanonicalTokenAlias(JsonObject tokens)
        => tokens.Select(p => p.Key).Any(name => CockpitSecretFields.IsTokenAlias(name)
            && name is not ("id_token" or "access_token" or "refresh_token"));

    private static bool IsQuota(JsonNode? node)
    {
        if (node is null) { return true; }
        if (node is not JsonObject quota || !IsInt32(quota["hourly_percentage"]) || !IsInt32(quota["weekly_percentage"])) { return false; }
        foreach (var field in new[] { "hourly_reset_time", "weekly_reset_time", "hourly_window_minutes", "weekly_window_minutes", "reset_credits_available", "reset_credits_next_expires_at" })
        {
            if (quota.ContainsKey(field) && !IsNullableInt64(quota[field])) { return false; }
        }
        foreach (var field in new[] { "hourly_window_present", "weekly_window_present" })
        {
            if (quota.ContainsKey(field) && !IsNullableBoolean(quota[field])) { return false; }
        }
        if (quota.ContainsKey("reset_credits") && !IsResetCredits(quota["reset_credits"])) { return false; }
        return true;
    }

    private static bool IsTeamQuotaHistory(JsonNode? node)
    {
        if (node is null) { return true; }
        return node is JsonObject history
            && IsString(history["user_id"])
            && IsString(history["account_id"])
            && IsInt64(history["observed_at"])
            && IsNullableInt64(GetOptional(history, "hourly_reset_time"))
            && IsNullableInt64(GetOptional(history, "weekly_reset_time"))
            && IsNullableInt32(GetOptional(history, "hourly_percentage"))
            && IsNullableInt32(GetOptional(history, "weekly_percentage"));
    }

    private static bool IsQuotaError(JsonNode? node)
        => node is null || node is JsonObject error
            && IsString(error["message"])
            && IsInt64(error["timestamp"])
            && IsNullableString(GetOptional(error, "code"));

    private static bool IsAgentIdentity(JsonNode? node)
        => node is null || node is JsonObject identity
            && IsString(identity["agent_runtime_id"])
            && IsString(identity["agent_private_key"])
            && IsString(identity["account_id"])
            && IsString(identity["chatgpt_user_id"])
            && IsNullableString(GetOptional(identity, "task_id"))
            && IsNullableString(GetOptional(identity, "email"))
            && IsNullableString(GetOptional(identity, "plan_type"))
            && (!identity.ContainsKey("chatgpt_account_is_fedramp") || IsBoolean(identity["chatgpt_account_is_fedramp"]));

    private static bool IsResetCredits(JsonNode? node)
    {
        if (node is not JsonArray credits) { return false; }
        foreach (var credit in credits)
        {
            if (credit is not JsonObject item) { return false; }
            foreach (var field in new[] { "id", "status", "reset_type" })
            {
                if (item.ContainsKey(field) && !IsNullableString(item[field])) { return false; }
            }
            foreach (var field in new[] { "granted_at", "expires_at", "redeemed_at" })
            {
                if (item.ContainsKey(field) && !IsNullableInt64(item[field])) { return false; }
            }
        }
        return true;
    }

    private static bool IsModelMappings(JsonNode? node)
    {
        if (node is not JsonArray mappings) { return false; }
        return mappings.All(item => item is JsonObject mapping && IsString(mapping["client_model"]) && IsString(mapping["upstream_model"]));
    }

    private static bool IsBooleanMap(JsonNode? node)
        => node is JsonObject map && map.All(pair => IsBoolean(pair.Value));

    private static bool IsInt64Map(JsonNode? node)
        => node is JsonObject map && map.All(pair => IsInt64(pair.Value));

    private static bool IsStringArray(JsonNode? node)
        => node is JsonArray array && array.All(IsString);

    private static bool IsNullableString(JsonNode? node) => node is null || IsString(node);
    private static bool IsNullableInt64(JsonNode? node) => node is null || IsInt64(node);
    private static bool IsNullableInt32(JsonNode? node) => node is null || IsInt32(node);
    private static bool IsNullableBoolean(JsonNode? node) => node is null || IsBoolean(node);
    private static JsonNode? GetOptional(JsonObject obj, string name) => obj.TryGetPropertyValue(name, out var value) ? value : null;

    private static bool IsString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);
    private static bool TryString(JsonNode? node, out string text)
    {
        text = string.Empty;
        if (node is JsonValue value && value.TryGetValue<string>(out var parsed))
        {
            text = parsed;
            return true;
        }

        return false;
    }

    private static bool IsBoolean(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out _);

    private static bool IsInt64(JsonNode? node)
    {
        return TryGetInt64Value(node, out _);
    }

    internal static bool TryGetInt64Value(JsonNode? node, out long result)
    {
        result = 0;
        if (node is not JsonValue value || value.TryGetValue<bool>(out _) || value.TryGetValue<string>(out _)) { return false; }
        if (value.TryGetValue<long>(out result)) { return true; }
        if (value.TryGetValue<int>(out var intValue)) { result = intValue; return true; }
        if (value.TryGetValue<uint>(out var uintValue)) { result = uintValue; return true; }
        if (value.TryGetValue<short>(out var shortValue)) { result = shortValue; return true; }
        if (value.TryGetValue<ushort>(out var ushortValue)) { result = ushortValue; return true; }
        if (value.TryGetValue<sbyte>(out var sbyteValue)) { result = sbyteValue; return true; }
        if (value.TryGetValue<byte>(out var byteValue)) { result = byteValue; return true; }
        if (value.TryGetValue<ulong>(out var ulongValue) && ulongValue <= long.MaxValue) { result = (long)ulongValue; return true; }
        return false;
    }

    private static bool IsUInt64(JsonNode? node)
    {
        if (node is not JsonValue value || value.TryGetValue<bool>(out _) || value.TryGetValue<string>(out _)) { return false; }
        return value.TryGetValue<ulong>(out _) || value.TryGetValue<uint>(out _) || value.TryGetValue<ushort>(out _) || value.TryGetValue<byte>(out _)
            || (TryGetInt64Value(node, out var signed) && signed >= 0);
    }

    private static bool IsInt32(JsonNode? node)
    {
        return TryGetInt64Value(node, out var value) && value is >= int.MinValue and <= int.MaxValue;
    }
}
