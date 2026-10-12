using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Export;
using AccountManager.Core.Logging;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>
/// Guards the full-Cockpit-mirror export path: a cached (secret-free) Cockpit entry becomes the
/// base of the exported row so client state / quota history survive, while the live identity,
/// tokens/keys and subscription data are overlaid from the current credential.
/// </summary>
public class ExportMirrorTests
{
    [Fact]
    public void MirrorKeepsCachedFieldsAndRefreshesSecrets()
    {
        var portable = new JsonObject
        {
            ["id"] = "cred-1",
            ["email"] = "a@b.com",
            ["auth_mode"] = "oauth",
            ["account_id"] = "acc-1",
            ["plan_type"] = "plus",
            ["subscription_active_until"] = "2026-10-28T11:53:22+00:00",
            ["tokens"] = new JsonObject
            {
                ["id_token"] = "id-new",
                ["access_token"] = "acc-new",
                ["refresh_token"] = "ref-new",
            },
        };
        var cached = new JsonObject
        {
            ["id"] = "codex_abc",
            ["email"] = "a@b.com",
            ["auth_mode"] = "oauth",
            ["account_id"] = "acc-1",
            ["plan_type"] = "pro",
            ["api_supports_vision"] = true,
            ["api_model_vision_support"] = new JsonObject { ["gpt-5"] = true },
            ["quota"] = new JsonObject
            {
                ["hourly_percentage"] = 15,
                ["hourly_reset_time"] = 1234567000L,
                ["weekly_percentage"] = 42,
                ["weekly_reset_time"] = 1234567999L,
            },
            ["created_at"] = 1234567890L,
            ["last_used"] = 1234567999L,
        };

        var merged = ExportService.MergeCockpitMirror(portable, cached, oauth: true);

        Assert.Equal("codex_abc", merged["id"]!.GetValue<string>()); // original Cockpit id kept
        Assert.True(merged["api_supports_vision"]!.GetValue<bool>()); // cached client state kept
        Assert.Equal(42, merged["quota"]!["weekly_percentage"]!.GetValue<int>()); // cached history kept
        Assert.Equal("plus", merged["plan_type"]!.GetValue<string>()); // live plan refreshed
        Assert.Equal("acc-new", merged["tokens"]!["access_token"]!.GetValue<string>()); // live token overlaid
        Assert.Equal(1234567890, merged["created_at"]!.GetValue<long>()); // cached timestamps kept
        Assert.Equal(1234567999, merged["last_used"]!.GetValue<long>()); // last-used time preserved too

        // These current Cockpit capability/history fields are part of the native serde shape.
        Assert.True(ExportService.TestCockpitExportShape(merged, mirrored: true), SchemaFailure(merged));
        Assert.True(ExportService.TestCockpitExportShape(merged));
    }

    [Fact]
    public void MirrorKeepsCachedApiProviderFieldsAndRefreshesKey()
    {
        var portable = new JsonObject
        {
            ["id"] = "cred-2",
            ["email"] = string.Empty,
            ["auth_mode"] = "apikey",
            ["openai_api_key"] = "sk-new",
            ["api_base_url"] = "https://api.example.com/",
            ["api_provider_mode"] = "custom",
            ["api_provider_id"] = "cockpit-abc",
            ["api_provider_name"] = "My API",
            ["api_model_catalog"] = new JsonArray { "gpt-5" },
            ["api_wire_api"] = "chat_completions",
            ["tokens"] = new JsonObject { ["id_token"] = string.Empty, ["access_token"] = string.Empty },
            ["created_at"] = 10L,
            ["last_used"] = 20L,
        };
        var cached = new JsonObject
        {
            ["id"] = "codex_apikey_x",
            ["email"] = "api-key-x",
            ["auth_mode"] = "apikey",
            ["api_provider_id"] = "cockpit-abc",
            ["api_supports_vision"] = true,
            ["api_sync_model_catalog_to_codex"] = false,
        };

        var merged = ExportService.MergeCockpitMirror(portable, cached, oauth: false);

        Assert.Equal("sk-new", merged["openai_api_key"]!.GetValue<string>());
        Assert.Equal("codex_apikey_x", merged["id"]!.GetValue<string>());
        Assert.Equal("api-key-x", merged["email"]!.GetValue<string>()); // cached email preserved
        Assert.Equal("cockpit-abc", merged["api_provider_id"]!.GetValue<string>());
        Assert.True(merged["api_supports_vision"]!.GetValue<bool>());
        Assert.True(merged.ContainsKey("api_sync_model_catalog_to_codex"));
        Assert.NotNull(merged["tokens"]); // shape parity: schema always carries tokens
        Assert.True(ExportService.TestCockpitExportShape(merged, mirrored: true), SchemaFailure(merged));
    }

