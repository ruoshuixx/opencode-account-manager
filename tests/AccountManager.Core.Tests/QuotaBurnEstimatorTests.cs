using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

public class QuotaBurnEstimatorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void FirstSampleHasNoRate()
    {
        var estimator = new QuotaBurnEstimator();
        estimator.Record("a", 80, T0);
        Assert.Equal(0, estimator.BurnPerMinute("a"));
        Assert.Equal(0, estimator.BurnPerMinute("missing"));
    }

    [Fact]
    public void SteadyDropProducesThatRate()
    {
        var estimator = new QuotaBurnEstimator();
        estimator.Record("a", 80, T0);
        estimator.Record("a", 74, T0.AddMinutes(2)); // 6% in 2 min = 3%/min
        Assert.Equal(3.0, estimator.BurnPerMinute("a"), 3);
    }

    [Fact]
    public void StaticSamplesDecayTowardIdle()
    {
        var estimator = new QuotaBurnEstimator();
        estimator.Record("a", 80, T0);
        estimator.Record("a", 74, T0.AddMinutes(2)); // 3%/min
        var afterDrop = estimator.BurnPerMinute("a");

        estimator.Record("a", 74, T0.AddMinutes(4)); // no further drop
        var afterIdle = estimator.BurnPerMinute("a");

        Assert.True(afterIdle < afterDrop);
        Assert.Equal(afterDrop * (1 - QuotaBurnEstimator.Alpha), afterIdle, 3);
    }

    [Fact]
    public void AResetToAHigherReadingCountsAsNoBurn()
    {
        var estimator = new QuotaBurnEstimator();
        estimator.Record("a", 5, T0);
        estimator.Record("a", 100, T0.AddMinutes(1)); // window reset
        Assert.Equal(0, estimator.BurnPerMinute("a"));
    }

    [Fact]
    public void VeryShortGapsAreIgnoredAsNoise()
    {
        var estimator = new QuotaBurnEstimator();
        estimator.Record("a", 80, T0);
        estimator.Record("a", 79, T0.AddSeconds(5)); // below MinElapsedMinutes
        Assert.Equal(0, estimator.BurnPerMinute("a"));
    }

    [Fact]
    public void NullReadingsAreIgnored()
    {
        var estimator = new QuotaBurnEstimator();
        estimator.Record("a", null, T0);
        estimator.Record("a", null, T0.AddMinutes(1));
        Assert.Equal(0, estimator.BurnPerMinute("a"));
    }
}
