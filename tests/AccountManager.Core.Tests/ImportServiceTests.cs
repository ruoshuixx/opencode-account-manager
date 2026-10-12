using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Export;
using AccountManager.Core.Import;
using AccountManager.Core.OpenCode;
using Xunit;

namespace AccountManager.Core.Tests;

public class ImportServiceTests
{
    private static string Base64Url(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Jwt(JsonObject payload) =>
        Base64Url("""{"alg":"none"}""") + "." + Base64Url(payload.ToJsonString()) + ".sig";

    private static JsonObject OAuthAccount(JsonObject? idTokenClaims = null)
    {
        var access = Jwt(new JsonObject
        {
            ["exp"] = 4102444800L,
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "acct_1234567890" },
            ["https://api.openai.com/profile"] = new JsonObject { ["email"] = "a@b.com" },
        });

        return new JsonObject
        {
            ["auth_mode"] = "oauth",
            ["email"] = "a@b.com",
            ["account_id"] = "acct_1234567890",
            ["tokens"] = new JsonObject
            {
                ["access_token"] = access,
                ["refresh_token"] = "refresh-value",
                ["id_token"] = idTokenClaims is null ? string.Empty : Jwt(idTokenClaims),
            },
        };
    }

    [Fact]
    public void ConvertAccountReadsSubscriptionFromIdTokenClaim()
    {
        var account = OAuthAccount(new JsonObject
        {
            ["https://api.openai.com/auth"] = new JsonObject
            {
                ["chatgpt_subscription_active_until"] = "2030-01-02T03:04:05+00:00",
            },
        });

        var row = ImportService.ConvertAccount(account, "chatgpt-browser");

        Assert.Equal("2030-01-02T03:04:05+00:00",
            row.Value["metadata"]!["subscriptionActiveUntil"]!.GetValue<string>());
    }

    [Fact]
    public void ConvertAccountPrefersTopLevelSubscriptionOverIdToken()
    {
        var account = OAuthAccount(new JsonObject
        {
            ["https://api.openai.com/auth"] = new JsonObject
            {
                ["chatgpt_subscription_active_until"] = "2030-01-02T03:04:05+00:00",
            },
        });
        account["subscription_active_until"] = "2031-05-05T00:00:00+00:00";

        var row = ImportService.ConvertAccount(account, "chatgpt-browser");

        Assert.Equal("2031-05-05T00:00:00+00:00",
            row.Value["metadata"]!["subscriptionActiveUntil"]!.GetValue<string>());
    }

    [Fact]
    public void ConvertAccountOmitsSubscriptionWhenNoSourceProvidesIt()
    {
        var row = ImportService.ConvertAccount(OAuthAccount(), "chatgpt-browser");

        Assert.Null(row.Value["metadata"]!["subscriptionActiveUntil"]);
    }

    [Fact]
    public void OAuthImportKeepsActuallyStoredIdTokenAndHistoryForPortableExport()
    {
        var account = OAuthAccount(new JsonObject { ["sub"] = "simulated-user" });
        account["created_at"] = 1700000010L;
        account["last_used"] = 1700000020L;
        account["plan_type"] = "team";
        var row = ImportService.ConvertAccount(account, "chatgpt-browser");
        Assert.Equal(account["tokens"]!["id_token"]!.GetValue<string>(), row.Value["metadata"]!["id_token"]!.GetValue<string>());
        Assert.Equal(1700000010L, row.Value["metadata"]!["created_at"]!.GetValue<long>());
        Assert.Equal(1700000020L, row.Value["metadata"]!["last_used"]!.GetValue<long>());
        Assert.Equal("team", row.Value["metadata"]!["planType"]!.GetValue<string>());
    }

    [Fact]
    public void ConvertAccountReadsUppercaseEnvStyleApiKey()
    {
        var account = new JsonObject
        {
            ["auth_mode"] = "apikey",
            ["OPENAI_API_KEY"] = "sk-test-value",
            ["api_base_url"] = "https://ark.cn-beijing.volces.com/api/coding/v3",
            ["api_provider_id"] = "volcengine_agentplan",
            ["api_provider_name"] = "火山Codingplan",
        };

        var row = ImportService.ConvertAccount(account, "chatgpt-browser");

        Assert.Equal("sk-test-value", row.Value["key"]!.GetValue<string>());
        Assert.Equal("volcengine_agentplan", row.Value["metadata"]!["cockpitID"]!.GetValue<string>());
        Assert.Equal("sk-test-value", row.Raw!["openai_api_key"]!.GetValue<string>());
        Assert.Null(row.Raw["OPENAI_API_KEY"]);
    }

    [Fact]
    public void ConvertAccountAcceptsEqualAndEmptyLegacyKeyAliasesButCanonicalizesRaw()
    {
        var account = new JsonObject
        {
            ["auth_mode"] = "apikey",
            ["openai_api_key"] = "SIMULATED-KEY",
            ["OPENAI_API_KEY"] = "SIMULATED-KEY",
            ["api_key"] = string.Empty,
            ["api_base_url"] = "https://api.example.test/v1",
            ["api_provider_id"] = "provider-test",
            ["api_provider_name"] = "Test API",
            ["api_model_catalog"] = new JsonArray("model-test"),
            ["api_wire_api"] = "responses",
        };

        var row = ImportService.ConvertAccount(account, "chatgpt-browser");

        Assert.Equal("SIMULATED-KEY", row.Value["key"]!.GetValue<string>());
        Assert.Equal("SIMULATED-KEY", row.Raw!["openai_api_key"]!.GetValue<string>());
        Assert.False(row.Raw.ContainsKey("OPENAI_API_KEY"));
        Assert.False(row.Raw.ContainsKey("api_key"));
    }

