using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using Xunit;

namespace AccountManager.Core.Tests;

public class SubscriptionStoreTests
{
    [Fact]
    public async Task StoreRoundTripsFullEntryAndSealsSecrets()
    {
        var dir = TestDirectory();
        try
        {
            var raw = new JsonObject
            {
                ["auth_mode"] = "oauth",
                ["email"] = "A@B.com",
                ["account_id"] = "acct_1",
                ["plan_type"] = "team",
                ["subscription_active_until"] = "2030-01-02T03:04:05+00:00",
                ["organization_id"] = "org-1",
                ["token_generation"] = 7,
                ["tokens"] = new JsonObject
                {
                    ["access_token"] = "ACCESS-SECRET",
                    ["ACCESS_TOKEN"] = "ACCESS-SECRET",
                    ["refresh_token"] = "REFRESH-SECRET",
                    ["id_token"] = "ID-SECRET",
                },
                ["openai_api_key"] = "API-SECRET",
                ["OPENAI_API_KEY"] = "API-SECRET",
                ["REFRESH_TOKEN"] = "DIRECT-REFRESH-SECRET",
                ["twoFactorSecret"] = "SIMULATED-2FA-SECRET",
                ["accountPassword"] = "SIMULATED-ACCOUNT-PASSWORD",
                ["agentIdentity"] = new JsonObject
                {
                    ["agentRuntimeId"] = "SIMULATED-RUNTIME-ID",
                    ["agentPrivateKey"] = "SIMULATED-AGENT-PRIVATE-KEY",
                    ["accountId"] = "SIMULATED-AGENT-ACCOUNT",
                    ["chatgptUserId"] = "SIMULATED-CHATGPT-USER",
                },
                ["egress_proxy_url"] = "https://SIMULATED-USER:SIMULATED-PASSWORD@proxy.example.invalid:8443",
            };

            var (cockpit, secrets) = SubscriptionStore.SplitSecrets(raw);
            var record = new SubscriptionRecord
            {
                AccountId = "acct_1",
                Email = "A@B.com",
                ActiveUntil = "2030-01-02T03:04:05+00:00",
                PlanType = "team",
                Source = "import",
                Cockpit = cockpit,
                SealedSecrets = SubscriptionStore.SealSecrets(secrets),
            };

            var store = new SubscriptionStore(_ => Task.FromResult(dir));
            await store.SaveAsync(new[] { record });

            var loaded = await store.LoadAsync();
            var again = loaded["acct_1|a@b.com"];
            Assert.Equal("2030-01-02T03:04:05+00:00", again.ActiveUntil);
            Assert.Equal("org-1", again.Cockpit!["organization_id"]!.GetValue<string>());
            Assert.Equal(7, again.Cockpit!["token_generation"]!.GetValue<int>());
            Assert.Null(again.Cockpit!["tokens"]);
            Assert.Null(again.Cockpit!["openai_api_key"]);
            Assert.Null(again.Cockpit["OPENAI_API_KEY"]);
            Assert.Null(again.Cockpit["two_factor_secret"]);
            Assert.Null(again.Cockpit["account_password"]);
            Assert.Null(again.Cockpit["agent_identity"]);
            Assert.Null(again.Cockpit["egress_proxy"]);

            var file = await File.ReadAllTextAsync(Path.Combine(dir, SubscriptionStore.FileName));
            Assert.DoesNotContain("ACCESS-SECRET", file);
            Assert.DoesNotContain("REFRESH-SECRET", file);
            Assert.DoesNotContain("ID-SECRET", file);
            Assert.DoesNotContain("API-SECRET", file);
            Assert.DoesNotContain("OPENAI_API_KEY", file);
            Assert.DoesNotContain("DIRECT-REFRESH-SECRET", file);
            Assert.DoesNotContain("SIMULATED-2FA-SECRET", file);
            Assert.DoesNotContain("SIMULATED-ACCOUNT-PASSWORD", file);
            Assert.DoesNotContain("SIMULATED-AGENT-PRIVATE-KEY", file);
            Assert.DoesNotContain("SIMULATED-USER:SIMULATED-PASSWORD", file);

            var opened = SubscriptionStore.OpenSecrets(again.SealedSecrets);
            Assert.Equal("ACCESS-SECRET", opened!["tokens"]!["access_token"]!.GetValue<string>());
            Assert.Equal("DIRECT-REFRESH-SECRET", opened["refresh_token"]!.GetValue<string>());
            Assert.Equal("API-SECRET", opened!["openai_api_key"]!.GetValue<string>());
            Assert.Equal("SIMULATED-2FA-SECRET", opened["two_factor_secret"]!.GetValue<string>());
            Assert.Equal("SIMULATED-ACCOUNT-PASSWORD", opened["account_password"]!.GetValue<string>());
            Assert.Equal("SIMULATED-AGENT-PRIVATE-KEY", opened["agent_identity"]!["agent_private_key"]!.GetValue<string>());
            Assert.Equal("https://SIMULATED-USER:SIMULATED-PASSWORD@proxy.example.invalid:8443", opened["egress_proxy"]!.GetValue<string>());

            var exportMirror = SubscriptionStore.GetExportMirror(again);
            Assert.Equal("SIMULATED-2FA-SECRET", exportMirror["two_factor_secret"]!.GetValue<string>());
            Assert.Equal("SIMULATED-ACCOUNT-PASSWORD", exportMirror["account_password"]!.GetValue<string>());
            Assert.Equal("SIMULATED-AGENT-PRIVATE-KEY", exportMirror["agent_identity"]!["agent_private_key"]!.GetValue<string>());
            Assert.Equal("https://SIMULATED-USER:SIMULATED-PASSWORD@proxy.example.invalid:8443", exportMirror["egress_proxy"]!.GetValue<string>());
            Assert.Null(exportMirror["tokens"]); // stale credentials are never restored for export
            Assert.Null(exportMirror["openai_api_key"]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void SplitSecretsRejectsConflictingApiKeyAliasesWithoutExposingValues()
    {
        const string lower = "SIMULATED-KEY-LOWER";
        const string upper = "SIMULATED-KEY-UPPER";
        var raw = new JsonObject
        {
            ["auth_mode"] = "apikey",
            ["openai_api_key"] = lower,
            ["OPENAI_API_KEY"] = upper,
        };

        var error = Assert.Throws<AccountManager.Core.OpenCode.OpenCodeException>(
            () => SubscriptionStore.SplitSecrets(raw));

        Assert.DoesNotContain(lower, error.Message);
        Assert.DoesNotContain(upper, error.Message);
    }

    [Fact]
    public void SplitSecretsRejectsConflictingAdditionalSecretAliasesWithoutExposingValues()
    {
        const string first = "SIMULATED-2FA-ONE";
        const string second = "SIMULATED-2FA-TWO";
        var raw = new JsonObject
        {
            ["two_factor_secret"] = first,
            ["accountTwoFactorSecret"] = second,
        };

        var error = Assert.Throws<AccountManager.Core.OpenCode.OpenCodeException>(
            () => SubscriptionStore.SplitSecrets(raw));

        Assert.DoesNotContain(first, error.Message);
        Assert.DoesNotContain(second, error.Message);
    }

    [Fact]
    public void ConflictedLegacyMirrorIsMarkedInvalidAfterPlaintextAliasesAreRemoved()
    {
        var json = new JsonObject
        {
            ["accounts"] = new JsonObject
            {
                ["provider|api-account"] = new JsonObject
                {
                    ["accountID"] = "provider",
                    ["email"] = "api-account",
                    ["cockpit"] = new JsonObject
                    {
                        ["auth_mode"] = "apikey",
                        ["two_factor_secret"] = "SIMULATED-CONFLICT-ONE",
                        ["accountTwoFactorSecret"] = "SIMULATED-CONFLICT-TWO",
                    },
                },
            },
        };

        var record = SubscriptionStore.Parse(json.ToJsonString())["provider|api-account"];
        var serialized = SubscriptionStore.Serialize(new[] { record });

        Assert.True(record.CockpitMirrorInvalid);
        Assert.Null(record.Cockpit!["two_factor_secret"]);
        Assert.DoesNotContain("SIMULATED-CONFLICT-ONE", serialized);
        Assert.DoesNotContain("SIMULATED-CONFLICT-TWO", serialized);
        Assert.Contains("cockpitMirrorInvalid", serialized);
        var error = Assert.Throws<AccountManager.Core.OpenCode.OpenCodeException>(
            () => SubscriptionStore.GetExportMirror(record));
        Assert.DoesNotContain("SIMULATED-CONFLICT", error.Message);
    }

    [Fact]
    public void ParseMigratesLegacyPlaintextAliasesOutOfCockpitMirror()
    {
        const string testKey = "SIMULATED-LEGACY-PLAINTEXT-KEY";
        var serialized = new JsonObject
        {
            ["version"] = 2,
            ["accounts"] = new JsonObject
            {
                ["source-provider|api-account"] = new JsonObject
                {
                    ["accountID"] = "source-provider",
                    ["email"] = "api-account",
                    ["cockpit"] = new JsonObject
                    {
                        ["id"] = "legacy-api",
                        ["auth_mode"] = "apikey",
                        ["OPENAI_API_KEY"] = testKey,
                        ["tokens"] = new JsonObject
                        {
                            ["ACCESS_TOKEN"] = "SIMULATED-LEGACY-ACCESS",
                            ["id_token"] = string.Empty,
                        },
                        ["twoFactorSecret"] = "SIMULATED-LEGACY-2FA",
                        ["agentIdentity"] = new JsonObject
                        {
                            ["agentRuntimeId"] = "SIMULATED-LEGACY-RUNTIME",
                            ["agentPrivateKey"] = "SIMULATED-LEGACY-PRIVATE-KEY",
                            ["accountId"] = "SIMULATED-LEGACY-ACCOUNT",
                            ["chatgptUserId"] = "SIMULATED-LEGACY-USER",
                        },
                        ["egress_proxy_url"] = "https://SIMULATED-LEGACY-USER:SIMULATED-LEGACY-PASSWORD@proxy.example.invalid",
                    },
                },
            },
        }.ToJsonString();

        var record = SubscriptionStore.Parse(serialized)["source-provider|api-account"];

        Assert.Null(record.Cockpit!["OPENAI_API_KEY"]);
        Assert.Null(record.Cockpit["openai_api_key"]);
        Assert.Null(record.Cockpit["tokens"]);
        Assert.Null(record.Cockpit["two_factor_secret"]);
        Assert.Null(record.Cockpit["agent_identity"]);
        Assert.Null(record.Cockpit["egress_proxy"]);
        var opened = SubscriptionStore.OpenSecrets(record.SealedSecrets);
        Assert.Equal(testKey, opened!["openai_api_key"]!.GetValue<string>());
        Assert.Equal("SIMULATED-LEGACY-ACCESS", opened["tokens"]!["access_token"]!.GetValue<string>());
        Assert.Equal("SIMULATED-LEGACY-2FA", opened["two_factor_secret"]!.GetValue<string>());
        Assert.Equal("SIMULATED-LEGACY-PRIVATE-KEY", opened["agent_identity"]!["agent_private_key"]!.GetValue<string>());
        Assert.Equal("https://SIMULATED-LEGACY-USER:SIMULATED-LEGACY-PASSWORD@proxy.example.invalid", opened["egress_proxy"]!.GetValue<string>());
        Assert.DoesNotContain(testKey, record.Cockpit.ToJsonString());
    }

    [Fact]
    public void SerializeSealsRawSecretAliasesEvenWhenCallerDidNotSplitThem()
    {
        const string testKey = "SIMULATED-UNSPLIT-KEY";
        var serialized = SubscriptionStore.Serialize(new[]
        {
            new SubscriptionRecord
            {
                AccountId = "provider",
                Email = "api-account",
                Cockpit = new JsonObject
                {
                    ["auth_mode"] = "apikey",
                    ["api_key"] = testKey,
                    ["tokens"] = new JsonObject { ["ID_TOKEN"] = "SIMULATED-ID-TOKEN" },
                    ["accountTwoFactorSecret"] = "SIMULATED-SERIALIZE-2FA",
                    ["password"] = "SIMULATED-SERIALIZE-PASSWORD",
                    ["agentIdentity"] = new JsonObject
                    {
                        ["agentRuntimeId"] = "SIMULATED-SERIALIZE-RUNTIME",
                        ["agentPrivateKey"] = "SIMULATED-SERIALIZE-PRIVATE-KEY",
                        ["accountId"] = "SIMULATED-SERIALIZE-ACCOUNT",
                        ["chatgptUserId"] = "SIMULATED-SERIALIZE-USER",
                    },
                    ["egress_proxy_url"] = "https://SIMULATED-SERIALIZE-USER:SIMULATED-SERIALIZE-PASSWORD@proxy.example.invalid",
                },
            },
        });

        Assert.DoesNotContain(testKey, serialized);
        Assert.DoesNotContain("SIMULATED-ID-TOKEN", serialized);
        Assert.DoesNotContain("api_key", serialized);
        Assert.DoesNotContain("SIMULATED-SERIALIZE-2FA", serialized);
        Assert.DoesNotContain("SIMULATED-SERIALIZE-PASSWORD", serialized);
        Assert.DoesNotContain("SIMULATED-SERIALIZE-PRIVATE-KEY", serialized);
        Assert.DoesNotContain("SIMULATED-SERIALIZE-USER:SIMULATED-SERIALIZE-PASSWORD", serialized);
        var record = SubscriptionStore.Parse(serialized)["provider|api-account"];
        var opened = SubscriptionStore.OpenSecrets(record.SealedSecrets);
        Assert.Equal(testKey, opened!["openai_api_key"]!.GetValue<string>());
        Assert.Equal("SIMULATED-ID-TOKEN", opened["tokens"]!["id_token"]!.GetValue<string>());
        Assert.Equal("SIMULATED-SERIALIZE-2FA", opened["two_factor_secret"]!.GetValue<string>());
        Assert.Equal("SIMULATED-SERIALIZE-PASSWORD", opened["account_password"]!.GetValue<string>());
        Assert.Equal("SIMULATED-SERIALIZE-PRIVATE-KEY", opened["agent_identity"]!["agent_private_key"]!.GetValue<string>());
        Assert.Equal("https://SIMULATED-SERIALIZE-USER:SIMULATED-SERIALIZE-PASSWORD@proxy.example.invalid", opened["egress_proxy"]!.GetValue<string>());
    }

    [Fact]
    public void ParseFallsBackToCockpitSubscription()
    {
        const string json = """
        { "version": 2, "accounts": { "acct|a@b.com": {
            "accountID": "acct", "email": "a@b.com",
            "cockpit": { "subscription_active_until": "2031-01-01T00:00:00+00:00" } } } }
        """;

        var parsed = SubscriptionStore.Parse(json);

        Assert.Equal("2031-01-01T00:00:00+00:00", parsed["acct|a@b.com"].ActiveUntil);
    }

    [Fact]
    public async Task LoadActiveUntilSkipsEntriesWithoutExpiry()
    {
        var dir = TestDirectory();
        try
        {
            var store = new SubscriptionStore(_ => Task.FromResult(dir));
            await store.SaveAsync(new[]
            {
                new SubscriptionRecord { AccountId = "has", Email = "a@b.com", ActiveUntil = "2030-01-02T03:04:05+00:00" },
                new SubscriptionRecord { AccountId = "none", Email = "c@d.com" },
            });

            var map = await store.LoadActiveUntilAsync();

            Assert.Equal("2030-01-02T03:04:05+00:00", map["has|a@b.com"]);
            Assert.False(map.ContainsKey("none|c@d.com"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task UpdateActiveUntilRefreshesExistingRecordAndPreservesSecrets()
    {
        var dir = TestDirectory();
        try
        {
            var store = new SubscriptionStore(_ => Task.FromResult(dir));
            var (cockpit, secrets) = SubscriptionStore.SplitSecrets(new JsonObject
            {
                ["email"] = "a@b.com",
                ["account_id"] = "acct",
                ["plan_type"] = "team",
                ["tokens"] = new JsonObject { ["access_token"] = "ACCESS-SECRET" },
            });
            await store.SaveAsync(new[]
            {
                new SubscriptionRecord
                {
                    AccountId = "acct",
                    Email = "a@b.com",
                    ActiveUntil = "2030-01-02T03:04:05+00:00",
                    PlanType = "team",
                    Source = "import",
                    Cockpit = cockpit,
                    SealedSecrets = SubscriptionStore.SealSecrets(secrets),
                },
            });

            var until = DateTimeOffset.Parse("2030-05-06T07:08:09+00:00");
            await store.UpdateActiveUntilAsync("acct", "a@b.com", until);

            var record = (await store.LoadAsync())["acct|a@b.com"];
            Assert.Equal(until.ToUniversalTime().ToString("O"), record.ActiveUntil);
            Assert.Equal("team", record.PlanType);
            Assert.Equal("import", record.Source);
            Assert.Equal(until.ToUniversalTime().ToString("O"), record.Cockpit!["subscription_active_until"]!.GetValue<string>());
            // The sealed token blob must survive the metadata-only refresh.
            Assert.NotNull(record.SealedSecrets);
            Assert.Equal("ACCESS-SECRET", SubscriptionStore.OpenSecrets(record.SealedSecrets)!["tokens"]!["access_token"]!.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task UpdateActiveUntilMarksEntryAuthoritativeAndSurfacesValue()
    {
        var dir = TestDirectory();
        try
        {
            var store = new SubscriptionStore(_ => Task.FromResult(dir));
            var until = DateTimeOffset.Parse("2030-01-02T03:04:05+00:00");
            await store.UpdateActiveUntilAsync("acct", "a@b.com", until);

            var record = (await store.LoadAsync())["acct|a@b.com"];
            Assert.True(record.Authoritative);

            var map = await store.LoadActiveUntilAsync();
            Assert.Equal(until.ToUniversalTime().ToString("O"), map["acct|a@b.com"]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ClearActiveUntilSurfacesAuthoritativeEmptyAndPreservesSecrets()
    {
        var dir = TestDirectory();
        try
        {
            var store = new SubscriptionStore(_ => Task.FromResult(dir));
            var (cockpit, secrets) = SubscriptionStore.SplitSecrets(new JsonObject
            {
                ["email"] = "a@b.com",
                ["account_id"] = "acct",
                ["plan_type"] = "team",
                ["tokens"] = new JsonObject { ["access_token"] = "ACCESS-SECRET" },
            });
            await store.SaveAsync(new[]
            {
                new SubscriptionRecord
                {
                    AccountId = "acct",
                    Email = "a@b.com",
                    ActiveUntil = "2030-01-02T03:04:05+00:00",
                    PlanType = "team",
                    Source = "import",
                    Cockpit = cockpit,
                    SealedSecrets = SubscriptionStore.SealSecrets(secrets),
                },
            });

            await store.ClearActiveUntilAsync("acct", "a@b.com");

            var record = (await store.LoadAsync())["acct|a@b.com"];
            Assert.Null(record.ActiveUntil);
            Assert.True(record.Authoritative);
            Assert.Equal("team", record.PlanType);
            Assert.Null(record.Cockpit!["subscription_active_until"]);
            Assert.NotNull(record.SealedSecrets);
            Assert.Equal("ACCESS-SECRET", SubscriptionStore.OpenSecrets(record.SealedSecrets)!["tokens"]!["access_token"]!.GetValue<string>());

            // The empty-string sentinel tells the mapper "authoritatively none".
            var map = await store.LoadActiveUntilAsync();
            Assert.True(map.ContainsKey("acct|a@b.com"));
            Assert.Equal(string.Empty, map["acct|a@b.com"]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AuthoritativeFlagRoundTripsThroughSerializeParse()
    {
        var serialized = SubscriptionStore.Serialize(new[]
        {
            new SubscriptionRecord { AccountId = "acct", Email = "a@b.com", ActiveUntil = null, Authoritative = true },
        });

        Assert.Contains("subscriptionAuthoritative", serialized);

        var record = SubscriptionStore.Parse(serialized)["acct|a@b.com"];
        Assert.True(record.Authoritative);
        Assert.Null(record.ActiveUntil);
    }

    private static string TestDirectory()
    {
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode")
            : Path.GetTempPath();
        return Path.Combine(root, "am-store-" + Guid.NewGuid().ToString("N"));
    }
}
