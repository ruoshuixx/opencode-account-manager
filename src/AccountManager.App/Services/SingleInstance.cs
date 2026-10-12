using System;
using System.Threading;
using Avalonia.Threading;

namespace AccountManager.App.Services;

/// <summary>
/// Keeps a single running instance. Windows uses a named mutex plus a named activation event; Linux
/// uses a per-user exclusive lock and a Unix socket (see <see cref="LinuxSingleInstance"/>). Later
/// launches signal the first one and exit, so no second window appears.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _signal;
    private readonly LinuxSingleInstance? _linux;
    private readonly Action<Action>? _dispatch;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _listener;
    private bool _listening;
    private bool _disposed;

    public SingleInstance(string name)
        : this(name, dispatch: null)
    {
    }

    /// <summary>
    /// Test/DI constructor. <paramref name="dispatch"/> defaults to the UI thread; tests may inject a
    /// synchronous dispatcher so activation delivery is observable without an Avalonia loop.
    /// </summary>
    internal SingleInstance(string name, Action<Action>? dispatch)
    {
        _dispatch = dispatch;

        if (OperatingSystem.IsWindows())
        {
            _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            IsFirstInstance = createdNew;

            if (createdNew)
            {
                _signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".activate");
            }
            else
            {
                ActivationFailed = !SignalExistingOwner(name);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                _linux = new LinuxSingleInstance(name, runtimeDirectory: null, dispatch: dispatch);
                IsFirstInstance = _linux.IsFirstInstance;
                if (IsFirstInstance)
                {
                    // Exclusive owner: no hand-off is needed.
                }
                else if (_linux.LockContended)
                {
                    // A real live owner holds the lock: only this outcome may try activation.
                    ActivationFailed = !_linux.TryActivatePrimary();
                }
                else
                {
                    // Unsafe/inaccessible/error outcome: exclusive ownership was not established.
                    OwnershipUnconfirmed = true;
                    ActivationFailed = true;
                }
            }
            catch
            {
                // No usable private runtime directory or an unsafe lock path: refuse startup rather
                // than running an unlocked primary that could write accounts concurrently.
                IsFirstInstance = false;
                OwnershipUnconfirmed = true;
                ActivationFailed = true;
            }
        }
        else
        {
            _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            IsFirstInstance = createdNew;
        }
    }

    public bool IsFirstInstance { get; }

    /// <summary>True only when the recovery channel is actually listening and dispatching.</summary>
    public bool CanActivate
    {
        get
        {
            if (_disposed) { return false; }
            if (OperatingSystem.IsLinux()) { return _linux?.CanActivate ?? false; }
            return IsFirstInstance && _listening && _signal is not null;
        }
    }

    /// <summary>Set when a later launch could not hand off to the running owner; never a silent success.</summary>
    public bool ActivationFailed { get; }

    /// <summary>
    /// Set when exclusive ownership could not be established for an unsafe/inaccessible reason (as
    /// opposed to genuine contention with a live owner). Startup must be refused, not unlocked.
    /// </summary>
    public bool OwnershipUnconfirmed { get; }

    /// <summary>First instance: run <paramref name="onActivate"/> on the UI thread when another launch asks.</summary>
    public void Listen(Action onActivate)
    {
        if (_disposed || !IsFirstInstance) { return; }

        if (OperatingSystem.IsLinux())
        {
            _linux?.Listen(onActivate);
            return;
        }

        if (_signal is null || _listening) { return; }
        _listening = true;
        _listener = new Thread(() =>
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!_signal.WaitOne(500)) { continue; }
                var dispatch = _dispatch;
                if (dispatch is null) { Dispatcher.UIThread.Post(onActivate); }
                else { dispatch(onActivate); }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceListener",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _listening = false;

        _cts.Cancel();
        try { _signal?.Set(); } catch { }
        try { _listener?.Join(1000); } catch { }
        _signal?.Dispose();
        _cts.Dispose();

        if (OperatingSystem.IsLinux())
        {
            _linux?.Dispose();
            return;
        }

        // Only the owner releases the mutex; a second process never owned it.
        if (IsFirstInstance)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
    }

    private static bool SignalExistingOwner(string name)
    {
        // Analyzer-visible platform guard: OpenExisting is Windows-only.
        if (!OperatingSystem.IsWindows()) { return false; }

        // The owner may still be creating the event; retry briefly. The opened handle is disposed so a
        // later launch never leaks the secondary event handle.
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var secondary = EventWaitHandle.OpenExisting(name + ".activate");
                secondary.Set();
                return true;
            }
            catch (Exception)
            {
                Thread.Sleep(100);
            }
        }

        return false;
    }
}