    [Fact]
    public void ConvertAccountAcceptsLatestNativeApiFieldsAndPreservesMirrorMetadata()
    {
        var account = new JsonObject
        {
            ["id"] = "cockpit-api-id",
            ["email"] = string.Empty,
            ["auth_mode"] = "apikey",
            ["openai_api_key"] = "SIMULATED-LATEST-KEY",
            ["api_base_url"] = "https://api.example.test/v1",
            ["api_provider_mode"] = "custom",
            ["api_provider_id"] = "provider-latest",
            ["api_provider_name"] = "Latest Provider",
            ["api_model_catalog"] = new JsonArray("model-one", "model-two"),
            ["api_wire_api"] = "responses",
            ["api_supports_websockets"] = true,
            ["api_supports_vision"] = true,
            ["api_model_vision_support"] = new JsonObject { ["model-one"] = true, ["model-two"] = false },
            ["api_model_context_windows"] = new JsonObject { ["model-one"] = 128000L },
            ["api_sync_model_catalog_to_codex"] = false,
            ["app_speed"] = "standard",
            ["token_generation"] = 7UL,
            ["token_source_mode"] = "managed",
            ["tokens"] = new JsonObject { ["id_token"] = string.Empty, ["access_token"] = string.Empty },
            ["quota"] = new JsonObject { ["hourly_percentage"] = 50, ["weekly_percentage"] = 75 },
            ["account_note"] = "simulated note",
            ["created_at"] = 1700000001L,
            ["last_used"] = 1700000002L,
        };

        var row = ImportService.ConvertAccount(account, "chatgpt-browser");

        Assert.Equal("responses", row.Raw!["api_wire_api"]!.GetValue<string>());
        Assert.True(row.Raw["api_supports_websockets"]!.GetValue<bool>());
        Assert.True(row.Raw["api_supports_vision"]!.GetValue<bool>());
        Assert.True(row.Raw["api_model_vision_support"]!["model-one"]!.GetValue<bool>());
        Assert.Equal(128000L, row.Raw["api_model_context_windows"]!["model-one"]!.GetValue<long>());
        Assert.Equal("simulated note", row.Raw["account_note"]!.GetValue<string>());
        Assert.Equal("SIMULATED-LATEST-KEY", row.Raw["openai_api_key"]!.GetValue<string>());
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, row.Definition!["package"]!.GetValue<string>());
        Assert.Equal("http", row.Definition["settings"]!["transport"]!.GetValue<string>());
        Assert.Equal(1700000001L, row.Value["metadata"]!["created_at"]!.GetValue<long>());
        Assert.Equal(1700000002L, row.Value["metadata"]!["last_used"]!.GetValue<long>());
        Assert.True(ExportService.TestCockpitExportShape(row.Raw));
    }

    [Fact]
    public void ConvertAccountRejectsConflictingAliasesWithoutLeakingEitherKey()
    {
        const string first = "SIMULATED-KEY-ONE";
        const string second = "SIMULATED-KEY-TWO";
        var account = new JsonObject
        {
            ["auth_mode"] = "apikey",
            ["openai_api_key"] = first,
            ["OPENAI_API_KEY"] = second,
            ["api_base_url"] = "https://api.example.test/v1",
            ["api_provider_id"] = "provider-test",
            ["api_provider_name"] = "Test API",
            ["api_model_catalog"] = new JsonArray("model-test"),
            ["api_wire_api"] = "chat_completions",
        };

        var error = Assert.Throws<AccountManager.Core.OpenCode.OpenCodeException>(
            () => ImportService.ConvertAccount(account, "chatgpt-browser"));

        Assert.DoesNotContain(first, error.Message);
        Assert.DoesNotContain(second, error.Message);
    }

    [Fact]
    public void ConvertAccountRejectsBlankApiKeyAliasesSafely()
    {
        var account = new JsonObject
        {
            ["auth_mode"] = "apikey",
            ["OPENAI_API_KEY"] = " ",
            ["key"] = string.Empty,
            ["api_base_url"] = "https://api.example.test/v1",
            ["api_provider_id"] = "provider-test",
            ["api_provider_name"] = "Test API",
            ["api_model_catalog"] = new JsonArray("model-test"),
            ["api_wire_api"] = "responses",
        };

        var error = Assert.Throws<AccountManager.Core.OpenCode.OpenCodeException>(
            () => ImportService.ConvertAccount(account, "chatgpt-browser"));

        Assert.Contains("缺少 openai_api_key", error.Message);
        Assert.DoesNotContain("SIMULATED", error.Message);
    }

    [Fact]
    public void ConvertAccountStillRejectsMissingApiKey()
    {
        var account = new JsonObject
        {
            ["auth_mode"] = "apikey",
            ["api_base_url"] = "https://ark.cn-beijing.volces.com/api/coding/v3",
            ["api_provider_id"] = "volcengine_agentplan",
        };

        Assert.Throws<AccountManager.Core.OpenCode.OpenCodeException>(
            () => ImportService.ConvertAccount(account, "chatgpt-browser"));
    }
}
