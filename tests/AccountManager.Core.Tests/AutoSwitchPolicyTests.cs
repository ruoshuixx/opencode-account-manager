using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

public class AutoSwitchPolicyTests
{
    private static AutoSwitchCandidate C(double? primaryRemaining, double? secondaryRemaining = null)
        => new("k", "k", new QuotaUsage
        {
            PrimaryPercent = primaryRemaining is null ? null : 100 - primaryRemaining,
            SecondaryPercent = secondaryRemaining is null ? null : 100 - secondaryRemaining,
        });

    [Theory]
    [InlineData(5.0, true)]
    [InlineData(5.1, false)]
    [InlineData(0.0, true)]
    public void FiveHourReserveUsesAtOrBelowThreshold(double remaining, bool expected)
        => Assert.Equal(expected, AutoSwitchPolicy.IsBelowReserve(C(remaining)));

    [Fact]
    public void WeeklyWindowCanTriggerEvenWhenFiveHourIsHealthy()
    {
        // 5h fine, weekly nearly gone -> still switch (dual-window OR, like Cockpit).
        Assert.True(AutoSwitchPolicy.IsBelowReserve(C(primaryRemaining: 80, secondaryRemaining: 1)));
    }

    [Fact]
    public void HealthyWindowsDoNotTrigger()
        => Assert.False(AutoSwitchPolicy.IsBelowReserve(C(primaryRemaining: 80, secondaryRemaining: 50)));

    [Theory]
    [InlineData(2.0, true)]
    [InlineData(2.1, false)]
    public void WeeklyOnlyAccountUsesWeeklyReserve(double remaining, bool expected)
        => Assert.Equal(expected, AutoSwitchPolicy.IsBelowReserve(C(primaryRemaining: null, secondaryRemaining: remaining)));

    [Fact]
    public void NoReportedWindowNeverSwitches()
    {
        var unknown = C(primaryRemaining: null, secondaryRemaining: null);
        Assert.False(AutoSwitchPolicy.IsBelowReserve(unknown));
        Assert.False(AutoSwitchPolicy.IsExhausted(unknown));
        Assert.False(AutoSwitchPolicy.ShouldSwitch(unknown, burnPerMinute: 10, TimeSpan.FromMinutes(5)));
        Assert.Null(AutoSwitchPolicy.Remaining(unknown));
    }

    [Fact]
    public void ExhaustedWhenEitherWindowIsSpent()
    {
        Assert.True(AutoSwitchPolicy.IsExhausted(C(primaryRemaining: 0)));
        Assert.True(AutoSwitchPolicy.IsExhausted(C(primaryRemaining: 80, secondaryRemaining: 0)));
        Assert.False(AutoSwitchPolicy.IsExhausted(C(primaryRemaining: 80, secondaryRemaining: 50)));
    }

    [Fact]
    public void ReserveAndRemainingFollowTheGoverningWindow()
    {
        var five = C(primaryRemaining: 40, secondaryRemaining: 90);
        Assert.Equal(AutoSwitchPolicy.Reserve5hPercent, AutoSwitchPolicy.Reserve(five));
        Assert.Equal(40, AutoSwitchPolicy.Remaining(five));

        var weekly = C(primaryRemaining: null, secondaryRemaining: 60);
        Assert.Equal(AutoSwitchPolicy.ReserveWeeklyPercent, AutoSwitchPolicy.Reserve(weekly));
        Assert.Equal(60, AutoSwitchPolicy.Remaining(weekly));
    }

    [Fact]
    public void ShouldSwitchFiresAtTheReserveEvenWhenIdle()
    {
        // 5h reserve is 5%: at or below it we switch regardless of speed.
        Assert.True(AutoSwitchPolicy.ShouldSwitch(C(primaryRemaining: 5), burnPerMinute: 0, TimeSpan.FromMinutes(2)));
        Assert.False(AutoSwitchPolicy.ShouldSwitch(C(primaryRemaining: 5.1), burnPerMinute: 0, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void ShouldSwitchLeavesASpeedMarginAboveTheReserve()
    {
        var lead = TimeSpan.FromMinutes(2);
        // 2%/min over a 2 min lead = 4% margin -> fires within 4% of the 5% reserve.
        Assert.True(AutoSwitchPolicy.ShouldSwitch(C(primaryRemaining: 8.5), burnPerMinute: 2, lead));
        Assert.False(AutoSwitchPolicy.ShouldSwitch(C(primaryRemaining: 10), burnPerMinute: 2, lead));
    }

    [Fact]
    public void FasterBurnLeavesMoreMargin()
    {
        var lead = TimeSpan.FromMinutes(1);
        Assert.False(AutoSwitchPolicy.ShouldSwitch(C(primaryRemaining: 12), burnPerMinute: 1, lead));
        Assert.True(AutoSwitchPolicy.ShouldSwitch(C(primaryRemaining: 12), burnPerMinute: 8, lead));
    }

    [Fact]
    public void WeeklyReserveIsAlsoPredictive()
    {
        var lead = TimeSpan.FromMinutes(1);
        // 2% margin above the 2% weekly reserve -> fires at 4%.
        Assert.True(AutoSwitchPolicy.ShouldSwitch(C(null, secondaryRemaining: 4), burnPerMinute: 2, lead));
        Assert.False(AutoSwitchPolicy.ShouldSwitch(C(null, secondaryRemaining: 5), burnPerMinute: 2, lead));
    }
}