    [Fact]
    public void MirrorKeepsHistoricalFieldsButUsesLiveApiSettingsAndRemovesOldAliases()
    {
        var currentModels = new JsonArray();
        for (var i = 0; i < 7; i++) { currentModels.Add((JsonNode?)("current-model-" + i)); }
        var portable = new JsonObject
        {
            ["id"] = "cred-api",
            ["email"] = string.Empty,
            ["auth_mode"] = "apikey",
            ["openai_api_key"] = "SIMULATED-LIVE-KEY",
            ["api_base_url"] = "https://live.example.test/v1",
            ["api_provider_mode"] = "custom",
            ["api_provider_id"] = "live-provider",
            ["api_provider_name"] = "Live Provider",
            ["api_model_catalog"] = currentModels,
            ["api_wire_api"] = "chat_completions",
            ["tokens"] = new JsonObject { ["id_token"] = string.Empty, ["access_token"] = string.Empty },
            ["created_at"] = 123L,
            ["last_used"] = 456L,
        };
        var staleModels = new JsonArray();
        for (var i = 0; i < 9; i++) { staleModels.Add((JsonNode?)("stale-model-" + i)); }
        var cached = new JsonObject
        {
            ["id"] = "cockpit-api",
            ["email"] = "api-account",
            ["auth_mode"] = "apikey",
            ["OPENAI_API_KEY"] = "SIMULATED-OLD-ALIAS-KEY",
            ["api_key"] = "SIMULATED-OLD-GENERIC-KEY",
            ["api_base_url"] = "https://stale.example.test",
            ["api_provider_mode"] = "custom",
            ["api_provider_id"] = "stale-provider",
            ["api_provider_name"] = "Stale Provider",
            ["api_model_catalog"] = staleModels,
            ["api_wire_api"] = "responses",
            ["api_supports_websockets"] = true,
            ["api_supports_vision"] = true,
            ["api_model_vision_support"] = new JsonObject { ["source-model"] = true },
            ["created_at"] = 0L,
            ["last_used"] = 0L,
            ["account_note"] = "keep this note",
            ["quota"] = new JsonObject
            {
                ["hourly_percentage"] = 1,
                ["weekly_percentage"] = 2,
            },
        };

        var merged = ExportService.MergeCockpitMirror(portable, cached, oauth: false);

        Assert.Equal("SIMULATED-LIVE-KEY", merged["openai_api_key"]!.GetValue<string>());
        Assert.Null(merged["OPENAI_API_KEY"]);
        Assert.Null(merged["api_key"]);
        Assert.Equal("https://live.example.test/v1", merged["api_base_url"]!.GetValue<string>());
        Assert.Equal("Live Provider", merged["api_provider_name"]!.GetValue<string>());
        Assert.Equal("live-provider", merged["api_provider_id"]!.GetValue<string>());
        Assert.Equal("chat_completions", merged["api_wire_api"]!.GetValue<string>());
        Assert.Equal(123L, merged["created_at"]!.GetValue<long>());
        Assert.Equal(456L, merged["last_used"]!.GetValue<long>());
        Assert.Equal(7, ((JsonArray)merged["api_model_catalog"]!).Count);
        Assert.False(merged["api_supports_websockets"]!.GetValue<bool>());
        Assert.True(merged["api_supports_vision"]!.GetValue<bool>()); // actual source capability retained
        Assert.Equal("keep this note", merged["account_note"]!.GetValue<string>());
        Assert.Equal(2, merged["quota"]!["weekly_percentage"]!.GetValue<int>());
        Assert.DoesNotContain("SIMULATED-OLD-ALIAS-KEY", merged.ToJsonString());
        Assert.DoesNotContain("SIMULATED-OLD-GENERIC-KEY", merged.ToJsonString());
        Assert.True(ExportService.TestCockpitExportShape(merged, mirrored: true), SchemaFailure(merged));
    }

