using System.Collections.Concurrent;
using AccountManager.Core.Logging;

namespace AccountManager.Core.Quota;

/// <summary>
/// Runs the live subscription lookup behind the <see cref="SubscriptionRefreshPolicy"/> gate and a
/// per-account minimum interval, so an aggressive quota poll never turns into a burst of
/// subscription requests. Throttle state is in memory: after a restart the first poll refreshes
/// anything missing or near expiry anyway.
/// </summary>
public sealed class SubscriptionRefresher
{
    private readonly SubscriptionService _service;
    private readonly AppLog? _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextAttempt = new(StringComparer.Ordinal);

    public SubscriptionRefresher(SubscriptionService service, AppLog? log = null)
    {
        _service = service;
        _log = log;
    }

    /// <summary>
    /// Refreshes one account's subscription when the gate allows it. Returns the live status, or
    /// null when no request was made (gate closed, throttled, or the request failed). A forced
    /// lookup bypasses the throttle; every attempt (success or failure) resets the throttle window.
    /// </summary>
    public async Task<SubscriptionStatus?> TryRefreshAsync(
        string key,
        string accessToken,
        string accountId,
        DateTimeOffset? storedUntil,
        DateTimeOffset now,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!SubscriptionRefreshPolicy.ShouldQuery(storedUntil, now, force)) { return null; }
        if (!force && _nextAttempt.TryGetValue(key, out var next) && next > now) { return null; }

        _nextAttempt[key] = now + SubscriptionRefreshPolicy.MinInterval;

        try
        {
            return await _service.GetSubscriptionAsync(accessToken, accountId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Warn("订阅查询失败：" + ex.Message);
            return null;
        }
    }
}
