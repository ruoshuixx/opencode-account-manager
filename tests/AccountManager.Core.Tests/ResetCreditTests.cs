using System.Net.Http;
using System.Text.Json.Nodes;
using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

public class ResetCreditTests
{
    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void ParsesCreditListWithDatesAndCount()
    {
        var list = ResetCreditService.ParseList(Node("""
        {
          "available_count": 2,
          "credits": [
            {"id":"RateLimitResetCredit_a","reset_type":"codex_rate_limits","status":"available",
             "granted_at":"2026-09-04T10:37:00Z","expires_at":"2026-10-04T10:37:00Z","title":"One free reset"},
            {"id":"RateLimitResetCredit_b","reset_type":"codex_rate_limits","status":"redeemed",
             "granted_at":"2026-09-01T00:00:00Z","expires_at":"2026-10-01T00:00:00Z",
             "redeemed_at":"2026-09-02T00:00:00Z"},
            {"id":"RateLimitResetCredit_c","reset_type":"codex_rate_limits","status":"available",
             "granted_at":"2026-09-29T07:15:00Z","expires_at":"2026-10-29T07:15:00Z"}
          ]
        }
        """));

        Assert.Equal(2, list.AvailableCount);
        Assert.Equal(3, list.Credits.Count);
        Assert.Equal("RateLimitResetCredit_a", list.Credits[0].Id);
        Assert.Equal("One free reset", list.Credits[0].Title);
        Assert.NotNull(list.Credits[0].GrantedAt);
        Assert.NotNull(list.Credits[0].ExpiresAt);
        Assert.True(list.Credits[0].IsAvailable);
        Assert.False(list.Credits[1].IsAvailable);
        Assert.NotNull(list.Credits[1].RedeemedAt);
    }

    [Fact]
    public void AvailableByExpiryOrdersSoonestFirst()
    {
        var list = ResetCreditService.ParseList(Node("""
        {"credits":[
          {"id":"late","status":"available","expires_at":"2026-10-29T07:15:00Z"},
          {"id":"soon","status":"available","expires_at":"2026-10-04T10:37:00Z"},
          {"id":"used","status":"redeemed","expires_at":"2026-01-01T00:00:00Z"}
        ]}
        """));

        Assert.Equal(new[] { "soon", "late" }, list.AvailableByExpiry.Select(c => c.Id));
        Assert.Equal(2, list.AvailableCount);
    }

    [Fact]
    public void MissingCountFallsBackToAvailableCredits()
    {
        var list = ResetCreditService.ParseList(Node("""
        {"credits":[{"id":"a","status":"available"},{"id":"b","status":"expired"}]}
        """));
        Assert.Equal(1, list.AvailableCount);
    }

    [Fact]
    public void EmptyListParsesToZero()
    {
        var list = ResetCreditService.ParseList(Node("""{"credits":[],"available_count":0}"""));
        Assert.Empty(list.Credits);
        Assert.Equal(0, list.AvailableCount);
    }

    [Fact]
    public void ParsesConsumeResult()
    {
        var result = ResetCreditService.ParseConsume(Node("""
        {"code":"reset","windows_reset":1,
         "credit":{"id":"RateLimitResetCredit_a","status":"redeemed","redeemed_at":"2026-09-30T12:00:00Z"}}
        """));

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.WindowsReset);
        Assert.Equal("RateLimitResetCredit_a", result.CreditId);
        Assert.NotNull(result.RedeemedAt);
    }

    [Fact]
    public void UnknownConsumeCodeIsNotSuccess()
    {
        var result = ResetCreditService.ParseConsume(Node("""{"code":"nope","windows_reset":0}"""));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void FormatExpiryUsesDaysAndHours()
    {
        var now = DateTimeOffset.Parse("2026-10-02T09:00:00Z");
        Assert.Equal("2d9h", ResetCreditService.FormatExpiry(now.AddDays(2).AddHours(9), now));
        Assert.Equal("3h12m", ResetCreditService.FormatExpiry(now.AddHours(3).AddMinutes(12), now));
        Assert.Equal("已过期", ResetCreditService.FormatExpiry(now.AddMinutes(-1), now));
        Assert.Equal(string.Empty, ResetCreditService.FormatExpiry(null, now));
    }

    [Fact]
    public async Task ConsumePostsCreditIdAndRedeemRequestId()
    {
        var fake = new CapturingResetCreditService();
        var result = await fake.ConsumeAsync("token", "acct", "RateLimitResetCredit_a");

        Assert.True(result.Succeeded);
        Assert.Equal(HttpMethod.Post, fake.Method);
        Assert.Equal(ResetCreditService.ConsumeUri, fake.Url);
        Assert.Equal("RateLimitResetCredit_a", fake.Body?["credit_id"]?.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(fake.Body?["redeem_request_id"]?.GetValue<string>()));
    }

    [Fact]
    public async Task ListUsesGetOnListUri()
    {
        var fake = new CapturingResetCreditService();
        var list = await fake.GetListAsync("token", "acct");

        Assert.Equal(HttpMethod.Get, fake.Method);
        Assert.Equal(ResetCreditService.ListUri, fake.Url);
        Assert.Null(fake.Body);
        Assert.Equal(1, list.AvailableCount);
    }

    private sealed class CapturingResetCreditService : ResetCreditService
    {
        public HttpMethod? Method { get; private set; }

        public string? Url { get; private set; }

        public JsonObject? Body { get; private set; }

        protected override Task<JsonNode> SendAsync(HttpMethod method, string url, string accessToken, string accountId, JsonObject? body, CancellationToken cancellationToken)
        {
            Method = method;
            Url = url;
            Body = body;
            JsonNode response = method == HttpMethod.Post
                ? Node("""{"code":"reset","windows_reset":1,"credit":{"id":"RateLimitResetCredit_a","redeemed_at":"2026-09-30T12:00:00Z"}}""")
                : Node("""{"available_count":1,"credits":[{"id":"RateLimitResetCredit_a","status":"available","expires_at":"2026-10-04T10:37:00Z"}]}""");
            return Task.FromResult(response);
        }
    }
}
