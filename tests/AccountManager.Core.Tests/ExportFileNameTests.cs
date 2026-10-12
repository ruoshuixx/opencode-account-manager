using AccountManager.Core.Export;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>
/// The export file name carries the date-time as a suffix. The save dialog's default name and the
/// WebDAV upload both use this one function, so a manual export and an upload are named alike.
/// </summary>
public class ExportFileNameTests
{
    [Fact]
    public void NameIsOpencodeUnderscoreDateUnderscoreTimeJson()
    {
        var name = ExportService.GetExportFileName(new DateTime(2026, 10, 12, 21, 5, 9));

        Assert.Equal("opencode_2026-10-12_21-05-09.json", name);
    }

    [Fact]
    public void NameKeepsTheJsonExtensionTheExporterRequires()
    {
        var name = ExportService.GetExportFileName(new DateTime(2026, 1, 2, 3, 4, 5));

        Assert.Equal(".json", Path.GetExtension(name));
        Assert.DoesNotContain(':', name);
    }
}
