namespace AccountManager.Core.Quota;

/// <summary>One Codex account as seen by the auto-switch selector. Percents are REMAINING.</summary>
public sealed record AutoSwitchCandidate(string Key, string Title, QuotaUsage Usage, string Team = "")
{
    /// <summary>Remaining 5h window percent; null = the account has no 5h window (Pro/Business weekly-only).</summary>
    public double? FiveHourRemaining => Usage.PrimaryPercent is null
        ? null
        : Math.Clamp(100 - Usage.PrimaryPercent.Value, 0, 100);

    /// <summary>Remaining weekly percent; null = no weekly window reported.</summary>
    public double? WeeklyRemaining => Usage.SecondaryPercent is null
        ? null
        : Math.Clamp(100 - Usage.SecondaryPercent.Value, 0, 100);
}

/// <summary>
/// Chooses the account to switch to when the active account runs out of quota.
///
/// Preference order (per spec):
/// Accounts whose 5h or weekly window is already spent (≤ <see cref="AutoSwitchPolicy.ExhaustedPercent"/>)
/// are excluded up front — switching onto one would just stall again.
/// ⓞ accounts in the same team/workspace as the active one — staying in the same organisation
///    beats a marginally better account elsewhere;
/// ① within that pool (else the whole pool): 5h remaining ≥ 25% → the one whose 5h window resets
///    soonest;
/// ② otherwise any account with 5h balance → the most remaining (deterministic "any");
/// ③ otherwise (every 5h window is zero/absent) → a weekly-only account (Pro/Business, no 5h
///    window) that still has weekly balance → the most weekly remaining;
/// ④ otherwise null = do not switch.
/// </summary>
public static class AutoSwitchSelector
{
    public const double PreferredRemainingPercent = 25.0;

    /// <param name="others">Candidate accounts (the active account is already excluded).</param>
    /// <param name="preferredTeam">Team/workspace id to prefer; empty = no team preference.</param>
    public static AutoSwitchCandidate? Select(IReadOnlyList<AutoSwitchCandidate> others, string? preferredTeam = null)
    {
        if (others.Count == 0) { return null; }

        // ⓞ Same organisation first: only fall back to the wider pool when nobody there is usable.
        if (!string.IsNullOrEmpty(preferredTeam))
        {
            var sameTeam = others
                .Where(c => string.Equals(c.Team, preferredTeam, StringComparison.Ordinal))
                .ToList();
            var preferred = PickUsable(sameTeam);
            if (preferred is not null) { return preferred; }
        }

        return PickUsable(others);
    }

    private static AutoSwitchCandidate? PickUsable(IReadOnlyList<AutoSwitchCandidate> pool)
    {
        // Never route a live task onto an account whose 5h or weekly window is already spent: it
        // would stall again immediately, which defeats the point of switching (continuity first).
        var usable = pool.Where(c => !AutoSwitchPolicy.IsExhausted(c)).ToList();
        if (usable.Count == 0) { return null; }

        var with5h = usable.Where(c => c.FiveHourRemaining is not null).ToList();

        // ① 5h remaining ≥ 25% → nearest 5h reset (ties: most remaining).
        var best = with5h
            .Where(c => c.FiveHourRemaining >= PreferredRemainingPercent)
            .OrderBy(c => c.Usage.PrimaryReset ?? DateTimeOffset.MaxValue)
            .ThenByDescending(c => c.FiveHourRemaining)
            .FirstOrDefault();

        // ② no ≥25% account → any with 5h balance → most remaining (ties: nearest reset).
        best ??= with5h
            .Where(c => c.FiveHourRemaining > 0)
            .OrderByDescending(c => c.FiveHourRemaining)
            .ThenBy(c => c.Usage.PrimaryReset ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();

        // ③ every 5h window is exhausted/absent → weekly-only account (no 5h window) with balance.
        best ??= usable
            .Where(c => c.FiveHourRemaining is null && (c.WeeklyRemaining ?? 0) > 0)
            .OrderByDescending(c => c.WeeklyRemaining)
            .ThenBy(c => c.Usage.SecondaryReset ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();

        // ④ everything exhausted → no switch.
        return best;
    }
}
