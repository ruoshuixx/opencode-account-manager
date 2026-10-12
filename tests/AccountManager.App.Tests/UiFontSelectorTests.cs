using AccountManager.App.Services;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// Linux UI font selection is a pure function of the installed family names: Inter stays the primary face
/// (as on Windows) unless an installed override is requested, and the installed CJK faces are returned in
/// preference order as fallbacks for Chinese glyphs.
/// </summary>
public class UiFontSelectorTests
{
    [Fact]
    public void NoOverrideKeepsTheBundledPrimaryFace()
    {
        Assert.Null(UiFontSelector.Pick(new[] { "Noto Sans CJK SC", "DejaVu Sans" }));
        Assert.Null(UiFontSelector.Pick(new[] { "Noto Sans CJK SC" }, "  "));
    }

    [Fact]
    public void UsesAnInstalledOverrideWithoutRegardToCase()
    {
        var installed = new[] { "Noto Sans CJK SC", "Noto Serif CJK SC" };

        Assert.Equal("Noto Serif CJK SC", UiFontSelector.Pick(installed, "noto serif cjk sc"));
    }

    [Fact]
    public void IgnoresAnOverrideThatIsNotInstalled()
    {
        Assert.Null(UiFontSelector.Pick(new[] { "Noto Sans CJK SC" }, "No Such Font"));
    }

    [Fact]
    public void FallbacksKeepPreferenceOrder()
    {
        var installed = new[] { "WenQuanYi Zen Hei", "Noto Sans CJK SC", "SimHei", "DejaVu Sans", "Noto Sans CJK JP" };

        Assert.Equal(new[] { "SimHei", "Noto Sans CJK SC", "WenQuanYi Zen Hei" }, UiFontSelector.Fallbacks(installed));
    }

    [Fact]
    public void FallbacksExcludeTheOverridePrimary()
    {
        var installed = new[] { "Noto Sans CJK SC", "SimHei" };

        Assert.Equal(new[] { "Noto Sans CJK SC" }, UiFontSelector.Fallbacks(installed, "SimHei"));
    }

    [Fact]
    public void NoPreferredFaceInstalledMeansNoFallbacks()
    {
        Assert.Empty(UiFontSelector.Fallbacks(new[] { "DejaVu Sans", "Liberation Sans" }));
        Assert.Empty(UiFontSelector.Fallbacks(Array.Empty<string>()));
    }
}
