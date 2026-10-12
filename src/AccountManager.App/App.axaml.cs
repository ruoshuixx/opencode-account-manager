using System;
using System.Threading;
using AccountManager.App.Services;
using AccountManager.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AccountManager.App;

public partial class App : Application
{
    private Views.MainWindow? _window;
    private TrayIcon? _tray;
    private WindowClosePolicy? _closePolicy;
    private LinuxTrayAvailability? _trayProbe;
    private TrayHostMonitor? _hostMonitor;
    private CancellationTokenSource? _closeCts;
    private bool _allowRealClose;
    private bool _cleaned;

    /// <summary>True once the user chose to quit; only then does the window close for real.</summary>
    public bool IsExiting { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new SettingsStore();
            var settings = store.Load();
            var services = new AppServices(settings);

            // Keep a comfortable default: never restore a near-fullscreen size.
            var designWidth = Math.Clamp(settings.WindowWidth, 1000, 1400);
            var designHeight = Math.Clamp(settings.WindowHeight, 620, 900);
            var window = new Views.MainWindow { Services = services, Width = designWidth, Height = designHeight };
            // Before it is shown, so CenterScreen centres the fitted size. A high display scale can
            // otherwise make the designed minimum larger than the monitor.
            window.FitToWorkArea(designWidth, designHeight);

            var dialogs = new Views.DialogService(() => window);
            window.DataContext = new MainWindowViewModel(services, dialogs);

            desktop.MainWindow = window;
            // X is cancelled and hides while the tray exists. Without a tray, closing the main
            // window must really exit instead of leaving an invisible background process.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            _window = window;
            window.Closing += OnWindowClosing;
            window.Closed += OnWindowClosed;

            // On Linux, X only hides when a real tray host and a live recovery channel both exist.
            if (OperatingSystem.IsLinux())
            {
                var probe = new LinuxTrayAvailability();
                _trayProbe = probe;
                _closePolicy = new WindowClosePolicy(token => probe.CheckAsync(token));
                _closeCts = new CancellationTokenSource();
            }

            CreateTrayIcon();
            window.Opened += (_, _) => SelfUpdater.CompleteStartup();

            // A second launch should surface this window instead of opening a new one. The callback
            // re-checks the live/closed state at execution time so a late event cannot resurrect it.
            Program.Instance?.Listen(ShowMainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Builds the notification-area icon: click or menu shows the window, menu quits.</summary>
    private void CreateTrayIcon()
    {
        try
        {
            _tray = new TrayIcon { ToolTipText = "OpenCode 账号管理", IsVisible = true };
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://OpenCodeAccountManager/Assets/app.ico"));
                _tray.Icon = new WindowIcon(stream);
            }
            catch
            {
                // No icon is better than a crash; the tray still works.
            }

            var show = new NativeMenuItem("显示主界面");
            show.Click += (_, _) => ShowMainWindow();

            var exit = new NativeMenuItem("退出");
            exit.Click += (_, _) => ExitApp();

            var menu = new NativeMenu();
            menu.Items.Add(show);
            var update = new NativeMenuItem("检查更新");
            update.Click += async (_, _) =>
            {
                ShowMainWindow();
                if (_window?.DataContext is MainWindowViewModel vm)
                {
                    await vm.CheckUpdatesCommand.ExecuteAsync(null);
                }
            };
            menu.Items.Add(update);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);

            _tray.Menu = menu;
            _tray.Clicked += (_, _) => ShowMainWindow();

            TrayIcon.SetIcons(this, new TrayIcons { _tray });
        }
        catch
        {
            // If the tray cannot be created, leave _tray null so X still quits normally.
            _tray = null;
        }
    }

    /// <summary>
    /// X hides to the tray instead of quitting, so auto-refresh keeps running — but a
    /// real tray host and a live single-instance recovery channel are both required, checked freshly
    /// each time. Anything unknown closes for real rather than leaving an invisible, unrecoverable app.
    /// </summary>
    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // No tray (creation failed) or an explicit quit: let the window actually close.
        if (IsExiting || _allowRealClose || _tray is null) { return; }

