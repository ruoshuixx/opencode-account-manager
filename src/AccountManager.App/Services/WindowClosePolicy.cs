namespace AccountManager.App.Services;

/// <summary>Fresh result of an actual StatusNotifier host query.</summary>
public enum TrayAvailability
{
    Available,
    Unavailable,
    Unknown,
}

/// <summary>The outcome the window Closing coordinator asks for.</summary>
public enum WindowCloseDecision
{
    Close,
    Hide,
    KeepVisible,
}

/// <summary>
/// Decides whether X hides the main window. Hiding requires a live tray host and an actually-ready
/// single-instance recovery channel; anything unknown closes for real so no invisible, unrecoverable
/// instance is left behind. The instance form is the actual App.Closing coordinator: it performs one
/// bounded fresh probe per close and keeps the window visible while a check is already in flight.
/// </summary>
public sealed class WindowClosePolicy
{
    private readonly Func<CancellationToken, Task<TrayAvailability>> _trayProbe;
    private readonly object _gate = new();
    private Task<WindowCloseDecision>? _pending;

    public WindowClosePolicy(Func<CancellationToken, Task<TrayAvailability>> trayProbe)
        => _trayProbe = trayProbe ?? throw new ArgumentNullException(nameof(trayProbe));

    /// <summary>True while a fresh bounded tray check is still running.</summary>
    public bool IsCheckPending
    {
        get { lock (_gate) { return _pending is { IsCompleted: false }; } }
    }

    public static bool ShouldHide(bool exiting, TrayAvailability tray, bool recoveryReady)
        => !exiting && tray == TrayAvailability.Available && recoveryReady;

    /// <summary>
    /// Applies a policy result against the currently observed capabilities, immediately before any UI
    /// action. A dead/exiting/cancelled app performs no action. A provisional Hide is only kept when a
    /// currently usable tray and current live recovery both still hold; otherwise it becomes a real
    /// Close. Existing Close/KeepVisible keep their meaning for a live, non-exiting, uncancelled window.
    /// </summary>
    public static WindowCloseDecision RevalidateDecision(
        WindowCloseDecision decision,
        bool alive,
        bool exiting,
        bool trayUsable,
        bool recoveryReady,
        bool cancelled)
    {
        if (!alive || exiting || cancelled) { return WindowCloseDecision.KeepVisible; }

        return decision switch
        {
            WindowCloseDecision.Hide => trayUsable && recoveryReady
                ? WindowCloseDecision.Hide
                : WindowCloseDecision.Close,
            _ => decision,
        };
    }

    /// <summary>
    /// One close attempt. An explicit exit closes immediately. An already-cancelled close keeps the
    /// window visible without probing at all. A re-entrant close while a check is already pending keeps
    /// the window visible without starting a second probe. A probe failure is Unknown and closes.
    /// </summary>
    public Task<WindowCloseDecision> DecideOnCloseAsync(bool exiting, bool recoveryReady, CancellationToken cancellationToken = default)
    {
        if (exiting) { return Task.FromResult(WindowCloseDecision.Close); }
        if (cancellationToken.IsCancellationRequested) { return Task.FromResult(WindowCloseDecision.KeepVisible); }

        lock (_gate)
        {
            if (_pending is { IsCompleted: false }) { return Task.FromResult(WindowCloseDecision.KeepVisible); }
            _pending = RunAsync(recoveryReady, cancellationToken);
            return _pending;
        }
    }

    private async Task<WindowCloseDecision> RunAsync(bool recoveryReady, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) { return WindowCloseDecision.KeepVisible; }

        Task<TrayAvailability> probeTask;
        try
        {
            probeTask = _trayProbe(cancellationToken);
        }
        catch
        {
            return WindowCloseDecision.Close;
        }

        TrayAvailability tray;
        try
        {
            // WaitAsync so a probe that ignores its token cannot stall the coordinator past the
            // close cancellation.
            tray = await probeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe(probeTask);
            return WindowCloseDecision.KeepVisible;
        }
        catch
        {
            Observe(probeTask);
            tray = TrayAvailability.Unknown;
        }

        // Re-check the cancel token after the probe so a cancelled close never applies a stale result.
        if (cancellationToken.IsCancellationRequested) { return WindowCloseDecision.KeepVisible; }

        return ShouldHide(false, tray, recoveryReady) ? WindowCloseDecision.Hide : WindowCloseDecision.Close;
    }

    private static void Observe(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
            return;
        }
        if (task.IsCompleted) { return; }

        _ = task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

/// <summary>
/// While the window is hidden this periodically re-checks the tray host and the recovery channel.
/// A failed or unknown check surfaces the window again instead of leaving a stale invisible app.
/// </summary>
internal sealed class TrayHostMonitor : IDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<bool>> _stillHealthy;
    private readonly Action _onLost;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private int _fired;

    internal TrayHostMonitor(Func<CancellationToken, Task<bool>> stillHealthy, Action onLost, TimeSpan? interval = null)
    {
        _stillHealthy = stillHealthy;
        _onLost = onLost;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>One bounded check; returns false and notifies at most once when the window must return.</summary>
    internal async Task<bool> CheckOnceAsync(CancellationToken cancellationToken)
    {
        bool healthy;
        try
        {
            var healthTask = _stillHealthy(cancellationToken);
            healthy = await healthTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return true;
        }
        catch
        {
            healthy = false;
        }

        if (!healthy && Interlocked.Exchange(ref _fired, 1) == 0)
        {
            _onLost();
        }

        return healthy;
    }

    internal void Start()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(_interval, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                if (!await CheckOnceAsync(_cts.Token).ConfigureAwait(false)) { return; }
            }
        });
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
