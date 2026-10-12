using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

public class CredentialAndMapperTests
{
    private static string Base64Url(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static JsonObject Token(string accountId, string email, long exp)
    {
        var header = Base64Url("""{"alg":"none"}""");
        var payloadObject = new JsonObject
        {
            ["exp"] = exp,
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = accountId },
            ["https://api.openai.com/profile"] = new JsonObject { ["email"] = email },
        };
        var payload = Base64Url(payloadObject.ToJsonString());
        return new JsonObject
        {
            ["type"] = "oauth",
            ["methodID"] = "chatgpt-browser",
            ["access"] = header + "." + payload + ".sig",
            ["metadata"] = new JsonObject { ["accountID"] = accountId, ["cockpitEmail"] = email },
        };
    }

    [Fact]
    public void IdentityRequiresBothAccountIdAndEmail()
    {
        var full = CredentialHelpers.GetIdentityInfo(Token("acct_1234567890", "a@b.com", 4102444800));
        Assert.Equal("acct_1234567890|a@b.com", full.Identity);

        var onlyMetadataId = new JsonObject { ["type"] = "oauth", ["metadata"] = new JsonObject { ["accountID"] = "acct_1" } };
        Assert.Equal(string.Empty, CredentialHelpers.GetIdentityInfo(onlyMetadataId).Identity);
    }

    [Fact]
    public void ExpiryPrefersJwtExpOverValueExpires()
    {
        var value = Token("acct_1234567890", "a@b.com", 4102444800);
        value["expires"] = 1_000; // must be ignored because the JWT exp is valid
        var expiry = CredentialHelpers.GetTokenExpiryMs(value);
        Assert.True(expiry.FromJwt);
        Assert.Equal(4102444800_000, expiry.ExpiresMs);
    }

    [Fact]
    public void CodexMethodDetection()
    {
        Assert.True(CredentialHelpers.IsCodexOAuthMethod("chatgpt-browser"));
        Assert.True(CredentialHelpers.IsCodexOAuthMethod("chatgpt"));
        Assert.False(CredentialHelpers.IsCodexOAuthMethod("anthropic"));
        Assert.False(CredentialHelpers.IsCodexOAuthMethod(null));
    }

    [Fact]
    public void SafeBaseUrlStripsCredentialsQueryAndFragment()
    {
        var mapper = new AccountMapper(new SecretRegistry(applyLongValueFilter: false));
        Assert.Equal("https://api.example.com/v1", mapper.SafeBaseUrl("https://user:pass@api.example.com/v1?key=abc#frag"));
        Assert.Equal("地址无法安全显示", mapper.SafeBaseUrl("http://api.example.com"));
        Assert.Equal("地址未配置", mapper.SafeBaseUrl(" "));
    }

