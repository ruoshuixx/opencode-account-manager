using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Export;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

public class AccountAdditionTests
{
    [Fact]
    public async Task AddApiPreservesOtherConfigAndCurrentCodexAndKeepsKeyOutOfConfig()
    {
        using var f = new Fixture();
        f.Api.Credentials.Add(Codex("current", "current@sample.test", true));
        f.Config.Config["model"] = "old/model";
        f.Config.Config["providers"] = new JsonObject { ["old"] = new JsonObject { ["name"] = "Original" } };
        f.Config.SyncRaw();
        var id = AccountActions.NewApiProviderId();
        var result = await f.Actions.CreateManagedApiProviderAsync(id, "My API", "https://api.example.test/v1", new[] { "model-a", "model-b" }, "test-secret", true);
        Assert.True(result.Complete);
        Assert.Equal(id, result.ProviderID);
        Assert.Equal("My API", f.Config.Config["providers"]![id]!["name"]!.GetValue<string>());
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, f.Config.Config["providers"]![id]!["package"]!.GetValue<string>());
        Assert.Equal("http", f.Config.Config["providers"]![id]!["settings"]!["transport"]!.GetValue<string>());
        Assert.Equal("old/model", f.Config.Config["model"]!.GetValue<string>());
        Assert.Equal("Original", f.Config.Config["providers"]!["old"]!["name"]!.GetValue<string>());
        Assert.DoesNotContain("test-secret", f.Config.Raw);
        Assert.True(f.Api.Find("current")!["active"]!.GetValue<bool>());
        Assert.Equal("test-secret", f.Api.Find(result.CredentialID)!["value"]!["key"]!.GetValue<string>());
        Assert.True(File.Exists(result.Backup));
        Assert.Equal(1, f.Api.Creates);
        Assert.Empty(f.Api.DeletedIds);
        Assert.True(f.Api.Find(result.CredentialID)!["value"]!["metadata"]!["created_at"]!.GetValue<long>() > 0);
        Assert.Equal(0L, f.Api.Find(result.CredentialID)!["value"]!["metadata"]!["last_used"]!.GetValue<long>());
    }

    [Fact]
    public async Task AddApiRefusesExistingProviderIncludingNullDefinitionBeforeBackupOrWrite()
    {
        using var f = new Fixture();
        var id = AccountActions.NewApiProviderId();
        f.Config.Config["providers"] = new JsonObject { [id] = null };
        f.Config.SyncRaw();
        await Assert.ThrowsAsync<OpenCodeException>(() => f.Actions.CreateManagedApiProviderAsync(id, "API", "https://example.test", new[] { "m" }, "secret"));
        Assert.Equal(0, f.Api.Creates);
        Assert.Empty(Directory.GetFiles(f.Backups.DirectoryPath));
    }

    [Fact]
    public async Task AddApiRefusesProjectOverrideAndOrphanCredential()
    {
        using var f = new Fixture();
        var id = AccountActions.NewApiProviderId();
        f.Api.ExtraDocuments.Add(new JsonObject
        {
            ["path"] = "project/opencode.json", ["type"] = "file",
            ["info"] = new JsonObject { ["providers"] = new JsonObject { [id] = new JsonObject() } },
        });
        await Assert.ThrowsAsync<OpenCodeException>(() => f.Actions.CreateManagedApiProviderAsync(id, "API", "https://example.test", new[] { "m" }, "secret"));
        f.Api.ExtraDocuments.Clear();
        f.Api.Credentials.Add(new JsonObject { ["id"] = "orphan", ["integrationID"] = id, ["value"] = new JsonObject { ["type"] = "unknown" } });
        await Assert.ThrowsAsync<OpenCodeException>(() => f.Actions.CreateManagedApiProviderAsync(id, "API", "https://example.test", new[] { "m" }, "secret"));
        Assert.Equal(0, f.Api.Creates);
    }

    [Fact]
    public async Task LostPostResponseIsReconciledWithoutSecondPost()
    {
        using var f = new Fixture();
        f.Api.LoseCreateResponse = true;
        var result = await f.Actions.CreateManagedApiProviderAsync(AccountActions.NewApiProviderId(), "API", "https://example.test", new[] { "m" }, "secret");
        Assert.True(result.Complete);
        Assert.Equal(1, f.Api.Creates);
        Assert.Single(f.Api.Credentials);
    }

    [Fact]
    public async Task ServerIgnoringRequestedIdNeverAuthorizesDeletingUnrelatedIdOrClaimingCleanup()
    {
        using var f = new Fixture();
        f.Api.IgnoreRequestedId = true;
        var id = AccountActions.NewApiProviderId();
        var result = await f.Actions.CreateManagedApiProviderAsync(id, "API", "https://example.test", new[] { "m" }, "secret");
        Assert.False(result.Complete);
        Assert.Contains("未删除", result.Warning);
        Assert.False(result.Saved);
        Assert.Single(f.Api.Credentials);
        Assert.Empty(f.Api.DeletedIds);
        Assert.Null(f.Config.Config["providers"]);
        Assert.Equal(1, f.Api.Creates);
    }

    [Fact]
    public async Task RepeatingApiAdditionNeverOverwritesOrCreatesSecondKey()
    {
        using var f = new Fixture();
        var id = AccountActions.NewApiProviderId();
        await f.Actions.CreateManagedApiProviderAsync(id, "API", "https://example.test", new[] { "m" }, "secret");
        await Assert.ThrowsAsync<OpenCodeException>(() => f.Actions.CreateManagedApiProviderAsync(id, "API", "https://example.test", new[] { "m" }, "different-key"));
        Assert.Equal(1, f.Api.Creates);
        Assert.Single(f.Api.Credentials);
        Assert.Equal("secret", f.Api.Credentials[0]!["value"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConfigWriteFailureOnlyCleansExactNewCredential()
    {
        using var f = new Fixture();
        f.Api.Credentials.Add(Codex("current", "current@sample.test", true));
        f.Config.FailWrite = true;
        var original = f.Config.Raw;
        var ex = await Assert.ThrowsAsync<OpenCodeException>(() => f.Actions.CreateManagedApiProviderAsync(AccountActions.NewApiProviderId(), "API", "https://example.test", new[] { "m" }, "secret"));
        Assert.Contains("安全清理", ex.Message);
        Assert.Equal(original, f.Config.Raw);
        Assert.Single(f.Api.Credentials);
        Assert.NotNull(f.Api.Find("current"));
        Assert.Single(f.Api.DeletedIds);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public async Task ConcurrentConfigChangeIsNotOverwrittenOrRolledBack()
    {
        using var f = new Fixture();
        f.Config.BeforeWrite = () => { f.Config.Config["user-edit"] = "preserve"; f.Config.SyncRaw(); };
        var result = await f.Actions.CreateManagedApiProviderAsync(AccountActions.NewApiProviderId(), "API", "https://example.test", new[] { "m" }, "secret");
        Assert.False(result.Complete);
        Assert.Equal("preserve", f.Config.Config["user-edit"]!.GetValue<string>());
        Assert.Single(f.Api.Credentials);
        Assert.Empty(f.Api.DeletedIds);
    }

    [Fact]
    public async Task ChangedNewCredentialIsNeverDeletedDuringCompensation()
    {
        using var f = new Fixture();
        f.Api.AfterCreate = node => node["value"]!["key"] = "changed-by-user";
        var result = await f.Actions.CreateManagedApiProviderAsync(AccountActions.NewApiProviderId(), "API", "https://example.test", new[] { "m" }, "secret");
        Assert.False(result.Complete);
        Assert.Empty(f.Api.DeletedIds);
        Assert.Equal("changed-by-user", f.Api.Credentials[0]!["value"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task PostWriteConcurrentChangeKeepsConfigAndCredentialAndWarns()
    {
        using var f = new Fixture();
        f.Config.AfterWrite = () => { f.Config.Config["user-edit"] = true; f.Config.SyncRaw(); };
        var result = await f.Actions.CreateManagedApiProviderAsync(AccountActions.NewApiProviderId(), "API", "https://example.test", new[] { "m" }, "secret");
        Assert.False(result.Complete);
        Assert.True(f.Config.Config["user-edit"]!.GetValue<bool>());
        Assert.Single(f.Api.Credentials);
        Assert.Empty(f.Api.DeletedIds);
    }

    [Fact]
    public async Task RenamePreservesIdKeyAndModelSettingsAndRejectsProjectOverrides()
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        f.Api.Credentials.Add(new JsonObject
        {
            ["id"] = "key-1", ["integrationID"] = "stable-id", ["active"] = true,
            ["value"] = new JsonObject { ["type"] = "key", ["key"] = "secret", ["metadata"] = new JsonObject() },
        });
        var model = f.Config.Config["providers"]!["stable-id"]!["models"]!["m"]!;
        model["limit"] = new JsonObject { ["context"] = 10000 };
        f.Config.SyncRaw();
        var result = await f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "Renamed", "https://example.test/v2", new[] { "m" }, null);
        Assert.True(result.Complete);
        Assert.Equal("Renamed", f.Config.Config["providers"]!["stable-id"]!["name"]!.GetValue<string>());
        Assert.Equal(10000, f.Config.Config["providers"]!["stable-id"]!["models"]!["m"]!["limit"]!["context"]!.GetValue<int>());
        Assert.Equal("secret", f.Api.Find("key-1")!["value"]!["key"]!.GetValue<string>());
        Assert.Equal(0, f.Api.Creates);
        f.Api.ExtraDocuments.Add(new JsonObject { ["type"] = "file", ["path"] = "project/opencode.json", ["info"] = f.Config.Config.DeepClone() });
        await Assert.ThrowsAsync<OpenCodeException>(() => f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "Blocked", "https://example.test", new[] { "m" }, null));
        Assert.Equal("Renamed", f.Config.Config["providers"]!["stable-id"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConfigOnlyProviderCanReceiveItsFirstKey()
    {
        using var f = new Fixture();
        f.SetProvider("configured-only");
        var result = await f.Actions.UpdateManagedApiProviderAsync("", "configured-only", "New Name", "https://example.test", new[] { "m" }, "first-key");
        Assert.True(result.Complete);
        Assert.Single(f.Api.Credentials);
        Assert.Equal("configured-only", result.ProviderID);
        Assert.Equal("first-key", f.Api.Credentials[0]!["value"]!["key"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SwitchingApiProtocolPreservesIdKeyModelReferencesAndOtherSettings(bool useResponses)
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        var provider = f.Config.Config["providers"]!["stable-id"]!;
        provider["package"] = useResponses
            ? "@opencode/ai/providers/openai-compatible"
            : "@opencode/ai/providers/openai-compatible/responses";
        provider["settings"]!["apiKey"] = "inline-test-key";
        provider["settings"]!["timeout"] = 60000;
        provider["headers"] = new JsonObject { ["X-Tenant"] = "tenant-1" };
        provider["models"]!["m"]!["limit"] = new JsonObject { ["context"] = 10000 };
        provider["models"]!["m"]!["modelID"] = "upstream-model";
        f.Config.Config["model"] = "stable-id/m";
        f.Config.Config["providers"]!["other"] = new JsonObject { ["name"] = "Other" };
        f.Config.SyncRaw();
        f.Api.Credentials.Add(ApiKeyCredential());
        var credentialsBefore = f.Api.Credentials.DeepClone();

        var initialReader = new ProviderConfigReader(f.Api, f.Config, f.Secrets);
        var initial = await new AccountSnapshotService(f.Api, new AccountMapper(f.Secrets), f.Secrets, providers: initialReader).GetSnapshotAsync();
        if (OpenAiApiProtocol.IsLegacyResponses(provider["package"]!.GetValue<string>())) { Assert.Contains("旧 Responses", initial.Warning); }

        var result = await f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "Original", "https://example.test", new[] { "m" }, null, useResponses);

        Assert.True(result.Complete);
        Assert.Equal("key-1", result.CredentialID);
        Assert.Equal("stable-id", result.ProviderID);
        var saved = f.Config.Config["providers"]!["stable-id"]!;
        var expectedPackage = useResponses
            ? OpenAiApiProtocol.ResponsesPackage
            : "@opencode/ai/providers/openai-compatible";
        Assert.Equal(expectedPackage, saved["package"]!.GetValue<string>());
        if (useResponses) { Assert.Equal("http", saved["settings"]!["transport"]!.GetValue<string>()); }
        Assert.Equal("Original", saved["name"]!.GetValue<string>());
        Assert.Equal("inline-test-key", saved["settings"]!["apiKey"]!.GetValue<string>());
        Assert.Equal(60000, saved["settings"]!["timeout"]!.GetValue<int>());
        Assert.Equal("tenant-1", saved["headers"]!["X-Tenant"]!.GetValue<string>());
        Assert.Equal("upstream-model", saved["models"]!["m"]!["modelID"]!.GetValue<string>());
        Assert.Equal(10000, saved["models"]!["m"]!["limit"]!["context"]!.GetValue<int>());
        Assert.Equal("stable-id/m", f.Config.Config["model"]!.GetValue<string>());
        Assert.Equal("Other", f.Config.Config["providers"]!["other"]!["name"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(credentialsBefore, f.Api.Credentials));
        Assert.Equal(0, f.Api.Creates);
        Assert.Empty(f.Api.DeletedIds);
        Assert.True(File.Exists(result.Backup));

        var reader = new ProviderConfigReader(f.Api, f.Config, f.Secrets);
        var snapshot = await new AccountSnapshotService(f.Api, new AccountMapper(f.Secrets), f.Secrets, providers: reader).GetSnapshotAsync();
        Assert.Equal(expectedPackage, snapshot.Accounts.Single(r => r.Provider == "stable-id").Definition!.Package);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OlderApiUpdateOverloadsKeepExistingResponsesProtocol(bool includeDisplayName)
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        f.Config.Config["providers"]!["stable-id"]!["package"] = "@opencode/ai/providers/openai-compatible/responses";
        f.Config.SyncRaw();
        f.Api.Credentials.Add(ApiKeyCredential());
        var result = includeDisplayName
            ? await f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "Renamed", "https://example.test/v2", new[] { "m" }, null)
            : await f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "https://example.test/v2", new[] { "m" }, null);
        Assert.True(result.Complete);
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, f.Config.Config["providers"]!["stable-id"]!["package"]!.GetValue<string>());
        Assert.Equal(0, f.Api.Creates);
    }

    [Fact]
    public async Task ApiProtocolCanChangeTogetherWithKeyRotation()
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        f.Api.Credentials.Add(ApiKeyCredential());
        var result = await f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "Original", "https://example.test", new[] { "m" }, "new-test-key", true);
        Assert.True(result.Complete);
        Assert.Equal("stable-id", result.ProviderID);
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, f.Config.Config["providers"]!["stable-id"]!["package"]!.GetValue<string>());
        Assert.Null(f.Api.Find("key-1"));
        Assert.Equal("new-test-key", f.Api.Find(result.CredentialID)!["value"]!["key"]!.GetValue<string>());
        Assert.Equal(new[] { "key-1" }, f.Api.DeletedIds);
    }

    [Fact]
    public async Task ConfigOnlyProviderCanChangeProtocolWithoutCreatingACredential()
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        var result = await f.Actions.UpdateManagedApiProviderAsync("", "stable-id", "Original", "https://example.test", new[] { "m" }, null, true);
        Assert.True(result.Complete);
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, f.Config.Config["providers"]!["stable-id"]!["package"]!.GetValue<string>());
        Assert.Empty(f.Api.Credentials);
        Assert.Equal(0, f.Api.Creates);
    }

    [Fact]
    public async Task SavingLegacyResponsesRepairsProviderAndModelEntryPointsWithoutRotatingTheKey()
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        var provider = f.Config.Config["providers"]!["stable-id"]!;
        provider["package"] = OpenAiApiProtocol.LegacyResponsesPackage;
        provider["models"]!["m"]!["package"] = OpenAiApiProtocol.LegacyResponsesPackage;
        provider["models"]!["m"]!["settings"] = new JsonObject { ["transport"] = "websocket", ["reasoningEffort"] = "high" };
        provider["models"]!["m"]!["modelID"] = "deepseek-v4.1-flash";
        f.Config.Config["model"] = "stable-id/m";
        f.Config.SyncRaw();
        f.Api.Credentials.Add(ApiKeyCredential());
        var credentialsBefore = f.Api.Credentials.DeepClone();

        var result = await f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "Original", "https://example.test", new[] { "m" }, null);

        Assert.True(result.Complete);
        var saved = f.Config.Config["providers"]!["stable-id"]!;
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, saved["package"]!.GetValue<string>());
        Assert.Equal("http", saved["settings"]!["transport"]!.GetValue<string>());
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, saved["models"]!["m"]!["package"]!.GetValue<string>());
        Assert.Equal("http", saved["models"]!["m"]!["settings"]!["transport"]!.GetValue<string>());
        Assert.Equal("high", saved["models"]!["m"]!["settings"]!["reasoningEffort"]!.GetValue<string>());
        Assert.Equal("deepseek-v4.1-flash", saved["models"]!["m"]!["modelID"]!.GetValue<string>());
        Assert.Equal("stable-id/m", f.Config.Config["model"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(credentialsBefore, f.Api.Credentials));
        Assert.Equal(0, f.Api.Creates);
        Assert.Empty(f.Api.DeletedIds);
        Assert.True(File.Exists(result.Backup));
        var reader = new ProviderConfigReader(f.Api, f.Config, f.Secrets);
        var snapshot = await new AccountSnapshotService(f.Api, new AccountMapper(f.Secrets), f.Secrets, providers: reader).GetSnapshotAsync();
        Assert.Empty(snapshot.Warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitProtocolChoiceUpdatesSupportedModelOverridesAndPreservesCustomOnes(bool responses)
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        var provider = f.Config.Config["providers"]!["stable-id"]!;
        provider["package"] = responses ? OpenAiApiProtocol.ChatPackage : OpenAiApiProtocol.ResponsesPackage;
        provider["models"]!["m"]!["package"] = responses ? OpenAiApiProtocol.ChatPackage : OpenAiApiProtocol.LegacyResponsesPackage;
        provider["models"]!["custom"] = new JsonObject { ["package"] = "file:///custom-adapter.js", ["name"] = "Custom" };
        provider["models"]!["inherit"] = new JsonObject { ["settings"] = new JsonObject { ["transport"] = "websocket" } };
        f.Config.SyncRaw();
        var result = await f.Actions.UpdateManagedApiProviderAsync("", "stable-id", "Original", "https://example.test", new[] { "m", "custom", "inherit" }, null, responses);
        Assert.True(result.Complete);
        var saved = f.Config.Config["providers"]!["stable-id"]!;
        Assert.Equal(OpenAiApiProtocol.PackageFor(responses), saved["models"]!["m"]!["package"]!.GetValue<string>());
        Assert.Equal("file:///custom-adapter.js", saved["models"]!["custom"]!["package"]!.GetValue<string>());
        if (responses) { Assert.Equal("http", saved["models"]!["inherit"]!["settings"]!["transport"]!.GetValue<string>()); }
        Assert.Equal(0, f.Api.Creates);
    }

    [Fact]
    public async Task AddApiWithResponsesAddsDocumentedTiersToKnownModelsOnly()
    {
        using var f = new Fixture();
        var id = AccountActions.NewApiProviderId();
        var result = await f.Actions.CreateManagedApiProviderAsync(id, "Ark",
            "https://ark.cn-beijing.volces.com/api/coding/v3",
            new[] { "deepseek-v4.1-flash", "deepseek-v4-pro", "glm-5.3-flash", "doubao-seed-2.1-turbo", "glm-5.3", "unknown-model" },
            "secret", true);

        Assert.True(result.Complete);
        var models = f.Config.Config["providers"]![id]!["models"]!;
        AssertEffortTiers(models["deepseek-v4.1-flash"]!, "low", "high");
        AssertEffortTiers(models["deepseek-v4-pro"]!, "low", "high");
        AssertEffortTiers(models["glm-5.3-flash"]!, "low", "high", "max");
        AssertEffortTiers(models["doubao-seed-2.1-turbo"]!, "low", "medium", "high");
        // Models the official table does not document are never guessed.
        Assert.Null(models["glm-5.3"]!["variants"]);
        Assert.Null(models["unknown-model"]!["variants"]);
    }

    [Fact]
    public async Task AddApiWithChatCompletionsAddsNoTiers()
    {
        using var f = new Fixture();
        var id = AccountActions.NewApiProviderId();
        await f.Actions.CreateManagedApiProviderAsync(id, "Ark",
            "https://ark.cn-beijing.volces.com/api/coding/v3", new[] { "deepseek-v4-pro" }, "secret", false);

        Assert.Null(f.Config.Config["providers"]![id]!["models"]!["deepseek-v4-pro"]!["variants"]);
    }

    [Fact]
    public async Task UpdateWithResponsesAddsTiersOnlyToNewModelsAndPreservesExistingVariants()
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        var provider = f.Config.Config["providers"]!["stable-id"]!;
        provider["package"] = OpenAiApiProtocol.ResponsesPackage;
        provider["models"]!["deepseek-v4-pro"] = new JsonObject
        {
            ["name"] = "My DeepSeek",
            ["variants"] = new JsonArray(new JsonObject { ["id"] = "custom", ["body"] = new JsonObject { ["x"] = 1 } }),
        };
        f.Config.SyncRaw();

        var result = await f.Actions.UpdateManagedApiProviderAsync("", "stable-id", "Original",
            "https://ark.cn-beijing.volces.com/api/plan/v3", new[] { "deepseek-v4-pro", "glm-5.3-flash" }, null, true);

        Assert.True(result.Complete);
        var models = f.Config.Config["providers"]!["stable-id"]!["models"]!;
        Assert.Equal("My DeepSeek", models["deepseek-v4-pro"]!["name"]!.GetValue<string>());
        Assert.Equal("custom", models["deepseek-v4-pro"]!["variants"]![0]!["id"]!.GetValue<string>());
        Assert.Single(models["deepseek-v4-pro"]!["variants"]!.AsArray());
        AssertEffortTiers(models["glm-5.3-flash"]!, "low", "high", "max");
    }

    [Theory]
    [InlineData("https://ark.cn-beijing.volces.com/api/coding/v3", true)]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v3", true)]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", false)]
    [InlineData("https://example.test/api/plan/v3", false)]
    public async Task UpdateBackfillsTiersForExistingModelsWithoutVariants(string url, bool expected)
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        var provider = f.Config.Config["providers"]!["stable-id"]!;
        provider["package"] = OpenAiApiProtocol.ResponsesPackage;
        // Existing documented model entry with no variants key at all (the reported case).
        provider["models"]!["deepseek-v4-pro"] = new JsonObject { ["name"] = "deepseek-v4-pro" };
        f.Config.SyncRaw();

        var result = await f.Actions.UpdateManagedApiProviderAsync("", "stable-id", "Original", url,
            new[] { "m", "deepseek-v4-pro" }, null, true);

        Assert.True(result.Complete);
        var model = f.Config.Config["providers"]!["stable-id"]!["models"]!["deepseek-v4-pro"]!;
        Assert.Equal(expected, model.AsObject().ContainsKey("variants"));
        if (expected) { AssertEffortTiers(model, "low", "high"); }
    }

    [Fact]
    public async Task UpdateBackfillKeepsUserCustomVariants()
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        var provider = f.Config.Config["providers"]!["stable-id"]!;
        provider["package"] = OpenAiApiProtocol.ResponsesPackage;
        provider["models"]!["deepseek-v4-pro"] = new JsonObject
        {
            ["name"] = "deepseek-v4-pro",
            ["variants"] = new JsonArray(new JsonObject { ["id"] = "mine", ["body"] = new JsonObject { ["x"] = 1 } }),
        };
        f.Config.SyncRaw();

        await f.Actions.UpdateManagedApiProviderAsync("", "stable-id", "Original",
            "https://ark.cn-beijing.volces.com/api/coding/v3", new[] { "deepseek-v4-pro" }, null, true);

        var variants = f.Config.Config["providers"]!["stable-id"]!["models"]!["deepseek-v4-pro"]!["variants"]!.AsArray();
        Assert.Single(variants);
        Assert.Equal("mine", variants[0]!["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("https://ark.cn-beijing.volces.com/api/coding/v3", true)]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v3", true)]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", false)]
    [InlineData("https://example.test/api/plan/v3", false)]
    public async Task AddResponsesRestrictsTiersByUrl(string url, bool expected)
    {
        using var f = new Fixture();
        var id = AccountActions.NewApiProviderId();
        var result = await f.Actions.CreateManagedApiProviderAsync(id, "Test", url,
            new[] { "deepseek-v4-pro" }, "secret", true);
        Assert.True(result.Complete);
        Assert.Equal(expected, f.Config.Config["providers"]![id]!["models"]!["deepseek-v4-pro"]!.AsObject().ContainsKey("variants"));
    }

    [Theory]
    [InlineData("https://ark.cn-beijing.volces.com/api/coding/v3", true)]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v3", true)]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", false)]
    [InlineData("https://example.test/api/plan/v3", false)]
    public async Task UpdateResponsesRestrictsTiersByNewUrl(string url, bool expected)
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        var result = await f.Actions.UpdateManagedApiProviderAsync("", "stable-id", "Original", url,
            new[] { "m", "deepseek-v4-pro" }, null, true);
        Assert.True(result.Complete);
        Assert.Equal(expected, f.Config.Config["providers"]!["stable-id"]!["models"]!["deepseek-v4-pro"]!.AsObject().ContainsKey("variants"));
    }

    private static void AssertEffortTiers(JsonNode model, params string[] expected)
    {
        var variants = Assert.IsType<JsonArray>(model["variants"]);
        Assert.Equal(expected, variants.Select(v => v!["id"]!.GetValue<string>()).ToArray());
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], variants[i]!["body"]!["reasoning"]!["effort"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task NativeResponsesCanBeReadEditedAndRemovedWithoutLosingTheApiRow()
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        f.Config.Config["providers"]!["stable-id"]!["package"] = OpenAiApiProtocol.ResponsesPackage;
        f.Config.SyncRaw();
        f.Api.Credentials.Add(ApiKeyCredential());
        var reader = new ProviderConfigReader(f.Api, f.Config, f.Secrets);
        var snapshot = await new AccountSnapshotService(f.Api, new AccountMapper(f.Secrets), f.Secrets, providers: reader).GetSnapshotAsync();
        var row = Assert.Single(snapshot.Accounts);
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, row.Definition!.Package);
        var save = await f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "Renamed", "https://example.test", new[] { "m" }, null, true);
        Assert.True(save.Complete);
        Assert.Equal("http", f.Config.Config["providers"]!["stable-id"]!["settings"]!["transport"]!.GetValue<string>());
        var removed = await f.Actions.RemoveManagedAccountAsync("key-1", "stable-id");
        Assert.True(removed.Removed);
        Assert.Empty(f.Api.Credentials);
    }

    [Fact]
    public async Task ProtocolChangesStillRefuseProjectOverridesAndProtectConcurrentEdits()
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        f.Api.Credentials.Add(ApiKeyCredential());
        f.Api.ExtraDocuments.Add(new JsonObject { ["type"] = "file", ["path"] = "project/opencode.json", ["info"] = f.Config.Config.DeepClone() });
        await Assert.ThrowsAsync<OpenCodeException>(() => f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "Original", "https://example.test", new[] { "m" }, null, true));
        f.Api.ExtraDocuments.Clear();
        f.Config.BeforeWrite = () => { f.Config.Config["user-edit"] = "keep"; f.Config.SyncRaw(); };
        await Assert.ThrowsAsync<OpenCodeException>(() => f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "Original", "https://example.test", new[] { "m" }, null, true));
        Assert.Equal("keep", f.Config.Config["user-edit"]!.GetValue<string>());
        Assert.Equal("@opencode/ai/providers/openai-compatible", f.Config.Config["providers"]!["stable-id"]!["package"]!.GetValue<string>());
        Assert.Single(f.Api.Credentials);
        Assert.Equal(0, f.Api.Creates);
        Assert.Empty(f.Api.DeletedIds);
    }

    [Fact]
    public async Task ProtocolReadbackMismatchWarnsWithoutOverwritingConcurrentChange()
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        f.Api.Credentials.Add(ApiKeyCredential());
        f.Config.AfterWrite = () =>
        {
            f.Config.Config["providers"]!["stable-id"]!["package"] = "concurrent-custom-package";
            f.Config.SyncRaw();
        };
        var result = await f.Actions.UpdateManagedApiProviderAsync("key-1", "stable-id", "Original", "https://example.test", new[] { "m" }, null, true);
        Assert.False(result.Complete);
        Assert.Equal("concurrent-custom-package", f.Config.Config["providers"]!["stable-id"]!["package"]!.GetValue<string>());
        Assert.Equal(0, f.Api.Creates);
        Assert.Empty(f.Api.DeletedIds);
    }

    [Fact]
    public async Task AddCodexDoesNotActivateOverExistingAccountAndIsIdempotent()
    {
        using var f = new Fixture();
        f.Api.Credentials.Add(Codex("current", "current@sample.test", true));
        var preview = await f.Actions.PreviewCodexAuthorizationAsync(AuthorizedAccount("new@sample.test"));
        Assert.False(preview.HasExistingAccount);
        var result = await f.Actions.SaveCodexAuthorizationAsync(preview, false);
        Assert.True(result.Complete);
        Assert.True(f.Api.Find("current")!["active"]!.GetValue<bool>());
        Assert.False(f.Api.Find(result.CredentialID)!["active"]!.GetValue<bool>());
        Assert.Equal("fake-id-token", f.Api.Find(result.CredentialID)!["value"]!["metadata"]!["id_token"]!.GetValue<string>());
        Assert.Same(result, await f.Actions.SaveCodexAuthorizationAsync(preview, false));
        Assert.Equal(1, f.Api.Creates);
    }

    [Fact]
    public async Task FirstCodexAccountUsesServerActivationRuleAndExplicitWarning()
    {
        using var f = new Fixture();
        var preview = await f.Actions.PreviewCodexAuthorizationAsync(AuthorizedAccount("new@sample.test"));
        var result = await f.Actions.SaveCodexAuthorizationAsync(preview, false);
        Assert.True(result.Complete);
        Assert.True(f.Api.Find(result.CredentialID)!["active"]!.GetValue<bool>());
        Assert.Contains("首个", result.Warning);
        Assert.False(f.Api.LastRequestedActivate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateCodexRequiresConfirmationAndPreservesActiveIdentity(bool active)
    {
        using var f = new Fixture();
        if (!active) { f.Api.Credentials.Add(Codex("current", "current@sample.test", true)); }
        f.Api.Credentials.Add(Codex("old", "same@sample.test", active));
        var preview = await f.Actions.PreviewCodexAuthorizationAsync(AuthorizedAccount("same@sample.test"));
        Assert.True(preview.HasExistingAccount);
        await Assert.ThrowsAsync<OpenCodeException>(() => f.Actions.SaveCodexAuthorizationAsync(preview, false));
        Assert.Equal(0, f.Api.Creates);
        var result = await f.Actions.SaveCodexAuthorizationAsync(preview, true);
        Assert.True(result.Complete);
        Assert.Null(f.Api.Find("old"));
        Assert.Equal(active, f.Api.Find(result.CredentialID)!["active"]!.GetValue<bool>());
        if (!active) { Assert.True(f.Api.Find("current")!["active"]!.GetValue<bool>()); }
        Assert.Equal(new[] { "old" }, f.Api.DeletedIds);
    }

    [Fact]
    public async Task ConfirmationDoesNotAuthorizeOverwritingChangedCredential()
    {
        using var f = new Fixture();
        f.Api.Credentials.Add(Codex("old", "same@sample.test", true));
        var preview = await f.Actions.PreviewCodexAuthorizationAsync(AuthorizedAccount("same@sample.test"));
        f.Api.Find("old")!["value"]!["refresh"] = "concurrent-refresh";
        await Assert.ThrowsAsync<OpenCodeException>(() => f.Actions.SaveCodexAuthorizationAsync(preview, true));
        Assert.Equal(0, f.Api.Creates);
        Assert.Empty(f.Api.DeletedIds);
    }

    [Fact]
    public async Task ChangedOldCredentialAfterCreateIsRetainedAndPartialResultWarns()
    {
        using var f = new Fixture();
        f.Api.Credentials.Add(Codex("old", "same@sample.test", true));
        var preview = await f.Actions.PreviewCodexAuthorizationAsync(AuthorizedAccount("same@sample.test"));
        f.Api.AfterCreate = _ => f.Api.Find("old")!["value"]!["refresh"] = "concurrent-refresh";
        var result = await f.Actions.SaveCodexAuthorizationAsync(preview, true);
        Assert.False(result.Complete);
        Assert.NotNull(f.Api.Find("old"));
        Assert.Empty(f.Api.DeletedIds);
    }

    [Fact]
    public async Task UnexpectedActivationIsReportedWithoutSwitchingOrDeletingAnything()
    {
        using var f = new Fixture();
        f.Api.Credentials.Add(Codex("current", "current@sample.test", true));
        var preview = await f.Actions.PreviewCodexAuthorizationAsync(AuthorizedAccount("new@sample.test"));
        f.Api.AfterCreate = node => { node["active"] = true; f.Api.Find("current")!["active"] = false; };
        var result = await f.Actions.SaveCodexAuthorizationAsync(preview, false);
        Assert.False(result.Complete);
        Assert.Contains("当前账号", result.Warning);
        Assert.Empty(f.Api.DeletedIds);
    }

    [Fact]
    public async Task DeleteFailureKeepsNewCredentialAndDoesNotRepeatCreation()
    {
        using var f = new Fixture();
        f.Api.Credentials.Add(Codex("old", "same@sample.test", true));
        f.Api.FailDelete = true;
        var preview = await f.Actions.PreviewCodexAuthorizationAsync(AuthorizedAccount("same@sample.test"));
        var result = await f.Actions.SaveCodexAuthorizationAsync(preview, true);
        Assert.False(result.Complete);
        Assert.Equal(2, f.Api.Credentials.Count);
        Assert.Same(result, await f.Actions.SaveCodexAuthorizationAsync(preview, true));
        Assert.Equal(1, f.Api.Creates);
    }

    [Fact]
    public async Task CancelledBeforeSaveDoesNotWrite()
    {
        using var f = new Fixture();
        var preview = await f.Actions.PreviewCodexAuthorizationAsync(AuthorizedAccount("new@sample.test"));
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Actions.SaveCodexAuthorizationAsync(preview, false, ct.Token));
        Assert.Equal(0, f.Api.Creates);
    }

    [Fact]
    public async Task AddedApiImmediatelyAppearsDespiteServerConfigCacheAndExportsWithNewName()
    {
        using var f = new Fixture();
        f.Api.CachedConfig = (JsonArray)(await f.Api.GetConfigEntriesAsync()).DeepClone();
        var added = await f.Actions.CreateManagedApiProviderAsync(AccountActions.NewApiProviderId(), "API", "https://example.test", new[] { "m" }, "secret");
        Assert.True(added.Complete);
        var reader = new ProviderConfigReader(f.Api, f.Config, f.Secrets);
        var snapshots = new AccountSnapshotService(f.Api, new AccountMapper(f.Secrets), f.Secrets, providers: reader);
        var snapshot = await snapshots.GetSnapshotAsync();
        Assert.Single(snapshot.Accounts);
        Assert.Equal("API", snapshot.Accounts[0].ProviderName);
        await f.Actions.UpdateManagedApiProviderAsync(added.CredentialID, added.ProviderID, "Renamed", "https://example.test", new[] { "m" }, null);
        snapshot = await snapshots.GetSnapshotAsync();
        Assert.Equal("Renamed", snapshot.Accounts[0].ProviderName);
        var export = new ExportService(f.Api, f.Secrets, f.Log, providers: reader);
        var path = Path.Combine(f.DirectoryPath, "export.json");
        await export.ExportAsync(new[] { added.CredentialID }, path);
        var rows = JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonArray;
        Assert.Equal("Renamed", rows![0]!["api_provider_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task CurrentGlobalConfigDoesNotOverrideHigherPriorityProjectConfig()
    {
        using var f = new Fixture();
        f.SetProvider("stable-id");
        f.Api.CachedConfig = (JsonArray)(await f.Api.GetConfigEntriesAsync()).DeepClone();
        f.Api.CachedConfig.Add(new JsonObject
        {
            ["type"] = "file", ["path"] = "project/opencode.json",
            ["info"] = new JsonObject { ["providers"] = new JsonObject { ["stable-id"] = new JsonObject { ["name"] = "Project" } } },
        });
        f.Config.Config["providers"]!["stable-id"]!["name"] = "Disk";
        f.Config.SyncRaw();
        var definitions = AccountMapper.GetProviderDefinitions(await new ProviderConfigReader(f.Api, f.Config, f.Secrets).ReadAsync());
        Assert.Equal("Project", definitions["stable-id"]["name"]!.GetValue<string>());
    }

    internal static JsonObject AuthorizedAccount(string email) => new()
    {
        ["auth_mode"] = "oauth", ["email"] = email, ["account_id"] = "acct-1",
        ["tokens"] = new JsonObject { ["access_token"] = AccessToken(email), ["refresh_token"] = "new-refresh", ["id_token"] = "fake-id-token" },
    };

    internal static string AccessToken(string email)
    {
        var claims = new JsonObject
        {
            ["exp"] = 4102444800L,
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = "acct-1", ["chatgpt_plan_type"] = "plus" },
            ["https://api.openai.com/profile"] = new JsonObject { ["email"] = email },
        };
        return "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
    }

    private static JsonObject ApiKeyCredential() => new()
    {
        ["id"] = "key-1", ["integrationID"] = "stable-id", ["active"] = true,
        ["value"] = new JsonObject { ["type"] = "key", ["key"] = "original-test-key", ["metadata"] = new JsonObject() },
    };

    private static JsonObject Codex(string id, string email, bool active) => new()
    {
        ["id"] = id, ["integrationID"] = "openai", ["active"] = active, ["label"] = email,
        ["value"] = new JsonObject
        {
            ["type"] = "oauth", ["methodID"] = "chatgpt-browser", ["access"] = AccessToken(email), ["refresh"] = "old-refresh", ["expires"] = 4102444800000L,
            ["metadata"] = new JsonObject { ["accountID"] = "acct-1", ["cockpitEmail"] = email },
        },
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        public FakeConfig Config { get; } = new();
        public FakeApi Api { get; }
        public AccountActions Actions { get; }
        public BackupService Backups { get; }
        public SecretRegistry Secrets { get; }
        public AppLog Log { get; }
        public string DirectoryPath => _directory;

        public Fixture()
        {
            var root = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode")
                : Path.GetTempPath();
            _directory = Path.Combine(root, "account-add-tests-" + Guid.NewGuid().ToString("N"));
            var secrets = new SecretRegistry(applyLongValueFilter: false);
            Secrets = secrets;
            Api = new FakeApi(Config);
            Backups = new BackupService(Path.Combine(_directory, "backups"));
            var log = new AppLog(secrets, Path.Combine(_directory, "logs"));
            Log = log;
            Actions = new AccountActions(Api, Config, Backups, secrets, log, new AccountMapper(secrets));
        }

        public void SetProvider(string id)
        {
            Config.Config["providers"] = new JsonObject
            {
                [id] = new JsonObject
                {
                    ["name"] = "Original", ["package"] = "@opencode/ai/providers/openai-compatible",
                    ["settings"] = new JsonObject { ["baseURL"] = "https://example.test" },
                    ["models"] = new JsonObject { ["m"] = new JsonObject { ["name"] = "m" } },
                },
            };
            Config.SyncRaw();
        }

        public void Dispose() { if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); } }
    }

    private sealed class FakeConfig : IConfigFile
    {
        public JsonObject Config { get; private set; } = new() { ["$schema"] = "https://opencode.ai/config.json" };
        public string Raw { get; private set; }
        public bool FailWrite;
        public Action? BeforeWrite;
        public Action? AfterWrite;
        public string Path { get; } = System.IO.Path.GetFullPath("test-only-opencode.json");
        public FakeConfig() { Raw = Config.ToJsonString(CoreJson.Indented); }
        public void SyncRaw() => Raw = Config.ToJsonString(CoreJson.Indented);
        public Task<ConfigDocument> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ConfigDocument { Path = Path, Raw = Raw, Config = (JsonObject)Config.DeepClone() });
        public Task WriteAsync(ConfigDocument document, CancellationToken cancellationToken = default)
        {
            BeforeWrite?.Invoke();
            if (FailWrite || Raw != document.Raw) { throw new OpenCodeException("simulated CAS/write failure"); }
            Config = (JsonObject)document.Config.DeepClone();
            SyncRaw();
            AfterWrite?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApi(FakeConfig config) : IOpenCodeApi
    {
        public JsonArray Credentials { get; } = new();
        public JsonArray ExtraDocuments { get; } = new();
        public JsonArray? CachedConfig;
        public List<string> DeletedIds { get; } = new();
        public int Creates;
        public bool LastRequestedActivate;
        public bool LoseCreateResponse;
        public bool IgnoreRequestedId;
        public bool FailDelete;
        public Action<JsonObject>? AfterCreate;
        public JsonNode? Find(string id) => Credentials.SingleOrDefault(c => JsonMerge.GetString(c, "id") == id);
        public Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((JsonArray)Credentials.DeepClone());
        public Task<JsonArray> GetConfigEntriesAsync(CancellationToken cancellationToken = default)
        {
            if (CachedConfig is not null) { return Task.FromResult((JsonArray)CachedConfig.DeepClone()); }
            var docs = (JsonArray)ExtraDocuments.DeepClone();
            docs.Insert(0, new JsonObject { ["type"] = "file", ["path"] = config.Path, ["info"] = config.Config.DeepClone() });
            return Task.FromResult(docs);
        }
        public Task<JsonNode?> GetIntegrationAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<JsonNode?>(new JsonObject { ["data"] = new JsonObject { ["methods"] = new JsonArray(new JsonObject { ["type"] = "oauth", ["id"] = "chatgpt-browser" }) } });
        public Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate, CancellationToken cancellationToken = default, string? requestedId = null)
        {
            Creates++;
            LastRequestedActivate = activate;
            var id = IgnoreRequestedId ? "unconfirmed-server-id" : requestedId ?? "created-" + Creates;
            if (Find(id) is not null) { throw new OpenCodeException("conflict"); }
            var active = activate || !Credentials.Any(c => JsonMerge.GetString(c, "integrationID") == integrationId && JsonNum.ToBool(c?["active"]));
            if (active)
            {
                foreach (var c in Credentials.Where(c => JsonMerge.GetString(c, "integrationID") == integrationId)) { c!["active"] = false; }
            }
            var entry = new JsonObject { ["id"] = id, ["integrationID"] = integrationId, ["label"] = label, ["active"] = active, ["value"] = value.DeepClone() };
            Credentials.Add(entry);
            AfterCreate?.Invoke(entry);
            if (LoseCreateResponse) { throw new OpenCodeException("simulated lost POST response"); }
            return Task.FromResult(id);
        }
        public Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
        {
            if (FailDelete) { throw new OpenCodeException("simulated DELETE failure"); }
            DeletedIds.Add(credentialId);
            var found = Find(credentialId);
            if (found is not null) { Credentials.Remove(found); }
            return Task.CompletedTask;
        }
    }
}
