namespace AccountManager.Core.Quota;

/// <summary>
/// When the active account should be left, so a long task keeps going instead of hitting the
/// wall mid-stream. Percentages are REMAINING (0..100).
///
/// The reserve is a floor we do not want to dip into (5h 5%, weekly 2%). On top of that the
/// switch is decided predictively: at the measured burn rate we leave early enough to cover one
/// poll interval plus the switch itself. A fast-burning account is therefore left with real
/// margin, while an idle one simply rides down to the reserve.
/// </summary>
public static class AutoSwitchPolicy
{
    /// <summary>Keep this share of the 5h window before switching.</summary>
    public const double Reserve5hPercent = 5.0;

    /// <summary>Keep this share of the weekly window before switching.</summary>
    public const double ReserveWeeklyPercent = 2.0;

    /// <summary>A window at or below this counts as spent (bypasses the switch cooldown).</summary>
    public const double ExhaustedPercent = 0.5;

    /// <summary>Remaining percent of the governing window, or null when nothing usable was reported.</summary>
    public static double? Remaining(AutoSwitchCandidate candidate)
        => candidate.FiveHourRemaining ?? candidate.WeeklyRemaining;

    /// <summary>Reserve percent for the window that governs this account.</summary>
    public static double Reserve(AutoSwitchCandidate candidate)
        => candidate.FiveHourRemaining is not null ? Reserve5hPercent : ReserveWeeklyPercent;

    /// <summary>
    /// True when either window is at or below its reserve. Checking both is deliberate: a healthy
    /// 5h window must not hide a nearly spent weekly window (and the reverse).
    /// </summary>
    public static bool IsBelowReserve(AutoSwitchCandidate candidate)
        => (candidate.FiveHourRemaining is double five && five <= Reserve5hPercent)
        || (candidate.WeeklyRemaining is double week && week <= ReserveWeeklyPercent);

    /// <summary>True when either window is spent, so the account cannot serve until it resets.</summary>
    public static bool IsExhausted(AutoSwitchCandidate candidate)
        => (candidate.FiveHourRemaining is double five && five <= ExhaustedPercent)
        || (candidate.WeeklyRemaining is double week && week <= ExhaustedPercent);

    /// <summary>
    /// Should the switcher move off the active account now?
    ///
    /// Fires when a window is already at its reserve, or — using the measured burn rate — when the
    /// governing window is projected to reach its reserve within <paramref name="lead"/>. The lead
    /// is one poll interval (time to notice) plus the switch itself, so the quota spent during that
    /// window comes out of the margin rather than the reserve.
    /// </summary>
    public static bool ShouldSwitch(AutoSwitchCandidate candidate, double burnPerMinute, TimeSpan lead)
    {
        if (IsBelowReserve(candidate)) { return true; }

        var remaining = Remaining(candidate);
        if (remaining is not double current) { return false; }

        var margin = Math.Max(0.0, burnPerMinute) * lead.TotalMinutes;
        return current - Reserve(candidate) <= margin;
    }
}
