using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Import;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

public class ImportProtocolApplicationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportUpdatesProtocolAndModelOverridesWithoutRotatingAnIdenticalKey(bool responses)
    {
        using var f = new Fixture(responses ? "responses" : "chat_completions");
        f.ExistingProvider["package"] = responses ? OpenAiApiProtocol.ChatPackage : OpenAiApiProtocol.LegacyResponsesPackage;
        var model = f.ExistingProvider["models"]!["m"]!;
        model["package"] = OpenAiApiProtocol.LegacyResponsesPackage;
        model["modelID"] = "upstream-model";
        model["limit"] = new JsonObject { ["context"] = 64000 };
        model["variants"] = new JsonArray(new JsonObject { ["id"] = "high", ["settings"] = new JsonObject { ["transport"] = "websocket" } });
        f.Config.SyncRaw();
        var originalCredentials = f.Api.Credentials.DeepClone();

        var result = await f.Service.ImportAsync(f.Preview());

        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(result.Backup));
        Assert.True(JsonNode.DeepEquals(originalCredentials, f.Api.Credentials));
        Assert.Equal(0, f.Api.Creates);
        Assert.Equal(0, f.Api.Deletes);
        Assert.Equal(1, f.Config.Writes);
        var provider = f.Config.Config["providers"]![f.Row.Provider]!;
        Assert.Equal(OpenAiApiProtocol.PackageFor(responses), provider["package"]!.GetValue<string>());
        Assert.Equal(OpenAiApiProtocol.PackageFor(responses), provider["models"]!["m"]!["package"]!.GetValue<string>());
        Assert.Equal("upstream-model", provider["models"]!["m"]!["modelID"]!.GetValue<string>());
        Assert.Equal(64000, provider["models"]!["m"]!["limit"]!["context"]!.GetValue<int>());
        Assert.Equal("stable-default", f.Config.Config["model"]!.GetValue<string>());
        Assert.Equal("keep", provider["headers"]!["X-Tenant"]!.GetValue<string>());
        Assert.DoesNotContain("SIMULATED-IMPORT-KEY", f.Config.Raw);
        Assert.Contains(result.Messages, m => m.Contains("后台已识别"));
        Assert.DoesNotContain(result.Messages, m => m.Contains("请重启"));
        if (responses)
        {
            Assert.Equal("http", provider["settings"]!["transport"]!.GetValue<string>());
            Assert.Equal("http", provider["models"]!["m"]!["variants"]![0]!["settings"]!["transport"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task ImportAddsDocumentedTiersToNewResponsesModelsOnly()
    {
        var source = ApiAccount("responses");
        source["api_base_url"] = "https://ark.cn-beijing.volces.com/api/coding/v3";
        source["api_model_catalog"] = new JsonArray("deepseek-v4-pro", "m", "unknown-model");
        using var f = new Fixture(source);
        f.ExistingProvider["models"]!["m"]!["variants"] = new JsonArray(new JsonObject { ["id"] = "custom" });
        f.Config.SyncRaw();

        var result = await f.Service.ImportAsync(f.Preview());

        Assert.Equal(0, result.Failed);
        var models = f.Config.Config["providers"]![f.Row.Provider]!["models"]!;
        // Existing entries keep their own variants untouched.
        Assert.Equal("custom", models["m"]!["variants"]![0]!["id"]!.GetValue<string>());
        // A brand-new documented model gets tiers; an unknown one is not guessed.
        Assert.Equal("low", models["deepseek-v4-pro"]!["variants"]![0]!["id"]!.GetValue<string>());
        Assert.Null(models["unknown-model"]!["variants"]);
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("empty")]
    [InlineData("null")]
    public async Task ImportPreservesExistingDocumentedModelVariants(string existingVariants)
    {
        var source = ApiAccount("responses");
        source["api_base_url"] = "https://ark.cn-beijing.volces.com/api/plan/v3";
        source["api_model_catalog"] = new JsonArray("deepseek-v4-pro");
        using var f = new Fixture(source);
        var model = new JsonObject { ["name"] = "deepseek-v4-pro" };
        if (existingVariants == "custom")
        {
            model["variants"] = new JsonArray(new JsonObject
            {
                ["id"] = "user-custom", ["body"] = new JsonObject { ["custom"] = true },
            });
        }
        else if (existingVariants == "empty") { model["variants"] = new JsonArray(); }
        else if (existingVariants == "null") { model["variants"] = null; }
        f.ExistingProvider["models"]!["deepseek-v4-pro"] = model;
        f.Config.SyncRaw();
        var before = model.DeepClone();

        var result = await f.Service.ImportAsync(f.Preview());

        Assert.Equal(0, result.Failed);
        var saved = f.ExistingProvider["models"]!["deepseek-v4-pro"]!;
        Assert.True(JsonNode.DeepEquals(before, saved), "Import must not overwrite an existing model's own variants (custom/empty/null).");
    }

    [Theory]
    [InlineData("coding", "absent")]
    [InlineData("coding", "custom")]
    [InlineData("coding", "empty")]
    [InlineData("coding", "null")]
    [InlineData("plan", "absent")]
    [InlineData("plan", "custom")]
    [InlineData("plan", "empty")]
    [InlineData("plan", "null")]
    public async Task SameArkPlanAndKeyRefreshesExistingDocumentedVariants(string plan, string state)
    {
        var url = "https://ark.cn-beijing.volces.com/api/" + plan + "/v3";
        var source = ApiAccount("responses");
        source["api_base_url"] = url;
        source["api_model_catalog"] = new JsonArray("deepseek-v4-pro", "glm-5.3-flash", "unknown-model");
        using var f = new Fixture(source);
        f.ExistingProvider["settings"]!["baseURL"] = url + "/";
        f.ExistingProvider["models"]!["deepseek-v4-pro"] = ExistingTierModel("deepseek-v4-pro", state);
        f.ExistingProvider["models"]!["glm-5.3-flash"] = ExistingTierModel("glm-5.3-flash", "custom");
        f.ExistingProvider["models"]!["unknown-model"] = ExistingTierModel("unknown-model", "custom");
        var unknownBefore = f.ExistingProvider["models"]!["unknown-model"]!.DeepClone();
        f.Config.SyncRaw();

        var result = await f.Service.ImportAsync(f.Preview());

        Assert.Equal(0, result.Failed);
        var models = f.ExistingProvider["models"]!;
        Assert.Equal(new[] { "low", "high" }, models["deepseek-v4-pro"]!["variants"]!.AsArray().Select(v => v!["id"]!.GetValue<string>()));
        Assert.Equal(new[] { "low", "high", "max" }, models["glm-5.3-flash"]!["variants"]!.AsArray().Select(v => v!["id"]!.GetValue<string>()));
        Assert.Equal(64000, models["deepseek-v4-pro"]!["limit"]!["context"]!.GetValue<int>());
        Assert.Equal("keep", models["deepseek-v4-pro"]!["settings"]!["custom"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(unknownBefore, models["unknown-model"]));
        Assert.Equal(0, f.Api.Creates);
        Assert.Equal(0, f.Api.Deletes);
    }

    [Theory]
    [InlineData("coding", "coding", "responses", false)]
    [InlineData("coding", "plan", "responses", true)]
    [InlineData("plan", "coding", "responses", true)]
    [InlineData("coding", "coding", "chat_completions", true)]
    [InlineData("other", "other", "responses", true)]
    [InlineData("standard", "standard", "responses", true)]
    public async Task DifferentKeyUrlOrProtocolKeepsExistingVariants(string oldPlan, string newPlan, string wire, bool sameKey)
    {
        static string Url(string plan) => plan switch
        {
            "other" => "https://example.test/api/coding/v3",
            "standard" => "https://ark.cn-beijing.volces.com/api/v3",
            _ => "https://ark.cn-beijing.volces.com/api/" + plan + "/v3",
        };
        var source = ApiAccount(wire);
        source["api_base_url"] = Url(newPlan);
        source["api_model_catalog"] = new JsonArray("deepseek-v4-pro");
        using var f = new Fixture(source);
        f.ExistingProvider["settings"]!["baseURL"] = Url(oldPlan);
        f.ExistingProvider["models"]!["deepseek-v4-pro"] = ExistingTierModel("deepseek-v4-pro", "custom");
        var before = f.ExistingProvider["models"]!["deepseek-v4-pro"]!["variants"]!.DeepClone();
        if (!sameKey)
        {
            f.Api.AllowKeyRotation = true;
            f.Api.Credentials[0]!["value"]!["key"] = "SIMULATED-DIFFERENT-KEY";
        }
        f.Config.SyncRaw();

        var result = await f.Service.ImportAsync(f.Preview());

        Assert.Equal(0, result.Failed);
        Assert.True(JsonNode.DeepEquals(before, f.ExistingProvider["models"]!["deepseek-v4-pro"]!["variants"]));
    }

    [Fact]
    public async Task SamePlanCompactImportWithExplicitResponsesRefreshesRetainedCatalog()
    {
        var source = ApiAccount("responses");
        var url = "https://ark.cn-beijing.volces.com/api/coding/v3";
        source["api_base_url"] = url;
        source.Remove("api_model_catalog");
        using var f = new Fixture(source);
        f.ExistingProvider["settings"]!["baseURL"] = url;
        f.ExistingProvider["models"]!["deepseek-v4-pro"] = ExistingTierModel("deepseek-v4-pro", "custom");
        f.Config.SyncRaw();

        var result = await f.Service.ImportAsync(f.Preview());

        Assert.Equal(0, result.Failed);
        Assert.Equal("low", f.ExistingProvider["models"]!["deepseek-v4-pro"]!["variants"]![0]!["id"]!.GetValue<string>());
        Assert.Null(f.ExistingProvider["models"]!["ark-code-latest"]);
    }

    private static JsonObject ExistingTierModel(string id, string state)
    {
        var model = new JsonObject
        {
            ["name"] = id,
            ["limit"] = new JsonObject { ["context"] = 64000 },
            ["settings"] = new JsonObject { ["custom"] = "keep" },
        };
        if (state == "custom")
        {
            model["variants"] = new JsonArray(new JsonObject
            {
                ["id"] = "user-custom", ["body"] = new JsonObject { ["custom"] = true },
            });
        }
        else if (state == "empty") { model["variants"] = new JsonArray(); }
        else if (state == "null") { model["variants"] = null; }
        return model;
    }

    [Fact]
    public async Task ImportBackfillsTiersForExistingModelWithoutVariantsEvenWithDifferentKey()
    {
        var source = ApiAccount("responses");
        source["api_base_url"] = "https://ark.cn-beijing.volces.com/api/coding/v3";
        source["api_model_catalog"] = new JsonArray("deepseek-v4-pro");
        using var f = new Fixture(source);
        f.ExistingProvider["settings"]!["baseURL"] = "https://ark.cn-beijing.volces.com/api/coding/v3";
        f.ExistingProvider["models"]!["deepseek-v4-pro"] = new JsonObject { ["name"] = "deepseek-v4-pro" };
        f.Config.SyncRaw();

        var result = await f.Service.ImportAsync(f.Preview());

        Assert.Equal(0, result.Failed);
        var variants = f.ExistingProvider["models"]!["deepseek-v4-pro"]!["variants"]!.AsArray();
        Assert.Equal(new[] { "low", "high" }, variants.Select(v => v!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ImportChatModelDoesNotGenerateReasoningVariants()
    {
        var source = ApiAccount("chat_completions");
        source["api_base_url"] = "https://ark.cn-beijing.volces.com/api/coding/v3";
        source["api_model_catalog"] = new JsonArray("deepseek-v4-pro");
        using var f = new Fixture(source);

        var result = await f.Service.ImportAsync(f.Preview());

        Assert.Equal(0, result.Failed);
        Assert.Null(f.ExistingProvider["models"]!["deepseek-v4-pro"]!["variants"]);
    }

    [Theory]
    [InlineData("https://ark.cn-beijing.volces.com/api/coding/v3", true)]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v3", true)]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", false)]
    [InlineData("https://example.test/api/plan/v3", false)]
    public async Task ImportResponsesRestrictsPreviewAndMergedTiersByUrl(string url, bool expected)
    {
        var source = ApiAccount("responses");
        source["api_base_url"] = url;
        source["api_model_catalog"] = new JsonArray("deepseek-v4-pro");
        using var f = new Fixture(source);
        Assert.Equal(expected, f.Row.Definition!["models"]!["deepseek-v4-pro"]!.AsObject().ContainsKey("variants"));
        var result = await f.Service.ImportAsync(f.Preview());
        Assert.Equal(0, result.Failed);
        Assert.Equal(expected, f.ExistingProvider["models"]!["deepseek-v4-pro"]!.AsObject().ContainsKey("variants"));
    }

    [Fact]
    public async Task CompactArkImportKeepsModelsAndResponsesButRepairsLegacyPackages()
    {
        var source = ApiAccount("responses");
        source["api_base_url"] = "https://ark.cn-beijing.volces.com/api/coding/v3";
        source.Remove("api_wire_api");
        source.Remove("api_model_catalog");
        using var f = new Fixture(source);
        f.ExistingProvider["package"] = OpenAiApiProtocol.LegacyResponsesPackage;
        f.ExistingProvider["models"]!["m"]!["package"] = OpenAiApiProtocol.LegacyResponsesPackage;
        f.Config.SyncRaw();

        var result = await f.Service.ImportAsync(f.Preview());

        Assert.Equal(0, result.Failed);
        var provider = f.Config.Config["providers"]![f.Row.Provider]!;
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, provider["package"]!.GetValue<string>());
        Assert.Equal(OpenAiApiProtocol.ResponsesPackage, provider["models"]!["m"]!["package"]!.GetValue<string>());
        Assert.Null(provider["models"]!["ark-code-latest"]);
        Assert.Equal(0, f.Api.Creates);
    }

    [Fact]
    public async Task SubscriptionOnlyImportDoesNotChangeApiProtocolOrCallRuntimeVerification()
    {
        using var f = new Fixture("responses");
        f.ExistingProvider["package"] = OpenAiApiProtocol.LegacyResponsesPackage;
        f.Config.SyncRaw();
        var original = f.Config.Raw;
        var preview = f.Preview();
        preview.SubscriptionOnly = true;

        var result = await f.Service.ImportAsync(preview);

        Assert.Equal(0, result.Failed);
        Assert.Equal(original, f.Config.Raw);
        Assert.Equal(0, f.Config.Writes);
        Assert.Equal(0, f.Api.RuntimeReads);
        Assert.Equal(0, f.Api.Creates);
        Assert.Equal(0, f.Api.Deletes);
    }

    private static JsonObject ApiAccount(string wire) => new()
    {
        ["id"] = "source-account", ["email"] = "", ["auth_mode"] = "apikey",
        ["openai_api_key"] = "SIMULATED-IMPORT-KEY", ["api_base_url"] = "https://example.test/v1",
        ["api_provider_id"] = "source-provider", ["api_provider_name"] = "New Name", ["api_provider_mode"] = "custom",
        ["api_model_catalog"] = new JsonArray("m"), ["api_wire_api"] = wire,
        ["tokens"] = new JsonObject { ["id_token"] = "", ["access_token"] = "" },
        ["created_at"] = 1700000000L, ["last_used"] = 1700000010L,
    };

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; }
        public FakeConfig Config { get; }
        public FakeApi Api { get; }
        public ImportService Service { get; }
        public ImportService.ImportRow Row { get; }
        public JsonNode ExistingProvider => Config.Config["providers"]![Row.Provider]!;
        public Fixture(string wire) : this(ApiAccount(wire)) { }
        public Fixture(JsonObject source)
        {
            var root = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode")
                : Path.GetTempPath();
            DirectoryPath = Path.Combine(root, "import-protocol-test-" + Guid.NewGuid().ToString("N"));
            Row = ImportService.ConvertAccount(source, "chatgpt-browser");
            Config = new FakeConfig(Path.Combine(DirectoryPath, "opencode.json"));
            Config.Config["providers"] = new JsonObject
            {
                [Row.Provider] = new JsonObject
                {
                    ["name"] = "Old Name", ["package"] = OpenAiApiProtocol.ChatPackage,
                    ["settings"] = new JsonObject { ["baseURL"] = "https://old.example.test/v1" },
                    ["models"] = new JsonObject { ["m"] = new JsonObject { ["name"] = "Old Model" } },
                    ["headers"] = new JsonObject { ["X-Tenant"] = "keep" },
                },
            };
            Config.SyncRaw();
            Api = new FakeApi(Config);
            Api.Credentials.Add(new JsonObject
            {
                ["id"] = "same-credential", ["integrationID"] = Row.Provider, ["active"] = true,
                ["value"] = Row.Value.DeepClone(),
            });
            var secrets = new SecretRegistry(applyLongValueFilter: false);
            Service = new ImportService(Api, Config, new BackupService(Path.Combine(DirectoryPath, "backups")), secrets,
                new AppLog(secrets, Path.Combine(DirectoryPath, "logs")));
        }
        public ImportPreview Preview() => new() { Payload = new List<ImportService.ImportRow> { Row } };
        public void Dispose() { if (Directory.Exists(DirectoryPath)) { Directory.Delete(DirectoryPath, recursive: true); } }
    }

    private sealed class FakeConfig(string path) : IConfigFile
    {
        public JsonObject Config { get; private set; } = new() { ["model"] = "stable-default" };
        public string Raw { get; private set; } = "{}";
        public int Writes;
        public void SyncRaw() => Raw = Config.ToJsonString(CoreJson.Indented);
        public Task<ConfigDocument> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ConfigDocument { Path = path, Raw = Raw, Config = (JsonObject)Config.DeepClone() });
        public Task WriteAsync(ConfigDocument document, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Raw, document.Raw);
            Writes++;
            Config = (JsonObject)document.Config.DeepClone();
            SyncRaw();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApi(FakeConfig config) : IOpenCodeApi, IProviderRuntimeReader
    {
        public JsonArray Credentials { get; } = new();
        public int Creates, Deletes, RuntimeReads;
        public bool AllowKeyRotation;
        public Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default) => Task.FromResult((JsonArray)Credentials.DeepClone());
        public Task<JsonArray> GetConfigEntriesAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Not needed by fake file import");
        public Task<JsonNode?> GetIntegrationAsync(string id, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No real OAuth in tests");
        public Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate, CancellationToken cancellationToken = default, string? requestedId = null)
        {
            Creates++;
            if (!AllowKeyRotation) { throw new InvalidOperationException("Identical keys must not be rotated"); }
            var id = requestedId ?? "rotated-credential";
            Credentials.Add(new JsonObject { ["id"] = id, ["integrationID"] = integrationId, ["value"] = value.DeepClone(), ["active"] = activate });
            return Task.FromResult(id);
        }
        public Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default)
        {
            Deletes++;
            if (!AllowKeyRotation) { throw new InvalidOperationException("No credential deletion expected"); }
            var match = Credentials.First(c => c?["id"]?.GetValue<string>() == credentialId);
            Credentials.Remove(match);
            return Task.CompletedTask;
        }
        public Task<JsonNode?> GetProviderAsync(string providerId, CancellationToken ct = default)
        {
            RuntimeReads++;
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["data"] = new JsonObject { ["id"] = providerId, ["package"] = config.Config["providers"]![providerId]!["package"]!.DeepClone() },
            });
        }
    }
}
