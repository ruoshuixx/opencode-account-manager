using AccountManager.Core.Accounts;
using Xunit;

namespace AccountManager.Core.Tests;

public class SubscriptionClockTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");

    [Fact]
    public void MissingExpiryShowsDash()
        => Assert.Equal("—", SubscriptionClock.FormatRemaining(null, Now));

    [Fact]
    public void PastExpiryShowsExpired()
        => Assert.Equal("已到期", SubscriptionClock.FormatRemaining(Now.AddMinutes(-1), Now));

    [Fact]
    public void AtLeastADayRoundsUpToWholeDays()
    {
        Assert.Equal("1天", SubscriptionClock.FormatRemaining(Now.AddDays(1), Now));
        Assert.Equal("3天", SubscriptionClock.FormatRemaining(Now.AddDays(2.5), Now));
        Assert.Equal("31天", SubscriptionClock.FormatRemaining(Now.AddDays(30.5), Now));
    }

    [Fact]
    public void UnderADayRoundsUpToHours()
    {
        Assert.Equal("6小时", SubscriptionClock.FormatRemaining(Now.AddHours(5).AddMinutes(30), Now));
        Assert.Equal("23小时", SubscriptionClock.FormatRemaining(Now.AddHours(22).AddMinutes(30), Now));
        // A partial day rounds up across the boundary.
        Assert.Equal("1天", SubscriptionClock.FormatRemaining(Now.AddHours(23.9), Now));
    }

    [Fact]
    public void UnderAnHourRoundsUpToMinutes()
    {
        Assert.Equal("30分", SubscriptionClock.FormatRemaining(Now.AddMinutes(29).AddSeconds(30), Now));
        Assert.Equal("1分", SubscriptionClock.FormatRemaining(Now.AddMinutes(1), Now));
        // A partial hour rounds up across the boundary.
        Assert.Equal("1小时", SubscriptionClock.FormatRemaining(Now.AddMinutes(59).AddSeconds(30), Now));
    }

    [Fact]
    public void UnderAMinuteStillReadsOneMinute()
        => Assert.Equal("1分", SubscriptionClock.FormatRemaining(Now.AddSeconds(20), Now));
}
