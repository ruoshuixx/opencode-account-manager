using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AccountManager.App.Controls;

/// <summary>
/// A precise quota bar: draws an outlined track and a fill whose width is exactly
/// Value% of the control width. Unlike a themed ProgressBar this never overlaps the
/// adjacent text and never lets a low value collapse into an invisible sliver.
/// </summary>
public class QuotaBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<QuotaBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<QuotaBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<QuotaBar, IBrush?>(nameof(Track));

    public static readonly StyledProperty<IBrush?> TrackBorderProperty =
        AvaloniaProperty.Register<QuotaBar, IBrush?>(nameof(TrackBorder));

    static QuotaBar()
    {
        AffectsRender<QuotaBar>(ValueProperty, FillProperty, TrackProperty, TrackBorderProperty);
    }

    /// <summary>Percentage 0..100 rendered as the filled portion.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public IBrush? TrackBorder
    {
        get => GetValue(TrackBorderProperty);
        set => SetValue(TrackBorderProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1) { return; }

        var radius = height / 2;
        var full = new Rect(0, 0, width, height);
        context.DrawRectangle(Track ?? Brushes.Transparent, new Pen(TrackBorder ?? Brushes.Transparent, 1), full, radius, radius);

        var ratio = Math.Clamp(Value / 100.0, 0, 1);
        if (ratio <= 0) { return; }

        // Keep a tiny but visible cap for very low values without distorting the ratio much.
        var fillWidth = Math.Max(width * ratio, Math.Min(4, width));
        var fill = new Rect(0, 0, fillWidth, height);
        context.DrawRectangle(Fill ?? Brushes.Transparent, null, fill, radius, radius);
    }
}
