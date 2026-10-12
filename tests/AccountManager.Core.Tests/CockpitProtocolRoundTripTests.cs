using System.Text.Json.Nodes;
using AccountManager.Core.Export;
using AccountManager.Core.Import;
using AccountManager.Core.Logging;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

public sealed class CockpitProtocolRoundTripTests
{
    [Theory]
    [InlineData("responses", OpenAiApiProtocol.ResponsesPackage)]
    [InlineData("chat_completions", "@opencode/ai/providers/openai-compatible")]
    public void ApiProtocolSurvivesImportExportAndFullMirrorRoundTrip(string wire, string expectedPackage)
    {
        var testRoot = TestDirectory("cockpit-roundtrip-");
        Directory.CreateDirectory(testRoot);
        try
        {
            var secrets = new SecretRegistry(applyLongValueFilter: false);
            var export = new ExportService(new TestOpenCodeApi(), secrets, new AppLog(secrets, Path.Combine(testRoot, "logs")));
            var imported = ImportService.ConvertAccount(ApiAccount(wire), "chatgpt-browser");
            Assert.Equal(expectedPackage, imported.Definition!["package"]!.GetValue<string>());

            var credential = new JsonObject
            {
                ["id"] = "credential-test",
                ["integrationID"] = imported.Provider,
                ["value"] = imported.Value.DeepClone(),
            };
            var definitions = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
            {
                [imported.Provider] = imported.Definition,
            };
            var exported = export.ConvertCredentialToCockpitExport(credential, definitions);
            var jsonRoundTrip = JsonNode.Parse(exported.ToJsonString())!.AsObject();

            Assert.Equal(wire, jsonRoundTrip["api_wire_api"]!.GetValue<string>());
            Assert.Equal("SIMULATED-ROUNDTRIP-KEY", jsonRoundTrip["openai_api_key"]!.GetValue<string>());
            Assert.Null(jsonRoundTrip["OPENAI_API_KEY"]);
            Assert.Equal(123L, jsonRoundTrip["created_at"]!.GetValue<long>());
            Assert.Equal(456L, jsonRoundTrip["last_used"]!.GetValue<long>());
            Assert.True(ExportService.TestCockpitExportShape(jsonRoundTrip));

            var cached = (JsonObject)jsonRoundTrip.DeepClone();
            var staleModels = new JsonArray();
            for (var i = 1; i <= 9; i++) { staleModels.Add((JsonNode?)("stale-" + i)); }
            cached["api_model_catalog"] = staleModels;
            cached["api_supports_websockets"] = true;
            cached["account_note"] = "simulated historical note";
            cached["OPENAI_API_KEY"] = "SIMULATED-STALE-ALIAS";

            var mirrored = ExportService.MergeCockpitMirror(jsonRoundTrip, cached, oauth: false);

            Assert.Equal(wire, mirrored["api_wire_api"]!.GetValue<string>());
            Assert.Equal(2, ((JsonArray)mirrored["api_model_catalog"]!).Count);
            Assert.Equal(wire == "responses", mirrored["api_supports_websockets"]!.GetValue<bool>());
            Assert.Equal("simulated historical note", mirrored["account_note"]!.GetValue<string>());
            Assert.Null(mirrored["OPENAI_API_KEY"]);
            Assert.DoesNotContain("SIMULATED-STALE-ALIAS", mirrored.ToJsonString());
            Assert.True(ExportService.TestCockpitExportShape(mirrored, mirrored: true));
        }
        finally
        {
            if (Directory.Exists(testRoot)) { Directory.Delete(testRoot, recursive: true); }
        }
    }

    [Fact]
    public async Task MissingLegacyTimestampsExportAsUnknownZeroAndWarnWithoutClaimingOriginalTime()
    {
        var testRoot = TestDirectory("cockpit-time-");
        Directory.CreateDirectory(testRoot);
        try
        {
            var credential = new JsonObject
            {
                ["id"] = "oauth-credential",
                ["integrationID"] = "openai",
                ["value"] = new JsonObject
                {
                    ["type"] = "oauth",
                    ["methodID"] = "chatgpt-browser",
                    ["access"] = "SIMULATED-OAUTH-ACCESS",
                    ["refresh"] = "SIMULATED-OAUTH-REFRESH",
                    ["metadata"] = new JsonObject { ["accountID"] = "account-test", ["cockpitEmail"] = "test@example.invalid" },
                },
            };
            var secrets = new SecretRegistry(applyLongValueFilter: false);
            var api = new TestOpenCodeApi(new JsonArray(credential));
            var exporter = new ExportService(api, secrets, new AppLog(secrets, Path.Combine(testRoot, "logs")));
            var path = Path.Combine(testRoot, "unknown-time.json");

            var result = await exporter.ExportAsync(new[] { "oauth-credential" }, path);
            var rows = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();

            Assert.Equal(0L, rows[0]!["created_at"]!.GetValue<long>());
            Assert.Equal(0L, rows[0]!["last_used"]!.GetValue<long>());
            Assert.Contains("0 作为未知占位", result.Warning);
            Assert.Contains("不表示原始时间", result.Warning);
            Assert.True(ExportService.TestCockpitExportShape(rows[0]));
        }
        finally
        {
            if (Directory.Exists(testRoot)) { Directory.Delete(testRoot, recursive: true); }
        }
    }

    private static string TestDirectory(string prefix)
    {
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode")
            : Path.GetTempPath();
        return Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
    }

    private static JsonObject ApiAccount(string wire) => new()
    {
        ["id"] = "source-account",
        ["email"] = string.Empty,
        ["auth_mode"] = "apikey",
        ["openai_api_key"] = "SIMULATED-ROUNDTRIP-KEY",
        ["api_base_url"] = "https://api.example.test/v1",
        ["api_provider_mode"] = "custom",
        ["api_provider_id"] = "source-provider",
        ["api_provider_name"] = "Source Provider",
        ["api_model_catalog"] = new JsonArray("live-model-1", "live-model-2"),
        ["api_wire_api"] = wire,
        ["tokens"] = new JsonObject { ["id_token"] = string.Empty, ["access_token"] = string.Empty },
        ["created_at"] = 123L,
        ["last_used"] = 456L,
    };

    private sealed class TestOpenCodeApi(JsonArray? credentials = null) : IOpenCodeApi
    {
        private readonly JsonArray _credentials = credentials is null ? new JsonArray() : (JsonArray)credentials.DeepClone();

        public Task<JsonArray> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((JsonArray)_credentials.DeepClone());
        public Task<JsonArray> GetConfigEntriesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new JsonArray());
        public Task<JsonNode?> GetIntegrationAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateCredentialAsync(string integrationId, string label, JsonObject value, bool activate,
            CancellationToken cancellationToken = default, string? requestedId = null) => throw new NotSupportedException();
        public Task DeleteCredentialAsync(string credentialId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
