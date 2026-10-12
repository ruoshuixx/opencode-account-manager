using System;
using AccountManager.App.Services;
using AccountManager.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Media;

namespace AccountManager.App.Views;

public partial class MainWindow : Window
{
    // Designed minimum size; the same values as MinWidth/MinHeight in MainWindow.axaml. The live
    // minimum is lowered (together with a content zoom) when the work area cannot show it.
    private const double DesignMinWidth = 1000;
    private const double DesignMinHeight = 560;

    private double _zoom = 1;

    public MainWindow()
    {
        InitializeComponent();
        ScalingChanged += (_, _) => FitToWorkArea(Width / _zoom, Height / _zoom);
        Title += " v" + AppUpdateInfo.DisplayVersion;

        // Show the build stamp in the window title so the running build can be
        // identified at a glance ("构建 yyyy-MM-dd HH:mm").
        var stamp = AppBuild.Stamp();
        if (!string.IsNullOrEmpty(stamp))
        {
            Title += "（" + stamp + "）";
        }
    }

    /// <summary>
    /// Fits the window to the monitor's work area. <paramref name="designWidth"/> and
    /// <paramref name="designHeight"/> are the unzoomed size to aim for.
    /// </summary>
    public void FitToWorkArea(double designWidth, double designHeight)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) { return; }
        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        if (area.Width <= 0 || area.Height <= 0 || scaling <= 0) { return; }

        var fitted = DialogWindowBounds.FitMainBounds(
            area.Width, area.Height, scaling, DesignMinWidth, DesignMinHeight, designWidth, designHeight);
        _zoom = fitted.Zoom;
        MinWidth = fitted.MinWidth;
        MinHeight = fitted.MinHeight;
        Width = fitted.Width;
        Height = fitted.Height;
        ZoomHost.LayoutTransform = fitted.Zoom < 1 ? new ScaleTransform(fitted.Zoom, fitted.Zoom) : null;
    }

    /// <summary>Set by the composition root so the window can persist its size on close.</summary>
    public AppServices? Services { get; set; }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) { vm.StopBackgroundMonitoring(); }
        if (Services is not null)
        {
            Services.Settings.WindowWidth = Width / _zoom;
            Services.Settings.WindowHeight = Height / _zoom;
            new SettingsStore().Save(Services.Settings);
        }

        base.OnClosed(e);
    }
}