    [Fact]
    public void SubscriptionExpiryReadsMetadataEpochAndIso()
    {
        var epoch = new JsonObject { ["metadata"] = new JsonObject { ["subscriptionActiveUntil"] = 4102444800L } };
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), AccountMapper.GetSubscriptionExpiry(epoch));

        var iso = new JsonObject { ["metadata"] = new JsonObject { ["subscription_active_until"] = "2030-01-02T03:04:05Z" } };
        Assert.Equal(DateTimeOffset.Parse("2030-01-02T03:04:05Z"), AccountMapper.GetSubscriptionExpiry(iso));

        var none = new JsonObject { ["metadata"] = new JsonObject() };
        Assert.Null(AccountMapper.GetSubscriptionExpiry(none));
    }

    [Fact]
    public void MapperCarriesSubscriptionExpiry()
    {
        var value = Token("acct_1234567890", "a@b.com", 4102444800);
        value["metadata"]!["subscriptionActiveUntil"] = 4102444800L;
        var credential = new JsonObject
        {
            ["id"] = "cred-1",
            ["integrationID"] = "openai",
            ["label"] = "a@b.com",
            ["value"] = value,
        };
        var row = new AccountMapper(new SecretRegistry(applyLongValueFilter: false)).ConvertCredential(credential);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), row.SubscriptionExpires);
    }

    [Fact]
    public void MapperUsesCacheFallbackWhenMetadataMissing()
    {
        var value = Token("acct_1234567890", "a@b.com", 4102444800);
        var credential = new JsonObject
        {
            ["id"] = "cred-1",
            ["integrationID"] = "openai",
            ["label"] = "a@b.com",
            ["value"] = value,
        };
        var fallback = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["acct_1234567890|a@b.com"] = "2030-01-02T03:04:05+00:00",
        };

        var row = new AccountMapper(new SecretRegistry(applyLongValueFilter: false))
            .ConvertCredential(credential, fallback);

        Assert.Equal(DateTimeOffset.Parse("2030-01-02T03:04:05+00:00"), row.SubscriptionExpires);
    }

    [Fact]
    public void MapperUsesTheLaterOfMetadataAndCache()
    {
        var value = Token("acct_1234567890", "a@b.com", 4102444800);
        value["metadata"]!["subscriptionActiveUntil"] = "2030-01-02T03:04:05+00:00";
        var credential = new JsonObject
        {
            ["id"] = "cred-1",
            ["integrationID"] = "openai",
            ["label"] = "a@b.com",
            ["value"] = value,
        };
        var mapper = new AccountMapper(new SecretRegistry(applyLongValueFilter: false));

        // A refreshed cache newer than the stale metadata wins, so a live refresh shows immediately.
        var newerFallback = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["acct_1234567890|a@b.com"] = "2031-01-02T03:04:05+00:00",
        };
        Assert.Equal(
            DateTimeOffset.Parse("2031-01-02T03:04:05+00:00"),
            mapper.ConvertCredential(credential, newerFallback).SubscriptionExpires);

        // The opposite direction still keeps the newer metadata.
        var olderFallback = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["acct_1234567890|a@b.com"] = "2029-01-02T03:04:05+00:00",
        };
        Assert.Equal(
            DateTimeOffset.Parse("2030-01-02T03:04:05+00:00"),
            mapper.ConvertCredential(credential, olderFallback).SubscriptionExpires);
    }

    [Fact]
    public void MapperAuthoritativeEmptySuppressesStaleMetadata()
    {
        var value = Token("acct_1234567890", "a@b.com", 4102444800);
        value["metadata"]!["subscriptionActiveUntil"] = "2030-01-02T03:04:05+00:00";
        var credential = new JsonObject
        {
            ["id"] = "cred-1",
            ["integrationID"] = "openai",
            ["label"] = "a@b.com",
            ["value"] = value,
        };
        // Empty string = a live lookup authoritatively found no subscription; it must beat the stale
        // metadata date so the row shows "—" rather than a value that no longer applies.
        var cleared = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["acct_1234567890|a@b.com"] = string.Empty,
        };

        var row = new AccountMapper(new SecretRegistry(applyLongValueFilter: false))
            .ConvertCredential(credential, cleared);

        Assert.Null(row.SubscriptionExpires);
    }

    [Fact]
    public void SecretRegistryRedactsRegisteredValues()
    {
        var registry = new SecretRegistry(applyLongValueFilter: false);
        registry.RegisterValue("super-secret-key");
        Assert.Equal("token=[REDACTED]", registry.Redact("token=super-secret-key"));
    }

    [Fact]
    public void SecretRegistryRegistersNestedSecretFields()
    {
        var registry = new SecretRegistry(applyLongValueFilter: false);
        registry.Register(JsonNode.Parse("""{"info":{"api_key":"abc123","nested":{"access":"xyz789"}}}"""));
        Assert.Equal("[REDACTED]", registry.Redact("abc123"));
        Assert.Equal("[REDACTED]", registry.Redact("xyz789"));
    }

    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("ACCESS_TOKEN")]
    [InlineData("twoFactorSecret")]
    [InlineData("accountTwoFactorSecret")]
    [InlineData("accountPassword")]
    [InlineData("password")]
    [InlineData("agentPrivateKey")]
    [InlineData("agent_private_key")]
    [InlineData("egress_proxy_url")]
    public void CockpitSecretAliasesAreRegisteredCaseInsensitivelyBeforeNormalization(string field)
    {
        var registry = new SecretRegistry(applyLongValueFilter: false);
        registry.Register(new JsonObject { ["nested"] = new JsonObject { [field] = "short-test-value" } });
        Assert.Equal("[REDACTED]", registry.Redact("short-test-value"));
    }
}
