using System.Text.Json.Nodes;
using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

public class SubscriptionServiceTests
{
    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void AccountsCheckPrefersTheMatchingAccountId()
    {
        var json = Node("""
        {"accounts":{
          "personal":{"account":{"account_id":"personal","is_free":false},"entitlement":{"expires_at":"2026-10-09T00:00:00Z"}},
          "team":{"account":{"account_id":"team-1","is_free":false},"entitlement":{"expires_at":"2026-11-08T17:04:26Z"}}}}
        """);

        var status = SubscriptionService.ParseAccountsCheck(json, "team-1");

        Assert.Equal(DateTimeOffset.Parse("2026-11-08T17:04:26Z"), status.ActiveUntil);
    }

    [Fact]
    public void AccountsCheckWithoutKnownIdFallsBackToDefaultThenNonFree()
    {
        var json = Node("""
        {"accounts":[
          {"account":{"account_id":"a","is_free":true},"entitlement":{"expires_at":"2026-10-09T00:00:00Z"}},
          {"account":{"account_id":"b","is_default":true,"is_free":false},"entitlement":{"expires_at":"2026-11-08T00:00:00Z"}}]}
        """);

        var status = SubscriptionService.ParseAccountsCheck(json, string.Empty);

        Assert.Equal(DateTimeOffset.Parse("2026-11-08T00:00:00Z"), status.ActiveUntil);
    }

    [Fact]
    public void AccountsCheckWithUnmatchedIdReturnsNothing()
    {
        // A specific workspace was asked for; borrowing another one's date would mislabel it.
        var json = Node("""
        {"accounts":{
          "other":{"account":{"account_id":"other","is_default":true,"is_free":false},"entitlement":{"expires_at":"2030-01-01T00:00:00Z"}}}}
        """);

        Assert.Null(SubscriptionService.ParseAccountsCheck(json, "requested").ActiveUntil);
    }

    [Fact]
    public void AccountsCheckMatchedEntryNeverBorrowsAnotherExpiry()
    {
        var json = Node("""
        {"accounts":{
          "t":{"account":{"account_id":"t","plan_type":"team"}},
          "other":{"account":{"account_id":"other","is_default":true},"entitlement":{"expires_at":"2030-01-01T00:00:00Z"}}}}
        """);

        var status = SubscriptionService.ParseAccountsCheck(json, "t");

        Assert.Null(status.ActiveUntil);
        Assert.Equal("team", status.PlanType);
    }

