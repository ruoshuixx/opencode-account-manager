namespace AccountManager.Core.Quota;

/// <summary>Parsed Codex usage windows. Window states: Available / Missing / Invalid / Unlimited.</summary>
public sealed class QuotaUsage
{
    public double? PrimaryPercent { get; set; }

    public DateTimeOffset? PrimaryReset { get; set; }

    public double? SecondaryPercent { get; set; }

    public DateTimeOffset? SecondaryReset { get; set; }

    public string Plan { get; set; } = string.Empty;

    public string PrimaryState { get; set; } = "Missing";

    public string SecondaryState { get; set; } = "Missing";

    public string? Message { get; set; }

    public bool? LimitReached { get; set; }

    /// <summary>Total "reset credits" the server reports as available (usage API's rate_limit_reset_credits.available_count).</summary>
    public int? ResetCreditsAvailable { get; set; }
}

public enum QuotaResultState
{
    Success,
    NoData,
    Partial,
    Expired,
    MissingToken,
    Unsupported,
    Error,
}

/// <summary>Outcome of the subscription lookup that rode along with a quota query.</summary>
public enum SubscriptionRefreshState
{
    /// <summary>No live lookup ran (not due, throttled, or it failed) — keep whatever value is shown.</summary>
    NotQueried,

    /// <summary>A live lookup ran and returned an expiry.</summary>
    Refreshed,

    /// <summary>A live lookup ran and authoritatively returned no expiry — clear the shown value.</summary>
    RefreshedEmpty,
}

/// <summary>Per-account quota result, keyed by the account row key.</summary>
public sealed class AccountQuotaResult
{
    public string Key { get; set; } = string.Empty;

    public QuotaResultState State { get; set; }

    public string Message { get; set; } = string.Empty;

    public QuotaUsage? Usage { get; set; }

    /// <summary>Live subscription expiry, when a refresh ran during this query (else null).</summary>
    public DateTimeOffset? SubscriptionActiveUntil { get; set; }

    /// <summary>What the live subscription lookup did: not run, returned a value, or authoritatively empty.</summary>
    public SubscriptionRefreshState SubscriptionRefresh { get; set; } = SubscriptionRefreshState.NotQueried;

    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.Now;
}
