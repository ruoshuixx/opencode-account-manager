namespace AccountManager.Core.Accounts;

/// <summary>
/// Formats how much subscription time is left, always rounded <em>up</em>: any leftover fraction
/// counts as the next whole unit, so a subscription with 30 days and a few hours left reads "31天"
/// (never "30天"). Whole days while at least a day remains, otherwise hours, otherwise minutes.
/// </summary>
public static class SubscriptionClock
{
    public static string FormatRemaining(DateTimeOffset? expires, DateTimeOffset now)
    {
        if (expires is not { } until) { return "—"; }

        var span = until.ToUniversalTime() - now.ToUniversalTime();
        if (span <= TimeSpan.Zero) { return "已到期"; }

        if (span.TotalDays >= 1) { return (long)Math.Ceiling(span.TotalDays) + "天"; }

        if (span.TotalHours >= 1)
        {
            var hours = (long)Math.Ceiling(span.TotalHours);
            return hours >= 24 ? "1天" : hours + "小时";
        }

        if (span.TotalMinutes >= 1)
        {
            var minutes = (long)Math.Ceiling(span.TotalMinutes);
            return minutes >= 60 ? "1小时" : minutes + "分";
        }

        return "1分";
    }
}
