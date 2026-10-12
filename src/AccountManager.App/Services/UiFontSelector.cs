namespace AccountManager.App.Services;

/// <summary>
/// Chooses the UI font on Linux so it matches Windows: the bundled Inter face for Latin text, and an
/// installed CJK face for Chinese glyphs (what Windows gets from Microsoft YaHei). Inter has no Chinese
/// glyphs, and fontconfig's own fallback can land on the Japanese Noto variant, so the CJK faces are
/// named explicitly. Windows never calls this.
/// </summary>
internal static class UiFontSelector
{
    /// <summary>Optional environment override, e.g. <c>Noto Serif CJK SC</c> for a 宋体-style face.</summary>
    internal const string OverrideVariable = "OCAM_UI_FONT";

    // Simplified-Chinese faces in preference order. Windows faces come first so a machine that has them
    // installed (Wine fonts, copied TTFs) uses them; the Noto/WenQuanYi faces are the usual Ubuntu ones.
    private static readonly string[] Preferred =
    {
        "SimHei",
        "Microsoft YaHei",
        "Noto Sans CJK SC",
        "Source Han Sans SC",
        "WenQuanYi Zen Hei",
        "WenQuanYi Micro Hei",
        "Droid Sans Fallback",
    };

    /// <summary>The requested override family when it is installed; otherwise null (keep Inter).</summary>
    internal static string? Pick(IEnumerable<string> installed, string? overrideName = null)
    {
        var requested = overrideName?.Trim();
        if (string.IsNullOrEmpty(requested))
        {
            return null;
        }

        return installed.FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Installed CJK faces, in order, for glyphs the primary face lacks.</summary>
    internal static IReadOnlyList<string> Fallbacks(IEnumerable<string> installed, string? primary = null)
    {
        var available = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        return Preferred
            .Where(name => available.Contains(name) && !string.Equals(name, primary, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
