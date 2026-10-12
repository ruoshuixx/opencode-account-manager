using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AccountManager.Core.OpenCode;

/// <summary>
/// Small OS seam for Linux CLI discovery. The production implementation
/// (<see cref="RealLinuxCliHost"/>) reads the real environment and file system; tests inject a
/// fixture so no real HOME or OpenCode install is touched.
/// </summary>
internal interface ILinuxCliHost
{
    string? GetEnvironmentVariable(string name);

    /// <summary>Resolves symlinks and returns the final path when it is a regular, executable file; otherwise null.</summary>
    string? ResolveExecutable(string path);

    /// <summary>Immediate child names of a directory, or null when missing/unreadable/over cap. Bounded.</summary>
    IReadOnlyList<string>? ListDirectory(string directory);
}

/// <summary>Production host: real environment variables and a bounded directory listing.</summary>
internal sealed class RealLinuxCliHost : ILinuxCliHost
{
    private const int MaxDirectoryEntries = 512;

    public static RealLinuxCliHost Instance { get; } = new();

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? ResolveExecutable(string path) => LinuxOpenCodeDiscovery.ResolveOrdinaryExecutable(path);

    public IReadOnlyList<string>? ListDirectory(string directory)
    {
        try
        {
            var names = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (names.Count >= MaxDirectoryEntries) { return null; }
                names.Add(Path.GetFileName(entry));
            }

            return names;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Conservative Linux discovery for the OpenCode CLI. Candidates are built with POSIX separators
/// (never <see cref="Path.Combine"/>, so injected Windows tests see the same strings as Linux), and
/// every candidate must resolve to an ordinary executable.
/// </summary>
internal static class LinuxOpenCodeDiscovery
{
    internal const string BundledCliPath = "/opt/OpenCode/resources/opencode-cli";

    /// <summary>
    /// Finds the OpenCode CLI: PATH (<c>opencode</c>, then <c>opencode-cli</c>), the versioned XDG
    /// config/data <c>ai.opencode.desktop/cli/&lt;version&gt;</c> caches (highest usable version
    /// across both roots), the existing user bins, then the bundled
    /// <c>/opt/OpenCode/resources/opencode-cli</c>. Returns the resolved final target, or null.
    /// </summary>
    public static string? FindCli(string home, IReadOnlyList<string> pathDirectories, ILinuxCliHost? host = null)
    {
        host ??= RealLinuxCliHost.Instance;
        if (ReferenceEquals(host, RealLinuxCliHost.Instance) && !OperatingSystem.IsLinux()) { return null; }

        foreach (var directory in pathDirectories ?? Array.Empty<string>())
        {
            var found = FirstExecutable(host,
                CombinePosix(directory, "opencode"),
                CombinePosix(directory, "opencode-cli"));
            if (found is not null) { return found; }
        }

        var cached = HighestVersionedCliAcrossRoots(host, CliCacheRoots(home, host));
        if (cached is not null) { return cached; }

        foreach (var relative in new[] { ".opencode/bin/opencode", ".local/bin/opencode", ".bun/bin/opencode" })
        {
            var found = host.ResolveExecutable(CombinePosix(home, relative));
            if (found is not null) { return found; }
        }

        return host.ResolveExecutable(BundledCliPath);
    }

    /// <summary>
    /// The production executable check: canonicalize the whole path (parent components included)
    /// with the real <c>realpath(3)</c>, require an existing, non-empty ordinary file, then confirm
    /// *effective* execute access for the current user with
    /// <c>faccessat(AT_FDCWD, path, X_OK, AT_EACCESS)</c> — the permission an <c>execve</c> would
    /// actually be granted, not merely the presence of some execute bit. Linux only; null on other
    /// hosts. Best-effort, deliberately not claimed to be race-proof.
    /// </summary>
    internal static string? ResolveOrdinaryExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsLinux()) { return null; }

        try
        {
            var resolved = RealPath(path);
            if (resolved is null) { return null; }

            var info = new FileInfo(resolved);
            if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0) { return null; }

            // Ordinary-file guard (best effort, deliberately narrow): a usable executable is a
            // non-empty file; device/FIFO/socket entries report no usable length and are rejected.
            long length;
            try { length = info.Length; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            if (length <= 0) { return null; }

            return HasEffectiveExecuteAccess(resolved) ? resolved : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>
    /// Effective execute access for the current process, matching what <c>execve</c> would allow:
    /// <c>faccessat(AT_FDCWD, path, X_OK, AT_EACCESS)</c>. Narrow interop; only reached on Linux.
    /// </summary>
    private static bool HasEffectiveExecuteAccess(string path)
        => faccessat(AtFdcwd, path, XOk, AtEaccess) == 0;

    private const int AtFdcwd = -100;
    private const int XOk = 1;
    private const int AtEaccess = 0x200;

    [DllImport("libc", SetLastError = true)]
    private static extern int faccessat(int dirfd, string pathname, int mode, int flags);

    private static IReadOnlyList<string> CliCacheRoots(string home, ILinuxCliHost host)
    {
        // Only absolute XDG overrides are honored; a relative value is ignored entirely.
        var data = AbsoluteXdg(host.GetEnvironmentVariable("XDG_DATA_HOME")) ?? CombinePosix(home, ".local/share");
        var config = AbsoluteXdg(host.GetEnvironmentVariable("XDG_CONFIG_HOME")) ?? CombinePosix(home, ".config");
        return new[]
        {
            CombinePosix(data, "ai.opencode.desktop", "cli"),
            CombinePosix(config, "ai.opencode.desktop", "cli"),
        };
    }

    /// <summary>
    /// The highest usable versioned CLI across every XDG root. Root order only breaks ties, so a
    /// newer config-root CLI beats a stale data-root one.
    /// </summary>
    private static string? HighestVersionedCliAcrossRoots(ILinuxCliHost host, IReadOnlyList<string> roots)
    {
        Version? bestVersion = null;
        string? best = null;
        foreach (var root in roots)
        {
            var entries = host.ListDirectory(root);
            if (entries is null) { continue; }

            foreach (var name in entries)
            {
                var version = ParseVersion(name);
                if (version is null) { continue; }
                if (bestVersion is not null && version <= bestVersion) { continue; }

                var found = FirstExecutable(host,
                    CombinePosix(root, name, "opencode-cli"),
                    CombinePosix(root, name, "opencode"));
                if (found is null) { continue; }

                bestVersion = version;
                best = found;
            }
        }

        return best;
    }

    private static string? FirstExecutable(ILinuxCliHost host, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var found = host.ResolveExecutable(candidate);
            if (found is not null) { return found; }
        }

        return null;
    }

    private static Version? ParseVersion(string name)
    {
        var match = Regex.Match(name, @"^(\d+\.\d+\.\d+)");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    private static string? AbsoluteXdg(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') ? value : null;

    /// <summary>Joins POSIX-style segments with '/', so injected tests never see Windows separators.</summary>
    private static string CombinePosix(string left, params string[] parts)
    {
        var builder = new StringBuilder(left.TrimEnd('/'));
        foreach (var part in parts)
        {
            var segment = part.Trim('/');
            if (segment.Length == 0) { continue; }
            builder.Append('/').Append(segment);
        }

        return builder.Length == 0 ? "/" : builder.ToString();
    }

    /// <summary>
    /// Full canonicalization with <c>realpath(3)</c>, which also resolves symlinks in parent
    /// directories (unlike <see cref="Path.GetFullPath(string)"/>). Null when the path does not
    /// resolve. Never called off Linux.
    /// </summary>
    private static string? RealPath(string path)
    {
        var buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(4096);
            return realpath(path, buffer) == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(buffer);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath(string path, IntPtr resolved);
}
