namespace AccountManager.Core.Quota;

/// <summary>
/// Decides when a live ChatGPT subscription lookup is worth its request. A manual refresh always
/// queries; an automatic poll only queries when the cached value is missing, already expired, or
/// within <see cref="NearExpiryWindow"/> of expiring — so a healthy account costs nothing while a
/// renewal is still noticed in time. Actual request spacing is enforced by
/// <see cref="SubscriptionRefresher"/>.
/// </summary>
public static class SubscriptionRefreshPolicy
{
    /// <summary>Start polling for a renewal once this much time or less remains.</summary>
    public static readonly TimeSpan NearExpiryWindow = TimeSpan.FromHours(6);

    /// <summary>Minimum spacing between two automatic lookups for the same account.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(30);

    public static bool ShouldQuery(DateTimeOffset? storedUntil, DateTimeOffset now, bool force)
    {
        if (force) { return true; }
        if (storedUntil is not { } until) { return true; }
        if (until <= now) { return true; }
        return until - now <= NearExpiryWindow;
    }
}
