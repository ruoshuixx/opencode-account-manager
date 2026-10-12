using AccountManager.App.Services;
using Xunit;

namespace AccountManager.App.Tests;

public class SettingsInheritanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "account-settings-" + Guid.NewGuid().ToString("N"));
    private string PathFor(string folder) => Path.Combine(_root, folder, "settings.json");

    [Fact]
    public void NewTimestampedDirectoryInheritsLastSavedPreferencesWithoutPlaintextPassword()
    {
        var shared = PathFor("user");
        new SettingsStore(PathFor("old-release"), shared).Save(new AppSettings
        {
            AutoRefreshMinutes = 7, AutoSwitchAccount = true,
            WebDavPassword = "protected-password-placeholder",
        });
        var inherited = new SettingsStore(PathFor("new-release"), shared).Load();
        Assert.True(inherited.AutoSwitchAccount);
        Assert.Equal(7, inherited.AutoRefreshMinutes);
        Assert.Equal("protected-password-placeholder", inherited.WebDavPassword);
        Assert.True(File.Exists(PathFor("new-release")));
    }

    [Fact]
    public void ExistingPortablePreferencesAreNotOverwrittenBySharedDefaults()
    {
        var portable = PathFor("portable");
        var shared = PathFor("user");
        new SettingsStore(portable).Save(new AppSettings { AutoRefreshMinutes = 11 });
        new SettingsStore(shared).Save(new AppSettings { AutoRefreshMinutes = 2 });
        var settings = new SettingsStore(portable, shared).Load();
        Assert.Equal(11, settings.AutoRefreshMinutes);
    }

    [Fact]
    public void CorruptPortableSettingsCanRecoverFromLastSavedPreferences()
    {
        var portable = PathFor("portable");
        var shared = PathFor("user");
        Directory.CreateDirectory(Path.GetDirectoryName(portable)!);
        File.WriteAllText(portable, "broken-json");
        new SettingsStore(shared).Save(new AppSettings { AutoRefreshMinutes = 9 });
        Assert.Equal(9, new SettingsStore(portable, shared).Load().AutoRefreshMinutes);
    }

    [Fact]
    public void SettingsFilesFromOlderReleasesWithRemovedProxyFieldsStillLoad()
    {
        var path = PathFor("portable");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "{\"CliPath\":\"/usr/bin/opencode\",\"OpenCodeUseSystemProxy\":true,"
            + "\"OpenCodeProxyUrl\":\"http://127.0.0.1:7890\",\"DesktopPath\":\"/opt/x\",\"AutoRefreshMinutes\":8}");

        var settings = new SettingsStore(path).Load();

        Assert.Equal("/usr/bin/opencode", settings.CliPath);
        Assert.Equal(8, settings.AutoRefreshMinutes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, true); }
    }
}