    [Fact]
    public void NewPortableApiDoesNotInventCapabilitiesAndNativeSchemaRequiresInt64Times()
    {
        var account = NativeApiAccount();
        Assert.True(ExportService.TestCockpitExportShape(account));
        Assert.Null(account["api_supports_websockets"]);
        Assert.Null(account["api_supports_vision"]);

        account.Remove("created_at");
        Assert.False(ExportService.TestCockpitExportShape(account));
        account["created_at"] = "123";
        Assert.False(ExportService.TestCockpitExportShape(account));
        account["created_at"] = 123.5;
        Assert.False(ExportService.TestCockpitExportShape(account));
    }

    [Fact]
    public void NativeOAuthSchemaAllowsEmptyIdTokenButRequiresTokenObjectAndStringFields()
    {
        var account = new JsonObject
        {
            ["id"] = "oauth-id",
            ["email"] = "test@example.invalid",
            ["auth_mode"] = "oauth",
            ["account_id"] = "account-test",
            ["tokens"] = new JsonObject
            {
                ["id_token"] = string.Empty,
                ["access_token"] = "SIMULATED-OAUTH-ACCESS",
                ["refresh_token"] = "SIMULATED-OAUTH-REFRESH",
            },
            ["created_at"] = 123L,
            ["last_used"] = 456L,
        };
        Assert.True(ExportService.TestCockpitExportShape(account));

        account["tokens"]!["id_token"] = 123;
        Assert.False(ExportService.TestCockpitExportShape(account));
        account["tokens"] = null;
        Assert.False(ExportService.TestCockpitExportShape(account));
    }

    [Fact]
    public void NativeSchemaRejectsInvalidEnumsBooleansContainersAndKeyAliases()
    {
        var account = NativeApiAccount();
        account["api_supports_websockets"] = "true";
        Assert.False(ExportService.TestCockpitExportShape(account));

        account["api_supports_websockets"] = true;
        account["app_speed"] = "turbo";
        Assert.False(ExportService.TestCockpitExportShape(account));

        account.Remove("app_speed");
        account["api_model_vision_support"] = new JsonArray("gpt-5");
        Assert.False(ExportService.TestCockpitExportShape(account));

        account.Remove("api_model_vision_support");
        account["OPENAI_API_KEY"] = "SIMULATED-ALIAS";
        Assert.False(ExportService.TestCockpitExportShape(account));

        account.Remove("OPENAI_API_KEY");
        account["api_provider_mode"] = "unknown_mode";
        Assert.False(ExportService.TestCockpitExportShape(account));

        account["api_provider_mode"] = "custom";
        account["api_wire_api"] = "unknown_protocol";
        Assert.False(ExportService.TestCockpitExportShape(account));

        account["api_wire_api"] = "responses";
        account["token_generation"] = -1;
        Assert.False(ExportService.TestCockpitExportShape(account));
    }

    [Fact]
    public void NativeSchemaAcceptsSerdeI32LongNodesAndDefaultedAgentFedrampBool()
    {
        var account = NativeApiAccount();
        account["quota"] = new JsonObject
        {
            ["hourly_percentage"] = 50L,
            ["weekly_percentage"] = 75L,
        };
        account["agent_identity"] = new JsonObject
        {
            ["agent_runtime_id"] = "SIMULATED-RUNTIME",
            ["agent_private_key"] = "SIMULATED-AGENT-KEY",
            ["account_id"] = "SIMULATED-ACCOUNT",
            ["chatgpt_user_id"] = "SIMULATED-USER",
        };
        Assert.True(ExportService.TestCockpitExportShape(account));

        account["quota"]!["hourly_percentage"] = 50.5;
        Assert.False(ExportService.TestCockpitExportShape(account));
        account["quota"]!["hourly_percentage"] = (long)int.MaxValue + 1;
        Assert.False(ExportService.TestCockpitExportShape(account));

        account["quota"]!["hourly_percentage"] = 50L;
        account["agent_identity"]!["chatgpt_account_is_fedramp"] = "false";
        Assert.False(ExportService.TestCockpitExportShape(account));
    }

