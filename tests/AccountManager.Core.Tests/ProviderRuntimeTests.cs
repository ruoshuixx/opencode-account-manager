using System.Text.Json.Nodes;
using AccountManager.Core.OpenCode;
using Xunit;

namespace AccountManager.Core.Tests;

public class ProviderRuntimeTests
{
    [Theory]
    [InlineData(OpenAiApiProtocol.ChatPackage, "chat_completions", false)]
    [InlineData(OpenAiApiProtocol.ResponsesPackage, "responses", true)]
    [InlineData(OpenAiApiProtocol.LegacyResponsesPackage, "responses", true)]
    public void PackageMappingPreservesWireProtocolAndCanonicalizesTheBrokenEntrypoint(string package, string wire, bool responses)
    {
        Assert.True(OpenAiApiProtocol.IsManagedPackage(package));
        Assert.Equal(responses, OpenAiApiProtocol.UsesResponses(package));
        Assert.Equal(wire, OpenAiApiProtocol.WireApiFor(package));
        Assert.Equal(OpenAiApiProtocol.PackageFor(responses), OpenAiApiProtocol.CanonicalPackage(package));
    }

    [Fact]
    public void UnknownRuntimeIsNotRewrittenOrPresentedAsResponses()
    {
        Assert.False(OpenAiApiProtocol.IsManagedPackage("file:///custom.js"));
        Assert.Null(OpenAiApiProtocol.WireApiFor("file:///custom.js"));
        Assert.Equal("file:///custom.js", OpenAiApiProtocol.CanonicalPackage("file:///custom.js"));
        Assert.False(OpenAiApiProtocol.UsesResponses(null));
    }

    [Fact]
    public async Task RuntimeReadbackIsIndependentFromTheDiskProjectionAndWaitsForTheWatcher()
    {
        var reader = new FakeReader();
        reader.Responses.Enqueue(Provider(OpenAiApiProtocol.ChatPackage));
        reader.Responses.Enqueue(new JsonObject { ["data"] = Provider(OpenAiApiProtocol.ResponsesPackage) });
        var result = await new ProviderRuntimeVerifier(reader).VerifyAsync("api-local", OpenAiApiProtocol.ResponsesPackage);
        Assert.Equal(ProviderRuntimeState.Applied, result.State);
        Assert.Contains("无需重启", result.Message);
        Assert.Equal(2, reader.Reads);
        Assert.Equal(new[] { "api-local", "api-local" }, reader.ProviderIds);
    }

    [Fact]
    public async Task OldOrOverriddenRuntimeIsReportedPendingNotAsASaveFailure()
    {
        var reader = new FakeReader { Fallback = Provider(OpenAiApiProtocol.LegacyResponsesPackage) };
        var result = await new ProviderRuntimeVerifier(reader).VerifyAsync("api-local", OpenAiApiProtocol.ResponsesPackage);
        Assert.Equal(ProviderRuntimeState.Pending, result.State);
        Assert.Contains("已保存", result.Message);
        Assert.Contains("项目覆盖", result.Message);
        Assert.Equal(3, reader.Reads);
    }

    [Fact]
    public async Task ReadFailureDoesNotLeakOutputRetryOrClaimTheWriteFailed()
    {
        var reader = new FakeReader { Failure = new InvalidOperationException("Authorization: real-looking-test-secret") };
        var result = await new ProviderRuntimeVerifier(reader).VerifyAsync("api-local", OpenAiApiProtocol.ResponsesPackage);
        Assert.Equal(ProviderRuntimeState.Unavailable, result.State);
        Assert.DoesNotContain("real-looking-test-secret", result.Message);
        Assert.Contains("不代表保存失败", result.Message);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task WrongProviderIdCannotConfirmProtocolAdoption()
    {
        var reader = new FakeReader { Fallback = new JsonObject { ["id"] = "different-provider", ["package"] = OpenAiApiProtocol.ResponsesPackage } };
        var result = await new ProviderRuntimeVerifier(reader).VerifyAsync("api-local", OpenAiApiProtocol.ResponsesPackage);
        Assert.Equal(ProviderRuntimeState.Unavailable, result.State);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task CancelledReadDoesNotReadOrMutateAnything()
    {
        var reader = new FakeReader();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProviderRuntimeVerifier(reader).VerifyAsync("api-local", OpenAiApiProtocol.ResponsesPackage, cancelled.Token));
        Assert.Equal(0, reader.Reads);
    }

    private static JsonObject Provider(string package) => new() { ["id"] = "api-local", ["package"] = package };

    private sealed class FakeReader : IProviderRuntimeReader
    {
        public int Reads;
        public List<string> ProviderIds = new();
        public Queue<JsonNode?> Responses = new();
        public JsonNode? Fallback;
        public Exception? Failure;
        public Task<JsonNode?> GetProviderAsync(string providerId, CancellationToken ct = default)
        {
            Reads++;
            ProviderIds.Add(providerId);
            if (Failure is not null) { throw Failure; }
            return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue() : Fallback);
        }
    }
}
