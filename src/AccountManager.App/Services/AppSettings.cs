using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AccountManager.App.Services;

/// <summary>Small persisted user preferences (CLI override, auto-refresh, window size).</summary>
public sealed class AppSettings
{
    public string? CliPath { get; set; }

    public int AutoRefreshMinutes { get; set; } = 5;

    /// <summary>When true, low quota on the active Codex account triggers polling and an automatic switch.</summary>
    public bool AutoSwitchAccount { get; set; }

    public double WindowWidth { get; set; } = 1140;

    public double WindowHeight { get; set; } = 720;

    public bool LogExpanded { get; set; }

    /// <summary>WebDAV server URL for the manual account sync (empty = not configured).</summary>
    public string? WebDavUrl { get; set; }

    public string? WebDavUsername { get; set; }

    /// <summary>App password wrapped with <c>SecretProtector</c> (never stored in plaintext).</summary>
    public string? WebDavPassword { get; set; }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly string _path;
    private readonly string? _inheritedPath;

    public SettingsStore(string? path = null, string? inheritedPath = null)
    {
        _path = path ?? DefaultPath();
        // Explicit paths stay isolated (tests and portable callers). Normal app saves keep a
        // last-used copy so a fresh timestamped release can inherit preferences, not reset them.
        _inheritedPath = inheritedPath ?? (path is null ? LegacyPath() : null);
    }

    public static string DefaultPath() => Path.Combine(PortablePaths.Data, "avalonia-settings.json");

    /// <summary>Old per-user location, read once to migrate settings into the portable folder.</summary>
    public static string LegacyPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "OpenCodeAccountImporter", "avalonia-settings.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "OpenCodeAccountImporter", "avalonia-settings.json");
    }

    public AppSettings Load()
    {
        var local = TryLoad(_path);
        if (local is not null) { return local; }
        if (_inheritedPath is not null && !string.Equals(_inheritedPath, _path, StringComparison.OrdinalIgnoreCase))
        {
            var inherited = TryLoad(_inheritedPath);
            if (inherited is not null)
            {
                Save(inherited);
                return inherited;
            }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        SaveTo(_path, settings);
        if (_inheritedPath is not null && !string.Equals(_inheritedPath, _path, StringComparison.OrdinalIgnoreCase))
        { SaveTo(_inheritedPath, settings); }
    }

    private static AppSettings? TryLoad(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) : null; }
        catch { return null; }
    }

    private static void SaveTo(string path, AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Best effort.
        }
    }
}

/// <summary>
/// Portable (green) data location: everything lives next to the executable so the single
/// file can be moved around with its settings and logs. Falls back to the per-user folder
/// only when the executable directory is not writable (e.g. Program Files, read-only media).
/// </summary>
public static class PortablePaths
{
    private static readonly Lazy<string> Resolved = new(Resolve);

    /// <summary>Directory for settings and logs.</summary>
    public static string Data => Resolved.Value;

    public static string Logs => Path.Combine(Data, "logs");

    private static string Resolve()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (IsWritable(baseDirectory)) { return baseDirectory; }

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "OpenCodeAccountImporter");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "OpenCodeAccountImporter");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
