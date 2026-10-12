using System.Diagnostics;
using System.Security.Cryptography;

namespace AccountManager.App.Services;

/// <summary>
/// In-app update for the system-wide .deb installation (<c>/opt/opencode-account-manager</c>), where the
/// program cannot replace itself. After the package is downloaded and verified, a detached user-side
/// script waits for this process to exit, installs the package through polkit (<c>pkexec</c>) with a
/// root helper that re-verifies the SHA-256, and starts the program again.
/// </summary>
internal static class DebInstallUpdater
{
    internal const string ProgramPath = "/opt/opencode-account-manager/OpenCodeAccountManager";
    internal const string HelperDirectory = "/usr/lib/opencode-account-manager";
    internal const string ApplyScript = HelperDirectory + "/apply-update.sh";
    internal const string InstallHelper = HelperDirectory + "/install-update";
    internal const string DpkgFileList = "/var/lib/dpkg/info/opencode-account-manager.list";
    internal const string FailureMarkerName = "update-failed.txt";

    /// <summary>True when this process is the program of an installed opencode-account-manager package.</summary>
    public static bool IsSupported
        => IsDebInstallation(OperatingSystem.IsLinux(), Environment.ProcessPath, File.Exists);

    internal static bool IsDebInstallation(bool isLinux, string? processPath, Func<string, bool> exists)
        => isLinux
           && string.Equals(processPath, ProgramPath, StringComparison.Ordinal)
           && exists(DpkgFileList)
           && exists(ApplyScript)
           && exists(InstallHelper);

    /// <summary>
    /// Hands the verified package to the detached installer script. The caller must exit afterwards;
    /// the script owns the downloaded file and removes it.
    /// </summary>
    public static void Launch(string verifiedPackagePath, int parentProcessId)
    {
        if (!IsSupported) { throw new InvalidOperationException("当前不是通过 .deb 安装的版本。"); }
        var path = Path.GetFullPath(verifiedPackagePath);
        if (!File.Exists(path)) { throw new FileNotFoundException("下载的安装包不存在。", path); }

        string sha256;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        var start = new ProcessStartInfo("/usr/bin/setsid")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in BuildArguments(path, sha256, parentProcessId)) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动升级程序。");
    }

    internal static IReadOnlyList<string> BuildArguments(string packagePath, string sha256, int parentProcessId)
        => new[] { "-f", ApplyScript, packagePath, sha256, parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>
    /// Reads and removes the note a failed update left behind, so the next start can tell the user why
    /// the old version is still running. Null when there is none.
    /// </summary>
    public static string? TakeFailureNote(string dataDirectory)
    {
        try
        {
            var marker = Path.Combine(dataDirectory, FailureMarkerName);
            if (!File.Exists(marker)) { return null; }
            var text = File.ReadAllText(marker).Trim();
            File.Delete(marker);
            return text.Length == 0 ? null : (text.Length > 500 ? text[..500] : text);
        }
        catch { return null; }
    }
}
