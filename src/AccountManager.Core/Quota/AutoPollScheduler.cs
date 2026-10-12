namespace AccountManager.Core.Quota;

/// <summary>
/// Chooses how soon to poll the active account again.
///
/// An idle account relaxes to the configured interval. An account that is being consumed is
/// polled faster, aiming for <see cref="PollsBeforeReserve"/> checks between now and the reserve
/// line (never faster than <see cref="MinInterval"/> and never slower than the normal interval).
/// That way a fast-burning account is noticed with time to switch before it runs dry, while a
/// static one is left almost alone.
/// </summary>
public static class AutoPollScheduler
{
    /// <summary>Never poll the active account more often than this.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(15);

    /// <summary>Rates at or below this (percent per minute) count as idle.</summary>
    public const double IdleEpsilonPerMinute = 0.05;

    /// <summary>Aim for at least this many polls between now and the reserve line.</summary>
    public const double PollsBeforeReserve = 3.0;

    /// <summary>Time allowed for a switch to complete once it has been decided.</summary>
    public static readonly TimeSpan SwitchLatency = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How far ahead of the reserve a switch must be decided: one poll interval (time to notice a
    /// drop) plus the time to actually switch. Multiply by the burn rate to get the quota margin.
    /// </summary>
    public static TimeSpan SwitchLead(TimeSpan pollInterval) => pollInterval + SwitchLatency;

    /// <summary>
    /// Next poll interval for the active account.
    /// </summary>
    /// <param name="remaining">Remaining percent of the governing window, or null when unknown.</param>
    /// <param name="reserve">Reserve percent for that window.</param>
    /// <param name="burnPerMinute">Consumption estimate in percent per minute (0 = idle).</param>
    /// <param name="normal">The configured refresh interval, used as the relaxed ceiling.</param>
    public static TimeSpan NextInterval(double? remaining, double reserve, double burnPerMinute, TimeSpan normal)
    {
        if (remaining is not double current) { return normal; }
        if (burnPerMinute <= IdleEpsilonPerMinute) { return normal; }

        var headroom = Math.Max(0.0, current - reserve);
        var minutesToReserve = headroom / burnPerMinute;
        var target = TimeSpan.FromMinutes(minutesToReserve / PollsBeforeReserve);

        if (target < MinInterval) { return MinInterval; }
        if (target > normal) { return normal; }
        return target;
    }
}
