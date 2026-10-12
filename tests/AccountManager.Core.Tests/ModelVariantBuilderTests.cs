using System.Text.Json.Nodes;
using AccountManager.Core.OpenCode;
using Xunit;

namespace AccountManager.Core.Tests;

public class ModelVariantBuilderTests
{
    private const string ArkUrl = "https://ark.cn-beijing.volces.com/api/coding/v3";
    [Theory]
    [InlineData("deepseek-v4-pro", "low,high")]
    [InlineData("deepseek-v4.1-flash", "low,high")]
    [InlineData("glm-5.3-flash", "low,high,max")]
    [InlineData("glm-5.2", "high,max")]
    [InlineData("doubao-seed-2.1-turbo", "low,medium,high")]
    public void ResponsesEntryUsesExpectedBodyShape(string modelId, string expected)
    {
        var entry = ModelVariantBuilder.NewModelEntry(modelId, true, ArkUrl);
        var variants = entry["variants"]!.AsArray();
        Assert.Equal(modelId, entry["name"]!.GetValue<string>());
        Assert.Equal(expected.Split(','), variants.Select(v => v!["id"]!.GetValue<string>()));
        foreach (var variant in variants)
        {
            Assert.Equal(variant!["id"]!.GetValue<string>(), variant["body"]!["reasoning"]!["effort"]!.GetValue<string>());
            Assert.DoesNotContain(variant["id"]!.GetValue<string>(), new[] { "none", "minimal" });
        }
    }

    [Theory]
    [InlineData("glm-5.3")]
    [InlineData("kimi-k2.7-code")]
    [InlineData("minimax-m3")]
    [InlineData("ark-code-latest")]
    [InlineData("deepseek-v4-professional")]
    [InlineData("unknown-model")]
    public void UnlistedModelDoesNotGetVariants(string modelId)
    {
        Assert.False(ModelVariantBuilder.NewModelEntry(modelId, true, ArkUrl).ContainsKey("variants"));
    }

    [Fact]
    public void ChatDoesNotGetVariants()
    {
        Assert.False(ModelVariantBuilder.NewModelEntry("deepseek-v4-pro", false, ArkUrl).ContainsKey("variants"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[{\"id\":\"custom\"}]")]
    public void BuilderPreservesExplicitVariants(string json)
    {
        var entry = new JsonObject { ["name"] = "deepseek-v4-pro", ["variants"] = JsonNode.Parse(json) };
        var before = entry.DeepClone();
        Assert.False(ModelVariantBuilder.TryAddMissingVariants(entry, "deepseek-v4-pro", true, ArkUrl));
        Assert.True(JsonNode.DeepEquals(before, entry));
    }

    [Theory]
    [InlineData("https://ark.cn-beijing.volces.com/api/coding/v3", true)]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v3/", true)]
    [InlineData("https://ARK.CN-BEIJING.VOLCES.COM/api/coding/v3/", true)]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", false)]
    [InlineData("https://example.test/api/coding/v3", false)]
    [InlineData("https://ark.cn-beijing.volces.com.example.test/api/plan/v3", false)]
    [InlineData("https://ark.cn-beijing.volces.com@evil.test/api/plan/v3", false)]
    [InlineData("https://user@ark.cn-beijing.volces.com/api/plan/v3", false)]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v3/responses", false)]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v30", false)]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v3?x=1", false)]
    [InlineData("https://ark.cn-beijing.volces.com/api/plan/v3#x", false)]
    [InlineData("https://ark.cn-beijing.volces.com:8443/api/plan/v3", false)]
    [InlineData("http://ark.cn-beijing.volces.com/api/plan/v3", false)]
    [InlineData("", false)]
    public void UrlLimitsGenerationToTheTwoArkPlans(string url, bool expected)
    {
        Assert.Equal(expected, ModelVariantBuilder.IsArkPlanEndpoint(url));
        Assert.Equal(expected, ModelVariantBuilder.NewModelEntry("deepseek-v4-pro", true, url).ContainsKey("variants"));
    }
}