        if (!OperatingSystem.IsLinux())
        {
            e.Cancel = true;
            _window?.Hide();
            return;
        }

        // Keep the window visible while one bounded fresh check runs; a re-entrant close while a
        // check is already pending keeps it visible too.
        e.Cancel = true;

        var policy = _closePolicy;
        if (policy is null) { PerformRealClose(); return; }

        var recoveryReady = Program.Instance?.CanActivate ?? false;
        WindowCloseDecision decision;
        try
        {
            decision = await policy.DecideOnCloseAsync(exiting: false, recoveryReady, _closeCts?.Token ?? default);
        }
        catch
        {
            decision = WindowCloseDecision.Close;
        }

        // Re-check the currently observed capabilities immediately before applying any UI action: an
        // IPC loss during the probe, a lost icon, or an exit must not hide an unrecoverable window.
        decision = WindowClosePolicy.RevalidateDecision(
            decision,
            alive: !_cleaned,
            exiting: IsExiting,
            trayUsable: _tray is { IsVisible: true, Icon: not null },
            recoveryReady: Program.Instance?.CanActivate ?? false,
            cancelled: _closeCts?.IsCancellationRequested ?? false);

        switch (decision)
        {
            case WindowCloseDecision.Hide:
                _window?.Hide();
                StartHostMonitor();
                break;
            case WindowCloseDecision.Close:
                PerformRealClose();
                break;
            default:
                // KeepVisible: still visible, nothing to do.
                break;
        }
    }

    /// <summary>Bypasses the policy once so the programmatic close cannot recurse.</summary>
    private void PerformRealClose()
    {
        _allowRealClose = true;
        _hostMonitor?.Dispose();
        _hostMonitor = null;
        _window?.Close();
    }

    /// <summary>After hiding, watch for loss of the tray host or recovery channel and surface the window.</summary>
    private void StartHostMonitor()
    {
        _hostMonitor?.Dispose();
        var probe = _trayProbe;
        if (probe is null) { return; }

        _hostMonitor = new TrayHostMonitor(
            async token =>
            {
                var tray = await probe.CheckAsync(token).ConfigureAwait(false);
                return WindowClosePolicy.ShouldHide(false, tray, Program.Instance?.CanActivate ?? false);
            },
            onLost: () => Dispatcher.UIThread.Post(ShowMainWindow));
        _hostMonitor.Start();
    }

    /// <summary>Real quit (tray → 退出): let the window close, then shut the lifetime down.</summary>
    public void ExitApp()
    {
        IsExiting = true;
        _allowRealClose = true;
        Cleanup();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Cleanup();

    /// <summary>Stops monitoring, disposes the tray and cancels the close check exactly once.</summary>
    private void Cleanup()
    {
        if (_cleaned) { return; }
        _cleaned = true;

        _hostMonitor?.Dispose();
        _hostMonitor = null;

        try { _closeCts?.Cancel(); } catch { }

        if (_window?.DataContext is MainWindowViewModel vm)
        {
            vm.StopBackgroundMonitoring();
        }

        _tray?.Dispose();
        _tray = null;
    }

    private void ShowMainWindow()
    {
        // A host monitor or IPC callback can fire after the window was closed/exited; never resurrect it.
        if (_cleaned || IsExiting) { return; }

        _hostMonitor?.Dispose();
        _hostMonitor = null;
        if (_window is { } window) { ActivateWindow(window); }
    }

    /// <summary>Restore and bring the main window to the foreground (used by single-instance / tray).</summary>
    private static void ActivateWindow(Window window)
    {
        if (window.WindowState == WindowState.Minimized) { window.WindowState = WindowState.Normal; }
        window.Show();
        var wasTopmost = window.Topmost;
        window.Topmost = true;
        window.Topmost = wasTopmost;
        window.Activate();
    }
}
