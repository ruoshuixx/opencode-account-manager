using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

public class SubscriptionRefresherTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
    private static readonly SubscriptionStatus Status = new()
    {
        ActiveUntil = DateTimeOffset.Parse("2026-11-08T00:00:00Z"),
    };

    [Fact]
    public async Task NearExpiryLookupRunsOnceThenThrottles()
    {
        var fake = new FakeService(Status);
        var refresher = new SubscriptionRefresher(fake);

        var first = await refresher.TryRefreshAsync("k", "token", "acct", Now.AddHours(1), Now, force: false);
        Assert.NotNull(first);
        Assert.Equal(1, fake.Calls);

        var second = await refresher.TryRefreshAsync("k", "token", "acct", Now.AddHours(1), Now.AddMinutes(5), force: false);
        Assert.Null(second);
        Assert.Equal(1, fake.Calls);

        // Past the 30-minute interval the throttled account is queried again.
        var third = await refresher.TryRefreshAsync("k", "token", "acct", Now.AddHours(1), Now.AddMinutes(31), force: false);
        Assert.NotNull(third);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task HealthyAccountIsNotQueried()
    {
        var fake = new FakeService(Status);
        var refresher = new SubscriptionRefresher(fake);

        var status = await refresher.TryRefreshAsync("k", "token", "acct", Now.AddDays(30), Now, force: false);

        Assert.Null(status);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task ForceBypassesThrottleAndGate()
    {
        var fake = new FakeService(Status);
        var refresher = new SubscriptionRefresher(fake);

        await refresher.TryRefreshAsync("k", "token", "acct", Now.AddDays(30), Now, force: true);
        await refresher.TryRefreshAsync("k", "token", "acct", Now.AddDays(30), Now.AddMinutes(1), force: true);

        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task FailureReturnsNullAndStillThrottles()
    {
        var fake = new FakeService(null);
        var refresher = new SubscriptionRefresher(fake);

        Assert.Null(await refresher.TryRefreshAsync("k", "token", "acct", Now.AddHours(1), Now, force: false));
        Assert.Null(await refresher.TryRefreshAsync("k", "token", "acct", Now.AddHours(1), Now.AddMinutes(5), force: false));
        Assert.Equal(1, fake.Calls);
    }

    private sealed class FakeService : SubscriptionService
    {
        private readonly SubscriptionStatus? _status;

        public FakeService(SubscriptionStatus? status) => _status = status;

        public int Calls { get; private set; }

        public override Task<SubscriptionStatus> GetSubscriptionAsync(string accessToken, string accountId, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_status is null) { throw new InvalidOperationException("boom"); }
            return Task.FromResult(_status);
        }
    }
}
