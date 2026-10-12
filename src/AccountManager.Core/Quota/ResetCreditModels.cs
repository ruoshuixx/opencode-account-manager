namespace AccountManager.Core.Quota;

/// <summary>One banked Codex rate-limit reset credit returned by the reset-credits endpoint.</summary>
public sealed class ResetCredit
{
    public string Id { get; set; } = string.Empty;

    public string ResetType { get; set; } = string.Empty;

    /// <summary>Server status, e.g. "available" / "redeemed" / "expired".</summary>
    public string Status { get; set; } = string.Empty;

    public DateTimeOffset? GrantedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RedeemedAt { get; set; }

    public string Title { get; set; } = string.Empty;

    public bool IsAvailable => string.Equals(Status, "available", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The list of reset credits plus the server-reported available count.</summary>
public sealed class ResetCreditList
{
    public IReadOnlyList<ResetCredit> Credits { get; set; } = Array.Empty<ResetCredit>();

    public int AvailableCount { get; set; }

    /// <summary>Available credits, ordered so the one expiring soonest comes first.</summary>
    public IReadOnlyList<ResetCredit> AvailableByExpiry => Credits
        .Where(c => c.IsAvailable)
        .OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue)
        .ToList();
}

/// <summary>Result of redeeming one reset credit.</summary>
public sealed class ResetConsumeResult
{
    /// <summary>Server code, expected "reset".</summary>
    public string Code { get; set; } = string.Empty;

    public int WindowsReset { get; set; }

    public string CreditId { get; set; } = string.Empty;

    public DateTimeOffset? RedeemedAt { get; set; }

    public bool Succeeded => string.Equals(Code, "reset", StringComparison.OrdinalIgnoreCase);
}
