using System.Text.Json.Nodes;
using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

public class QuotaParseTests
{
    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void ParsesPercentWindows()
    {
        var usage = QuotaService.Parse(Node("""
        {"plan_type":"plus","rate_limit":{
          "primary_window":{"used_percent":42,"reset_at":4102444800},
          "secondary_window":{"used_percent":88,"reset_at":4102444800}}}
        """));

        Assert.Equal(42, usage.PrimaryPercent);
        Assert.Equal(88, usage.SecondaryPercent);
        Assert.Equal("Available", usage.PrimaryState);
        Assert.Equal("Available", usage.SecondaryState);
        Assert.Equal("plus", usage.Plan);
        Assert.NotNull(usage.PrimaryReset);
    }

    [Fact]
    public void ParsesUsedLimitRatio()
    {
        var usage = QuotaService.Parse(Node("""
        {"rate_limit":{"primary_window":{"used":25,"limit":100},"secondary_window":{"used":1,"total":4}}}
        """));

        Assert.Equal(25, usage.PrimaryPercent);
        Assert.Equal(25, usage.SecondaryPercent);
    }

    [Fact]
    public void ParsesRemainingPercentAsInverse()
    {
        var usage = QuotaService.Parse(Node("""
        {"rate_limit":{"primary_window":{"remaining_percent":70},"secondary_window":{"remaining_percent":10}}}
        """));

        Assert.Equal(30, usage.PrimaryPercent);
        Assert.Equal(90, usage.SecondaryPercent);
    }

    [Fact]
    public void DurationDecidesPlacementRegardlessOfName()
    {
        var usage = QuotaService.Parse(Node("""
        {"rate_limit":{
          "primary_window":{"limit_window_seconds":604800,"used_percent":80},
          "secondary_window":{"limit_window_seconds":18000,"used_percent":20}}}
        """));

        Assert.Equal(20, usage.PrimaryPercent);
        Assert.Equal(80, usage.SecondaryPercent);
    }

    [Fact]
    public void UnknownDurationMarksWindowInvalid()
    {
        var usage = QuotaService.Parse(Node("""
        {"rate_limit":{"primary_window":{"limit_window_seconds":999,"used_percent":10}}}
        """));

        Assert.Equal("Invalid", usage.PrimaryState);
        Assert.Contains("无法识别", usage.Message);
    }

    [Fact]
    public void MissingWindowStaysMissing()
    {
        var usage = QuotaService.Parse(Node("""
        {"rate_limit":{"primary_window":{"used_percent":10}}}
        """));

        Assert.Equal("Available", usage.PrimaryState);
        Assert.Equal("Missing", usage.SecondaryState);
        Assert.Contains("缺失", usage.Message);
    }

    [Fact]
    public void ExplicitUnlimitedWins()
    {
        var usage = QuotaService.Parse(Node("""{"rate_limit":{"unlimited":true}}"""));
        Assert.Equal("Unlimited", usage.PrimaryState);
        Assert.Equal("Unlimited", usage.SecondaryState);
        Assert.False(usage.LimitReached);
    }

    [Fact]
    public void ExplicitLimitReachedIsHonoured()
    {
        var usage = QuotaService.Parse(Node("""
        {"rate_limit":{"limit_reached":true,"primary_window":{"used_percent":10}}}
        """));
        Assert.True(usage.LimitReached);
    }

    [Fact]
    public void LimitReachedInferredFromFullWindow()
    {
        var usage = QuotaService.Parse(Node("""
        {"rate_limit":{"primary_window":{"used_percent":100},"secondary_window":{"used_percent":5}}}
        """));
        Assert.True(usage.LimitReached);
    }

    [Fact]
    public void MissingRateLimitThrows()
    {
        Assert.Throws<InvalidOperationException>(() => QuotaService.Parse(Node("""{"plan":"plus"}""")));
    }

    [Fact]
    public void ResetAfterSecondsBecomesFutureTime()
    {
        var usage = QuotaService.Parse(Node("""
        {"rate_limit":{"primary_window":{"used_percent":10,"reset_after_seconds":3600}}}
        """));
        Assert.NotNull(usage.PrimaryReset);
        Assert.True(usage.PrimaryReset!.Value > DateTimeOffset.UtcNow.AddMinutes(55));
    }

    [Fact]
    public void ParsesResetCreditsFromTopLevel()
    {
        var usage = QuotaService.Parse(Node("""
        {"plan_type":"team","rate_limit":{"primary_window":{"used_percent":10}},
         "rate_limit_reset_credits":{"applicable_available_count":0,"available_count":3}}
        """));

        Assert.Equal(3, usage.ResetCreditsAvailable);
    }

    [Fact]
    public void ParsesResetCreditsNestedUnderRateLimit()
    {
        var usage = QuotaService.Parse(Node("""
        {"rate_limit":{"primary_window":{"used_percent":10},"restart_placeholder":true,
         "rate_limit_reset_credits":{"available_count":2}}}
        """));

        Assert.Equal(2, usage.ResetCreditsAvailable);
    }

    [Fact]
    public void MissingResetCreditsStaysNull()
    {
        var usage = QuotaService.Parse(Node("""
        {"rate_limit":{"primary_window":{"used_percent":10}}}
        """));

        Assert.Null(usage.ResetCreditsAvailable);
    }

    [Theory]
    [InlineData(401, "过期")]
    [InlineData(403, "权限")]
    [InlineData(429, "频繁")]
    public void ErrorMessagesAreFixed(int status, string fragment)
    {
        Assert.Contains(fragment, QuotaService.ResolveError(status));
    }
}
