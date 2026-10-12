using AccountManager.App.Views;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// The main window's layout is designed for a minimum size in logical units, so a high display scale can
/// make that minimum larger than the monitor. The fitting maths is a pure function of work area and scale.
/// </summary>
public class DialogWindowBoundsTests
{
    [Fact]
    public void LeavesTheDesignedSizeAloneWhenItFitsTheWorkArea()
    {
        var fitted = DialogWindowBounds.FitMainBounds(1920, 1040, 1.0, 1000, 560, 1140, 720);

        Assert.Equal(new DialogWindowBounds.MainBounds(1.0, 1000, 560, 1140, 720), fitted);
    }

    [Fact]
    public void ZoomsDownSoTheDesignedMinimumFitsASmallWorkAreaAtAHighScale()
    {
        // 1368x741 work area at 175%: only about 782x423 logical units are available.
        var fitted = DialogWindowBounds.FitMainBounds(1368, 741, 1.75, 1000, 560, 1140, 720);

        Assert.InRange(fitted.Zoom, 0.1, 0.99);
        // The minimum window, in device pixels, plus decoration room must fit the work area.
        Assert.True(fitted.MinWidth * 1.75 + 16 <= 1368 + 1e-6);
        Assert.True(fitted.MinHeight * 1.75 + 80 <= 741 + 1e-6);
        // The content keeps the designed minimum: the window divided by the zoom is still 1000x560.
        Assert.Equal(1000, fitted.MinWidth / fitted.Zoom, 6);
        Assert.Equal(560, fitted.MinHeight / fitted.Zoom, 6);
        Assert.True(fitted.Width >= fitted.MinWidth && fitted.Height >= fitted.MinHeight);
    }

    [Fact]
    public void KeepsAUserSizeBetweenTheMinimumAndTheWorkArea()
    {
        var fitted = DialogWindowBounds.FitMainBounds(2560, 1400, 1.25, 1000, 560, 1300, 800);

        Assert.Equal(1.0, fitted.Zoom);
        Assert.Equal(1300, fitted.Width);
        Assert.Equal(800, fitted.Height);
    }

    [Fact]
    public void NeverProducesANonPositiveSizeForADegenerateWorkArea()
    {
        var fitted = DialogWindowBounds.FitMainBounds(10, 10, 4.0, 1000, 560, 1140, 720);

        Assert.True(fitted.Zoom > 0);
        Assert.True(fitted.MinWidth > 0 && fitted.MinHeight > 0);
        Assert.True(fitted.Width > 0 && fitted.Height > 0);
    }
}