    [Fact]
    public void ExportRestoresSealedSourceMetadataButUsesOnlyLiveTokensAndApiKey()
    {
        var raw = NativeApiAccount();
        raw["openai_api_key"] = "SIMULATED-STALE-KEY";
        raw["twoFactorSecret"] = "SIMULATED-2FA";
        raw["accountPassword"] = "SIMULATED-PASSWORD";
        raw["agentIdentity"] = new JsonObject
        {
            ["agentRuntimeId"] = "SIMULATED-RUNTIME",
            ["agentPrivateKey"] = "SIMULATED-AGENT-PRIVATE-KEY",
            ["accountId"] = "SIMULATED-AGENT-ACCOUNT",
            ["chatgptUserId"] = "SIMULATED-USER",
        };
        raw["egress_proxy_url"] = "https://SIMULATED-PROXY-USER:SIMULATED-PROXY-PASSWORD@proxy.example.invalid";
        var (cacheEntry, secrets) = SubscriptionStore.SplitSecrets(raw);
        var record = new SubscriptionRecord
        {
            AccountId = "test-provider",
            Email = "api-account",
            Cockpit = cacheEntry,
            SealedSecrets = SubscriptionStore.SealSecrets(secrets),
        };
        var sourceMirror = SubscriptionStore.GetExportMirror(record);
        var live = NativeApiAccount();
        live["openai_api_key"] = "SIMULATED-LIVE-KEY";
        live["api_base_url"] = "https://live.example.invalid/v1";
        live["tokens"] = new JsonObject
        {
            ["id_token"] = string.Empty,
            ["access_token"] = string.Empty,
        };

        var merged = ExportService.MergeCockpitMirror(live, sourceMirror, oauth: false);

        Assert.Equal("SIMULATED-2FA", merged["two_factor_secret"]!.GetValue<string>());
        Assert.Equal("SIMULATED-PASSWORD", merged["account_password"]!.GetValue<string>());
        Assert.Equal("SIMULATED-AGENT-PRIVATE-KEY", merged["agent_identity"]!["agent_private_key"]!.GetValue<string>());
        Assert.False(merged["agent_identity"]!.AsObject().ContainsKey("agentPrivateKey"));
        Assert.Equal("https://SIMULATED-PROXY-USER:SIMULATED-PROXY-PASSWORD@proxy.example.invalid", merged["egress_proxy"]!.GetValue<string>());
        Assert.Equal("SIMULATED-LIVE-KEY", merged["openai_api_key"]!.GetValue<string>());
        Assert.Null(merged["OPENAI_API_KEY"]);
        Assert.True(ExportService.TestCockpitExportShape(merged, mirrored: true), SchemaFailure(merged));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CachedHistoryExportUsesLiveSettingsOrAbortsWhenInvalid(bool invalidHistory)
    {
        var testDirectory = TestDirectory();
        Directory.CreateDirectory(testDirectory);
        try
        {
            var credentials = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "credential-test",
                    ["integrationID"] = "provider-live",
                    ["value"] = new JsonObject
                    {
                        ["type"] = "key",
                        ["key"] = "SIMULATED-LIVE-API-KEY",
                        ["metadata"] = new JsonObject { ["cockpitID"] = "provider-source" },
                    },
                },
            };
            var configEntries = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "file",
                    ["info"] = new JsonObject
                    {
                        ["providers"] = new JsonObject
                        {
                            ["provider-live"] = new JsonObject
                            {
                                ["name"] = "Live Provider",
                                ["package"] = OpenAiApiProtocol.ResponsesPackage,
                                ["settings"] = new JsonObject { ["baseURL"] = "https://live.example.invalid/v1" },
                                ["models"] = new JsonObject { ["live-model"] = new JsonObject { ["name"] = "Live Model" } },
                            },
                        },
                    },
                },
            };
            var cache = new SubscriptionStore(_ => Task.FromResult(Path.Combine(testDirectory, "cache")));
            await cache.SaveAsync(new[]
            {
                new SubscriptionRecord
                {
                    AccountId = "provider-source",
                    Email = "api-account",
                    Cockpit = new JsonObject
                    {
                        ["id"] = "source-account",
                        ["email"] = "api-account",
                        ["auth_mode"] = "apikey",
                        ["api_base_url"] = "https://old.example.invalid/v1",
                        ["api_provider_mode"] = "custom",
                        ["api_provider_id"] = "provider-source",
                        ["api_provider_name"] = "Source Provider",
                        ["api_model_catalog"] = new JsonArray("source-model"),
                        ["api_wire_api"] = "responses",
                        ["quota"] = new JsonObject
                        {
                            ["hourly_percentage"] = invalidHistory ? JsonValue.Create("malformed") : JsonValue.Create(25),
                            ["weekly_percentage"] = 50,
                        },
                        ["created_at"] = 100L,
                        ["last_used"] = 200L,
                    },
                },
            });

            var secrets = new SecretRegistry(applyLongValueFilter: false);
            var exporter = new ExportService(new ExportApi(credentials, configEntries), secrets,
                new AppLog(secrets, Path.Combine(testDirectory, "logs")), subscriptions: cache);
            var target = Path.Combine(testDirectory, "must-not-exist.json");

            if (invalidHistory)
            {
                var error = await Assert.ThrowsAsync<OpenCodeException>(
                    () => exporter.ExportAsync(new[] { "credential-test" }, target));

                Assert.Contains("取消导出", error.Message);
                Assert.DoesNotContain("SIMULATED", error.Message);
                Assert.False(File.Exists(target));
            }
            else
            {
                var result = await exporter.ExportAsync(new[] { "credential-test" }, target);
                var exported = JsonNode.Parse(await File.ReadAllTextAsync(target))!.AsArray().Single()!;
                Assert.Equal("SIMULATED-LIVE-API-KEY", exported["openai_api_key"]!.GetValue<string>());
                Assert.Equal("https://live.example.invalid/v1", exported["api_base_url"]!.GetValue<string>());
                Assert.Equal("Live Provider", exported["api_provider_name"]!.GetValue<string>());
                Assert.Equal("responses", exported["api_wire_api"]!.GetValue<string>());
                Assert.Equal("live-model", exported["api_model_catalog"]![0]!.GetValue<string>());
                Assert.Equal(50, exported["quota"]!["weekly_percentage"]!.GetValue<int>());
                Assert.DoesNotContain("Cockpit 镜像", result.Warning);
                Assert.True(ExportService.TestCockpitExportShape(exported));
            }
        }
        finally
        {
            if (Directory.Exists(testDirectory)) { Directory.Delete(testDirectory, recursive: true); }
        }
    }

    private static JsonObject NativeApiAccount() => new()
    {
        ["id"] = "api-id",
        ["email"] = string.Empty,
        ["auth_mode"] = "apikey",
        ["openai_api_key"] = "SIMULATED-TEST-KEY",
        ["api_base_url"] = "https://api.example.test/v1",
        ["api_provider_mode"] = "custom",
        ["api_provider_id"] = "test-provider",
        ["api_provider_name"] = "Test Provider",
        ["api_model_catalog"] = new JsonArray("gpt-5"),
        ["api_wire_api"] = "responses",
        ["tokens"] = new JsonObject { ["id_token"] = string.Empty, ["access_token"] = string.Empty },
        ["created_at"] = 123L,
        ["last_used"] = 456L,
    };

    private static string SchemaFailure(JsonObject account)
        => CockpitAccountSchema.GetInvalidReason(account) ?? "unknown shape issue";

    private static string TestDirectory()
    {
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode")
            : Path.GetTempPath();
        return Path.Combine(root, "am-export-mirror-" + Guid.NewGuid().ToString("N"));
    }

    private sealed class ExportApi(JsonArray credentials, JsonArray configEntries) : IOpenCodeApi
    {
        public Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((JsonArray)credentials.DeepClone());

        public Task<JsonArray> GetConfigEntriesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((JsonArray)configEntries.DeepClone());

        public Task<JsonNode?> GetIntegrationAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate,
            CancellationToken cancellationToken = default, string? requestedId = null)
            => throw new NotSupportedException();

        public Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public void FindCachedRecordMatchesOAuthByIdentityAndApiByCockpitId()
    {
        var oauthRecord = new SubscriptionRecord
        {
            AccountId = "acc-1",
            Email = "a@b.com",
            Cockpit = new JsonObject { ["auth_mode"] = "oauth", ["id"] = "codex_1" },
        };
        var apiRecord = new SubscriptionRecord
        {
            AccountId = "cockpit-xyz",
            Email = "api-key-z",
            Cockpit = new JsonObject { ["auth_mode"] = "apikey", ["id"] = "codex_apikey_z", ["api_provider_id"] = "cockpit-xyz" },
        };
        var records = new List<SubscriptionRecord> { oauthRecord, apiRecord };

        var oauthCredential = new JsonObject
        {
            ["value"] = new JsonObject
            {
                ["type"] = "oauth",
                ["metadata"] = new JsonObject { ["accountID"] = "acc-1", ["cockpitEmail"] = "A@B.COM" },
            },
        };
        Assert.Same(oauthRecord, ExportService.FindCachedRecord(oauthCredential, new JsonObject(), records));

        var apiCredential = new JsonObject
        {
            ["value"] = new JsonObject
            {
                ["type"] = "key",
                ["metadata"] = new JsonObject { ["cockpitID"] = "cockpit-xyz" },
            },
        };
        Assert.Same(apiRecord, ExportService.FindCachedRecord(apiCredential, new JsonObject(), records));

        var unmatched = new JsonObject
        {
            ["value"] = new JsonObject
            {
                ["type"] = "key",
                ["metadata"] = new JsonObject { ["cockpitID"] = "cockpit-none" },
            },
        };
        Assert.Null(ExportService.FindCachedRecord(unmatched, new JsonObject(), records));
    }

    [Fact]
    public void FindCachedRecordFallsBackToApiBaseUrlWhenCockpitIdIsStale()
    {
        // After a subscription-only import the live API credential can still carry an older
        // cockpitID (cockpit-…) while the cache key uses the original source id (volcengine_…).
        // The base URL plus matching sealed Key is required; URL alone is not an identity.
        var cached = new SubscriptionRecord
        {
            AccountId = "volcengine_agentplan",
            Email = "api-key-19c8b9e1",
            SealedSecrets = SubscriptionStore.SealSecrets(new JsonObject { ["openai_api_key"] = "SIMULATED-SAME-KEY" }),
            Cockpit = new JsonObject
            {
                ["auth_mode"] = "apikey",
                ["id"] = "codex_apikey_19c8b9e1",
                ["api_provider_id"] = "volcengine_agentplan",
                ["api_base_url"] = "https://ark.cn-beijing.volces.com/api/coding/v3",
                ["api_supports_vision"] = true,
            },
        };
        var records = new List<SubscriptionRecord> { cached };

        var credential = new JsonObject
        {
            ["value"] = new JsonObject
            {
                ["type"] = "key",
                ["key"] = "SIMULATED-SAME-KEY",
                ["metadata"] = new JsonObject { ["cockpitID"] = "cockpit-stale" },
            },
        };
        var portable = new JsonObject
        {
            ["auth_mode"] = "apikey",
            ["api_base_url"] = "https://ark.cn-beijing.volces.com/api/coding/v3/",
        };

        Assert.Same(cached, ExportService.FindCachedRecord(credential, portable, records));
        credential["value"]!["key"] = "SIMULATED-DIFFERENT-KEY";
        Assert.Null(ExportService.FindCachedRecord(credential, portable, records));
        credential["value"]!["key"] = "SIMULATED-SAME-KEY";
        records.Add(cached);
        Assert.Null(ExportService.FindCachedRecord(credential, portable, records));
    }
}
