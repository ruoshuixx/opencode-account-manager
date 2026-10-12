namespace AccountManager.Core.Quota;

/// <summary>
/// Estimates how fast the active account is being consumed (percent per minute), so the poller
/// can speed up while a task is running and relax when nothing is happening.
///
/// Uses an exponential moving average and ignores sub-<see cref="MinElapsedMinutes"/> gaps, which
/// keeps the coarse integer percent readings from looking like a burst. A flat window (or a reset
/// that raises the reading) contributes zero, so the estimate decays back to "idle".
/// </summary>
public sealed class QuotaBurnEstimator
{
    /// <summary>Weight of the newest sample in the moving average.</summary>
    public const double Alpha = 0.4;

    /// <summary>Gaps shorter than this are treated as noise and do not update the estimate.</summary>
    public const double MinElapsedMinutes = 0.25;

    private readonly Dictionary<string, (double Remaining, DateTimeOffset At)> _samples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _burn = new(StringComparer.Ordinal);

    /// <summary>Feed one reading for an account. A null remaining reading is ignored.</summary>
    public void Record(string key, double? remaining, DateTimeOffset at)
    {
        if (remaining is not double current) { return; }

        if (_samples.TryGetValue(key, out var previous))
        {
            var minutes = (at - previous.At).TotalMinutes;
            if (minutes >= MinElapsedMinutes)
            {
                var drop = previous.Remaining - current;
                var instantaneous = drop > 0 ? drop / minutes : 0.0;
                var prior = _burn.TryGetValue(key, out var existing) ? existing : instantaneous;
                _burn[key] = prior * (1 - Alpha) + instantaneous * Alpha;
            }
        }

        _samples[key] = (current, at);
    }

    /// <summary>Current estimate in percent per minute; 0 when idle or unknown.</summary>
    public double BurnPerMinute(string key) => _burn.TryGetValue(key, out var value) ? value : 0.0;

    /// <summary>Drop all state for one account (kept by key, so this is only needed to forget).</summary>
    public void Forget(string key)
    {
        _samples.Remove(key);
        _burn.Remove(key);
    }
}
