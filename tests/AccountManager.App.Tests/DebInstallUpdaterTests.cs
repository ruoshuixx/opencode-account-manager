using AccountManager.App.Services;
using Xunit;

namespace AccountManager.App.Tests;

public class DebInstallUpdaterTests
{
    private static bool Exists(string path)
        => path is DebInstallUpdater.DpkgFileList or DebInstallUpdater.ApplyScript or DebInstallUpdater.InstallHelper;

    [Fact]
    public void RecognizesTheProgramOfAnInstalledPackage()
    {
        Assert.True(DebInstallUpdater.IsDebInstallation(true, DebInstallUpdater.ProgramPath, Exists));
    }

    [Theory]
    [InlineData("/home/u/opencode-account-manager-linux/app/OpenCodeAccountManager")]
    [InlineData("/opt/opencode-account-manager/OpenCodeAccountManager.old")]
    [InlineData(null)]
    public void OtherLocationsAreNotTheDebInstallation(string? processPath)
    {
        Assert.False(DebInstallUpdater.IsDebInstallation(true, processPath, Exists));
    }

    [Fact]
    public void RequiresLinuxTheDpkgRecordAndBothHelpers()
    {
        Assert.False(DebInstallUpdater.IsDebInstallation(false, DebInstallUpdater.ProgramPath, Exists));
        Assert.False(DebInstallUpdater.IsDebInstallation(true, DebInstallUpdater.ProgramPath, p => p != DebInstallUpdater.DpkgFileList));
        Assert.False(DebInstallUpdater.IsDebInstallation(true, DebInstallUpdater.ProgramPath, p => p != DebInstallUpdater.InstallHelper));
        Assert.False(DebInstallUpdater.IsDebInstallation(true, DebInstallUpdater.ProgramPath, p => p != DebInstallUpdater.ApplyScript));
    }

    [Fact]
    public void ScriptArgumentsAreDetachedFlagScriptPackageChecksumAndPid()
    {
        var arguments = DebInstallUpdater.BuildArguments("/tmp/x/pkg.deb", new string('a', 64), 4242);

        Assert.Equal(new[] { "-f", DebInstallUpdater.ApplyScript, "/tmp/x/pkg.deb", new string('a', 64), "4242" }, arguments);
    }

    [Fact]
    public void FailureNoteIsReadOnceAndThenRemoved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ocam-deb-note-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Null(DebInstallUpdater.TakeFailureNote(directory));

            File.WriteAllText(Path.Combine(directory, DebInstallUpdater.FailureMarkerName), "没有获得管理员授权。\n");

            Assert.Equal("没有获得管理员授权。", DebInstallUpdater.TakeFailureNote(directory));
            Assert.Null(DebInstallUpdater.TakeFailureNote(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
