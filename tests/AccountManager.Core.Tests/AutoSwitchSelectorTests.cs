using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

public class AutoSwitchSelectorTests
{
    private static QuotaUsage Usage(double? primaryUsed, double? secondaryUsed = null, DateTimeOffset? primaryReset = null, DateTimeOffset? secondaryReset = null)
        => new()
        {
            PrimaryPercent = primaryUsed,
            PrimaryReset = primaryReset,
            SecondaryPercent = secondaryUsed,
            SecondaryReset = secondaryReset,
        };

    private static AutoSwitchCandidate C(string key, QuotaUsage usage, string team = "") => new(key, key, usage, team);

    [Fact]
    public void PicksNearestResetAmongAccountsAbove25Percent()
    {
        // remaining = 100 - used
        var full = C("full", Usage(primaryUsed: 10, primaryReset: DateTimeOffset.UtcNow.AddHours(4)));   // 90% left, resets in 4h
        var mid = C("mid", Usage(primaryUsed: 60, primaryReset: DateTimeOffset.UtcNow.AddMinutes(20)));   // 40% left, resets in 20min
        var low = C("low", Usage(primaryUsed: 80, primaryReset: DateTimeOffset.UtcNow.AddMinutes(5)));    // 20% left

        var target = AutoSwitchSelector.Select(new[] { full, mid, low });

        Assert.NotNull(target);
        Assert.Equal("mid", target!.Key);
    }

    [Fact]
    public void FallsBackToMostRemainingWhenNobodyAbove25()
    {
        var a = C("a", Usage(primaryUsed: 85, primaryReset: DateTimeOffset.UtcNow.AddMinutes(5)));   // 15% left, resets soon
        var b = C("b", Usage(primaryUsed: 92, primaryReset: DateTimeOffset.UtcNow.AddHours(3)));     // 8% left

        var target = AutoSwitchSelector.Select(new[] { a, b });

        Assert.NotNull(target);
        Assert.Equal("a", target!.Key);
    }

    [Fact]
    public void WeeklyOnlyAccountWinsWhenAllFiveHourWindowsAreZero()
    {
        var dead1 = C("dead1", Usage(primaryUsed: 100, primaryReset: DateTimeOffset.UtcNow.AddHours(2)));
        var dead2 = C("dead2", Usage(primaryUsed: 100, primaryReset: DateTimeOffset.UtcNow.AddMinutes(30)));
        var pro = C("pro", Usage(primaryUsed: null, secondaryUsed: 40, secondaryReset: DateTimeOffset.UtcNow.AddDays(3))); // no 5h window, 60% weekly

        var target = AutoSwitchSelector.Select(new[] { dead1, dead2, pro });

        Assert.NotNull(target);
        Assert.Equal("pro", target!.Key);
    }

    [Fact]
    public void ReturnsNullWhenEverythingIsExhausted()
    {
        var dead = C("dead", Usage(primaryUsed: 100));
        var pro = C("pro", Usage(primaryUsed: null, secondaryUsed: 100)); // weekly gone too

        var target = AutoSwitchSelector.Select(new[] { dead, pro });

        Assert.Null(target);
    }

    [Fact]
    public void WeeklyExhaustedAccountIsSkippedEvenWithFiveHourBalance()
    {
        // 5h has room but the weekly cap is spent → switching there would stall again immediately.
        var weeklyDead = C("weeklyDead", Usage(primaryUsed: 20, secondaryUsed: 100, primaryReset: DateTimeOffset.UtcNow.AddHours(3)));
        var healthy = C("healthy", Usage(primaryUsed: 30, secondaryUsed: 20));

        var target = AutoSwitchSelector.Select(new[] { weeklyDead, healthy });

        Assert.NotNull(target);
        Assert.Equal("healthy", target!.Key);
    }

    [Fact]
    public void WeeklyExhaustedAccountAloneYieldsNoSwitch()
    {
        var weeklyDead = C("weeklyDead", Usage(primaryUsed: 20, secondaryUsed: 100));

        Assert.Null(AutoSwitchSelector.Select(new[] { weeklyDead }));
    }

    [Fact]
    public void WeeklyOnlyAccountsAreIgnoredWhileFiveHourCandidatesExist()
    {
        var low5h = C("low5h", Usage(primaryUsed: 70, primaryReset: DateTimeOffset.UtcNow.AddMinutes(40))); // 30% left
        var pro = C("pro", Usage(primaryUsed: null, secondaryUsed: 10));

        var target = AutoSwitchSelector.Select(new[] { low5h, pro });

        Assert.NotNull(target);
        Assert.Equal("low5h", target!.Key);
    }

    [Fact]
    public void AccountsWithoutUsableDataAreIgnored()
    {
        var broken = C("broken", new QuotaUsage()); // no windows parsed
        var ok = C("ok", Usage(primaryUsed: 50, primaryReset: DateTimeOffset.UtcNow.AddHours(1)));

        var target = AutoSwitchSelector.Select(new[] { broken, ok });

        Assert.NotNull(target);
        Assert.Equal("ok", target!.Key);
    }

    [Fact]
    public void EmptyPoolReturnsNull()
    {
        // The VM excludes the current account, so a single-account setup yields an empty pool.
        Assert.Null(AutoSwitchSelector.Select(new List<AutoSwitchCandidate>()));
    }

    [Fact]
    public void PrefersTheSameTeamOverABetterAccountElsewhere()
    {
        var same = C("same", Usage(primaryUsed: 20, primaryReset: DateTimeOffset.UtcNow.AddHours(3)), team: "team-a"); // 80% left
        var other = C("other", Usage(primaryUsed: 5, primaryReset: DateTimeOffset.UtcNow.AddMinutes(5)), team: "team-b"); // 95% left, resets sooner

        var target = AutoSwitchSelector.Select(new[] { same, other }, preferredTeam: "team-a");

        Assert.NotNull(target);
        Assert.Equal("same", target!.Key);
    }

    [Fact]
    public void AppliesTheRulesWithinTheSameTeamFirst()
    {
        // Two usable same-team accounts: the ≥25% nearest-reset rule picks mid, not the fuller one.
        var full = C("full", Usage(primaryUsed: 10, primaryReset: DateTimeOffset.UtcNow.AddHours(4)), team: "t"); // 90%, resets far
        var mid = C("mid", Usage(primaryUsed: 60, primaryReset: DateTimeOffset.UtcNow.AddMinutes(20)), team: "t"); // 40%, resets soon
        var outsider = C("outsider", Usage(primaryUsed: 50, primaryReset: DateTimeOffset.UtcNow.AddMinutes(1)), team: "u");

        var target = AutoSwitchSelector.Select(new[] { full, mid, outsider }, preferredTeam: "t");

        Assert.NotNull(target);
        Assert.Equal("mid", target!.Key);
    }

    [Fact]
    public void FallsBackToAnotherTeamWhenTheSameTeamHasNothingUsable()
    {
        var sameDead = C("same", Usage(primaryUsed: 100), team: "team-a");
        var other = C("other", Usage(primaryUsed: 40), team: "team-b");

        var target = AutoSwitchSelector.Select(new[] { sameDead, other }, preferredTeam: "team-a");

        Assert.NotNull(target);
        Assert.Equal("other", target!.Key);
    }

    [Fact]
    public void NoPreferredTeamKeepsThePlainRules()
    {
        var a = C("a", Usage(primaryUsed: 10), team: "x");
        var b = C("b", Usage(primaryUsed: 20), team: "y");

        var target = AutoSwitchSelector.Select(new[] { a, b });

        Assert.Equal("a", target!.Key);
    }
}
