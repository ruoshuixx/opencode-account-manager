using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

public class AutoPollSchedulerTests
{
    private static readonly TimeSpan Normal = TimeSpan.FromMinutes(5);

    [Fact]
    public void UnknownRemainingKeepsTheNormalInterval()
        => Assert.Equal(Normal, AutoPollScheduler.NextInterval(null, 15, 5, Normal));

    [Fact]
    public void IdleAccountRelaxesToTheNormalInterval()
        => Assert.Equal(Normal, AutoPollScheduler.NextInterval(20, 15, 0, Normal));

    [Fact]
    public void TinyRateIsStillIdle()
        => Assert.Equal(Normal, AutoPollScheduler.NextInterval(20, 15, AutoPollScheduler.IdleEpsilonPerMinute, Normal));

    [Fact]
    public void SlowBurnFarFromTheReserveKeepsTheNormalInterval()
    {
        // 55% headroom at 1%/min = 55 min to the reserve -> far slower than the 5 min ceiling.
        Assert.Equal(Normal, AutoPollScheduler.NextInterval(70, 15, 1, Normal));
    }

    [Fact]
    public void FastBurnNearTheReserveUsesTheFloor()
    {
        // 1% headroom at 10%/min = 6 s to the reserve -> clamp to the 15 s floor.
        Assert.Equal(AutoPollScheduler.MinInterval, AutoPollScheduler.NextInterval(16, 15, 10, Normal));
    }

    [Fact]
    public void AlreadyAtOrBelowTheReserveUsesTheFloor()
    {
        Assert.Equal(AutoPollScheduler.MinInterval, AutoPollScheduler.NextInterval(14, 15, 2, Normal));
        Assert.Equal(AutoPollScheduler.MinInterval, AutoPollScheduler.NextInterval(15, 15, 2, Normal));
    }

    [Fact]
    public void IntervalScalesWithTheTimeLeftBeforeTheReserve()
    {
        // 25% headroom at 2%/min = 12.5 min to the reserve; /3 polls -> ~4.17 min (< 5 min ceiling).
        var interval = AutoPollScheduler.NextInterval(40, 15, 2, Normal);
        Assert.True(interval < Normal);
        Assert.True(interval > AutoPollScheduler.MinInterval);
        Assert.Equal(250, interval.TotalSeconds, 1);
    }

    [Fact]
    public void SwitchLeadAddsTheSwitchLatencyToOnePoll()
    {
        var poll = TimeSpan.FromSeconds(20);
        Assert.Equal(poll + AutoPollScheduler.SwitchLatency, AutoPollScheduler.SwitchLead(poll));
    }
}