    [Fact]
    public void AccountsCheckReadsEpochSeconds()
    {
        var json = Node("""{"accounts":{"team":{"account":{"account_id":"t"},"entitlement":{"expires_at":4102444800}}}}""");

        var status = SubscriptionService.ParseAccountsCheck(json, "t");

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), status.ActiveUntil);
    }

    [Fact]
    public void AccountsCheckReadsPlanType()
    {
        var json = Node("""{"accounts":{"t":{"account":{"account_id":"t","plan_type":"plus"},"entitlement":{"expires_at":"2030-01-02T03:04:05Z"}}}}""");

        var status = SubscriptionService.ParseAccountsCheck(json, "t");

        Assert.Equal("plus", status.PlanType);
    }

    [Fact]
    public void AccountsCheckWithoutExpiryReturnsNothing()
    {
        var json = Node("""{"accounts":{"t":{"account":{"account_id":"t"}}}}""");
        Assert.Null(SubscriptionService.ParseAccountsCheck(json, "t").ActiveUntil);
    }

    [Fact]
    public void SubscriptionsReadsActiveUntilThroughDataWrapper()
    {
        var json = Node("""{"data":{"active_until":"2026-11-08T17:04:26Z","plan_type":"plus"}}""");

        var status = SubscriptionService.ParseSubscriptions(json);

        Assert.Equal(DateTimeOffset.Parse("2026-11-08T17:04:26Z"), status.ActiveUntil);
        Assert.Equal("plus", status.PlanType);
    }

    [Fact]
    public void SubscriptionsReadsArrayFirstElement()
    {
        var json = Node("""[{"expires_at":"2030-01-02T03:04:05Z"}]""");
        Assert.Equal(DateTimeOffset.Parse("2030-01-02T03:04:05Z"), SubscriptionService.ParseSubscriptions(json).ActiveUntil);
    }

    [Fact]
    public void SubscriptionsReadsEmptyObjectAsNothing()
        => Assert.Null(SubscriptionService.ParseSubscriptions(Node("""{}""")).ActiveUntil);

    private sealed class FakeTransportService : SubscriptionService
    {
        private readonly Func<string, JsonNode?> _respond;

        public FakeTransportService(Func<string, JsonNode?> respond) => _respond = respond;

        public List<string> Urls { get; } = new();

        protected override bool TrySystemCurl => false;

        protected override Task<JsonNode> SendAsync(string url, string accessToken, string accountId, CancellationToken cancellationToken)
        {
            Urls.Add(url);
            var node = _respond(url);
            if (node is null) { throw new InvalidOperationException("no canned response"); }
            return Task.FromResult(node);
        }
    }

    [Fact]
    public async Task ExpiredAccountsCheckFallsBackToSubscriptions()
    {
        var service = new FakeTransportService(url => url.Contains("accounts/check")
            ? Node("""{"accounts":{"t":{"account":{"account_id":"t"},"entitlement":{"expires_at":"2000-01-01T00:00:00Z"}}}}""")
            : Node("""{"active_until":"2099-01-02T03:04:05Z"}"""));

        var status = await service.GetSubscriptionAsync("token", "t");

        Assert.Equal(DateTimeOffset.Parse("2099-01-02T03:04:05Z"), status.ActiveUntil);
        Assert.Equal(2, service.Urls.Count);
        Assert.Contains("subscriptions", service.Urls[1]);
    }

    [Fact]
    public async Task FreshAccountsCheckSkipsSubscriptionsFallback()
    {
        var service = new FakeTransportService(_ =>
            Node("""{"accounts":{"t":{"account":{"account_id":"t"},"entitlement":{"expires_at":"2099-01-02T03:04:05Z"}}}}"""));

        var status = await service.GetSubscriptionAsync("token", "t");

        Assert.Equal(DateTimeOffset.Parse("2099-01-02T03:04:05Z"), status.ActiveUntil);
        Assert.Single(service.Urls);
    }

    [Fact]
    public async Task FallbackFailureWithNoPrimaryValueThrowsInsteadOfReportingEmpty()
    {
        // accounts/check matched but had no expiry; the fallback then failed. That is "unknown", not
        // "none" — it must surface as an error so the caller keeps the existing value.
        var service = new FakeTransportService(url => url.Contains("accounts/check")
            ? Node("""{"accounts":{"t":{"account":{"account_id":"t"}}}}""")
            : throw new InvalidOperationException("网络不可达"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSubscriptionAsync("token", "t"));
    }

    [Fact]
    public async Task FallbackFailureAfterExpiredPrimaryAlsoThrows()
    {
        // The expired primary is not a usable answer; a failed fallback must not surface it as if it
        // were current.
        var service = new FakeTransportService(url => url.Contains("accounts/check")
            ? Node("""{"accounts":{"t":{"account":{"account_id":"t"},"entitlement":{"expires_at":"2000-01-01T00:00:00Z"}}}}""")
            : throw new InvalidOperationException("网络不可达"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSubscriptionAsync("token", "t"));
    }

    [Fact]
    public async Task SuccessfulButEmptyBothEndpointsReportsEmpty()
    {
        // Both endpoints answered and neither carried an expiry: a genuine authoritative "none".
        var service = new FakeTransportService(url => url.Contains("accounts/check")
            ? Node("""{"accounts":{"t":{"account":{"account_id":"t"}}}}""")
            : Node("""{}"""));

        var status = await service.GetSubscriptionAsync("token", "t");

        Assert.Null(status.ActiveUntil);
        Assert.Equal(2, service.Urls.Count);
    }

    [Fact]
    public void CurlOutputSplitsBodyAndStatus()
    {
        var (status, body) = SubscriptionService.ParseCurlOutput(
            "{\"ok\":true}\n" + SubscriptionService.CurlStatusMarker + "200___");

        Assert.Equal(200, status);
        Assert.Equal("{\"ok\":true}", body);
    }

    [Fact]
    public void CurlOutputKeepsMultilineBodyIntact()
    {
        var payload = "{\n  \"a\": 1\n}";
        var (status, body) = SubscriptionService.ParseCurlOutput(
            payload + "\n" + SubscriptionService.CurlStatusMarker + "403___");

        Assert.Equal(403, status);
        Assert.Equal(payload, body);
    }

    [Fact]
    public void CurlOutputWithoutMarkerReportsNoStatus()
    {
        var (status, body) = SubscriptionService.ParseCurlOutput("network broke");

        Assert.Equal(0, status);
        Assert.Equal("network broke", body);
    }

    [Fact]
    public void CurlOutputWithEmptyInputIsEmpty()
    {
        var (status, body) = SubscriptionService.ParseCurlOutput(string.Empty);

        Assert.Equal(0, status);
        Assert.Equal(string.Empty, body);
    }
}
