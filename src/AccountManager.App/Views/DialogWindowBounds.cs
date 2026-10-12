using Avalonia;
using Avalonia.Controls;

namespace AccountManager.App.Views;

/// <summary>Keep native title bars and fixed dialog actions inside the monitor's work area.</summary>
internal static class DialogWindowBounds
{
    // Screen pixels to logical units, minus room for native decorations and a small margin.
    private const double WidthMargin = 32;
    private const double HeightMargin = 64;

    // Room kept for native decorations (title bar, borders) around the main window, in device pixels.
    private const double MainWidthMarginPx = 16;
    private const double MainHeightMarginPx = 80;

    /// <summary>Main window zoom, minimum size and size after fitting them to a work area.</summary>
    internal readonly record struct MainBounds(double Zoom, double MinWidth, double MinHeight, double Width, double Height);

    /// <summary>
    /// The layout is designed for a minimum size in logical units. At a high display scale that minimum
    /// can be larger than the whole monitor, which leaves the window cut off (and some window managers
    /// refuse to maximize it). When the minimum does not fit, the content is zoomed down uniformly so
    /// the designed layout still fits, and the window's minimum and default sizes shrink with it.
    /// </summary>
    internal static MainBounds FitMainBounds(
        double areaWidth, double areaHeight, double scaling,
        double designMinWidth, double designMinHeight, double designWidth, double designHeight)
    {
        var maxWidth = Math.Max(1, (areaWidth - MainWidthMarginPx) / scaling);
        var maxHeight = Math.Max(1, (areaHeight - MainHeightMarginPx) / scaling);
        var zoom = Math.Min(1.0, Math.Min(maxWidth / designMinWidth, maxHeight / designMinHeight));
        var minWidth = designMinWidth * zoom;
        var minHeight = designMinHeight * zoom;
        return new MainBounds(
            zoom,
            minWidth,
            minHeight,
            Math.Clamp(designWidth * zoom, minWidth, Math.Max(minWidth, maxWidth)),
            Math.Clamp(designHeight * zoom, minHeight, Math.Max(minHeight, maxHeight)));
    }

    public static void Fit(Window dialog, Window? owner = null, bool center = false)
    {
        var screen = dialog.Screens.ScreenFromWindow(owner ?? dialog) ?? dialog.Screens.Primary;
        if (screen is null) { return; }
        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        if (area.Width <= 0 || area.Height <= 0 || scaling <= 0) { return; }

        // Screen pixels must be converted to Avalonia logical units. Reserve space for
        // native decorations as well as a small margin; taskbars are already excluded.
        var maxWidth = Math.Max(1, area.Width / scaling - WidthMargin);
        var maxHeight = Math.Max(1, area.Height / scaling - HeightMargin);
        dialog.MinWidth = Math.Min(380, maxWidth);
        dialog.MinHeight = Math.Min(260, maxHeight);
        dialog.MaxWidth = maxWidth;
        dialog.MaxHeight = maxHeight;
        dialog.Width = Math.Clamp(dialog.Width, dialog.MinWidth, maxWidth);
        dialog.Height = Math.Clamp(dialog.Height, dialog.MinHeight, maxHeight);

        if (!center) { return; }
        var width = (int)Math.Ceiling(dialog.Width * scaling);
        var height = (int)Math.Ceiling((dialog.Height + 40) * scaling);
        var x = owner is null ? area.X + (area.Width - width) / 2
            : owner.Position.X + (int)(owner.Bounds.Width * scaling - width) / 2;
        var y = owner is null ? area.Y + (area.Height - height) / 2
            : owner.Position.Y + (int)(owner.Bounds.Height * scaling - height) / 2;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = new PixelPoint(
            Math.Clamp(x, area.X + 8, Math.Max(area.X + 8, area.Right - width - 8)),
            Math.Clamp(y, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - height - 8)));
    }
}
