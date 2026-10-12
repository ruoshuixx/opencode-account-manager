using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

public class SubscriptionRefreshPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");

    [Fact]
    public void ForceAlwaysQueries()
        => Assert.True(SubscriptionRefreshPolicy.ShouldQuery(Now.AddDays(30), Now, force: true));

    [Fact]
    public void MissingValueQueries()
        => Assert.True(SubscriptionRefreshPolicy.ShouldQuery(null, Now, force: false));

    [Fact]
    public void ExpiredValueQueries()
        => Assert.True(SubscriptionRefreshPolicy.ShouldQuery(Now.AddMinutes(-1), Now, force: false));

    [Fact]
    public void WithinSixHourWindowQueries()
    {
        Assert.True(SubscriptionRefreshPolicy.ShouldQuery(Now.AddHours(5), Now, force: false));
        Assert.True(SubscriptionRefreshPolicy.ShouldQuery(Now.AddHours(6), Now, force: false));
    }

    [Fact]
    public void BeyondSixHourWindowSkips()
    {
        Assert.False(SubscriptionRefreshPolicy.ShouldQuery(Now.AddHours(6).AddSeconds(1), Now, force: false));
        Assert.False(SubscriptionRefreshPolicy.ShouldQuery(Now.AddDays(30), Now, force: false));
    }
}
