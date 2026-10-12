using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Security;

namespace AccountManager.Core.OpenCode;

public sealed class OpenCodeException : Exception
{
    public OpenCodeException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class OpenCodeCliOptions
{
    /// <summary>Explicit CLI path chosen by the user (Settings). Null = auto-detect.</summary>
    public string? CliPath { get; set; }

    public int TimeoutMs { get; set; } = 60000;
}

/// <summary>
/// CLI location seam: auto-detection for the current OS plus a usability check used to invalidate
/// a cached or configured path that disappeared or lost its execute bit. The default implementation
/// is the real production locator; tests inject a fixture so no real HOME/PATH is scanned.
/// </summary>
internal interface IOpenCodeCliLocator
{
    string? Locate();

    bool IsUsable(string path);
}

/// <summary>
/// Runs the OpenCode CLI (<c>opencode api ...</c> / <c>opencode debug paths</c>) and returns
/// parsed JSON. All server output is considered hostile: failures never echo raw responses.
/// </summary>
public sealed class OpenCodeCli
{
    private readonly OpenCodeCliOptions _options;
    private readonly SecretRegistry _secrets;
    private readonly Logging.AppLog? _log;
    private readonly IOpenCodeCliLocator _locator;
    private string? _cli;

    public OpenCodeCli(OpenCodeCliOptions options, SecretRegistry secrets, Logging.AppLog? log = null)
        : this(options, secrets, log, null)
    {
    }

    internal OpenCodeCli(OpenCodeCliOptions options, SecretRegistry secrets, Logging.AppLog? log, IOpenCodeCliLocator? locator)
    {
        _options = options;
        _secrets = secrets;
        _log = log;
        _locator = locator ?? new DefaultOpenCodeCliLocator();
    }

    public string? ResolvedCliPath => _options.CliPath ?? _cli;

    /// <summary>Change the CLI override at runtime (Settings) and drop the cached auto-detection.</summary>
    public void SetCliPath(string? path)
    {
        _options.CliPath = string.IsNullOrWhiteSpace(path) ? null : path;
        _cli = null;
    }

    public string ResolveCli()
    {
        // An explicit override is authoritative: if it stopped being usable we fail closed rather
        // than silently falling back to auto-detection (the user chose this path deliberately).
        if (!string.IsNullOrWhiteSpace(_options.CliPath))
        {
            if (_locator.IsUsable(_options.CliPath!)) { return _options.CliPath!; }
            throw new OpenCodeException("配置里的 OpenCode CLI 路径不存在或不可执行，请在设置中重新选择。");
        }

        // A cached auto-detection is reused only while it is still usable; a deleted or
        // de-executable binary (e.g. an app update) forces a fresh resolve.
        if (_cli is not null && _locator.IsUsable(_cli)) { return _cli; }

        _cli = null;
        var found = _locator.Locate();
        if (string.IsNullOrWhiteSpace(found) || !_locator.IsUsable(found))
        {
            throw new OpenCodeException("找不到 OpenCode CLI。请先启动桌面版一次，或在设置中手动选择 CLI 文件。");
        }

        _cli = found;
        _log?.Info("发现 OpenCode CLI：" + found);
        return found;
    }

    private static string? FindOnWindows()
    {
        // Desktop CLI shipped under %APPDATA%\ai.opencode.desktop\cli (prefer highest version).
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ai.opencode.desktop", "cli");
        if (Directory.Exists(root))
        {
            var candidate = Directory.EnumerateFiles(root, "opencode-cli.exe", SearchOption.AllDirectories)
                .Select(path => new { Path = path, Version = ParseVersion(path), Written = SafeWriteTime(path) })
                .OrderByDescending(x => x.Version)
                .ThenByDescending(x => x.Written)
                .FirstOrDefault();
            if (candidate is not null) { return candidate.Path; }
        }

        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "@opencodedesktop"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenCode"),
        };
        foreach (var baseDir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (!string.IsNullOrEmpty(baseDir))
            {
                roots.Add(Path.Combine(baseDir, "@opencodedesktop"));
                roots.Add(Path.Combine(baseDir, "OpenCode"));
            }
        }

        foreach (var r in roots)
        {
            var desktop = Path.Combine(r, "OpenCode.exe");
            if (File.Exists(desktop))
            {
                var dir = Path.GetDirectoryName(desktop)!;
                foreach (var rel in new[] { "opencode-cli.exe", @"resources\opencode-cli.exe", @"resources\bin\opencode-cli.exe" })
                {
                    var candidate = Path.Combine(dir, rel);
                    if (File.Exists(candidate)) { return candidate; }
                }
            }
        }

        return FindOnPath(new[] { "opencode-cli.exe", "opencode.exe" });
    }

    private static bool IsUsablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return false; }
        try
        {
            // On Linux the auto-detected CLI must be an ordinary executable; a symlink is accepted
            // only when its final target is one.
            if (OperatingSystem.IsLinux()) { return LinuxOpenCodeDiscovery.ResolveOrdinaryExecutable(path) is not null; }
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The production locator: Windows keeps the existing registry-free discovery; Linux uses the
    /// conservative <see cref="LinuxOpenCodeDiscovery"/> (PATH, XDG caches, user bins, bundled
    /// resource); other Unix (e.g. macOS) keeps the pre-existing PATH / user-bin lookup.
    /// </summary>
    private sealed class DefaultOpenCodeCliLocator : IOpenCodeCliLocator
    {
        public string? Locate()
        {
            if (OperatingSystem.IsWindows()) { return FindOnWindows(); }

            if (OperatingSystem.IsLinux())
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return LinuxOpenCodeDiscovery.FindCli(home, pathDirectories);
            }

            // Non-Linux Unix (e.g. macOS): keep the pre-existing PATH / user-bin lookup. No Linux
            // process or restart support is added for these platforms.
            return FindOnUnix();
        }

        public bool IsUsable(string path) => IsUsablePath(path);
    }

    private static string? FindOnUnix()
    {
        var found = FindOnPath(new[] { "opencode", "opencode-cli" });
        if (found is not null) { return found; }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var rel in new[] { ".opencode/bin/opencode", ".local/bin/opencode", ".bun/bin/opencode" })
        {
            var candidate = Path.Combine(home, rel);
            if (File.Exists(candidate)) { return candidate; }
        }

        return null;
    }

    private static string? FindOnPath(IEnumerable<string> names)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in names)
        {
            foreach (var dir in paths)
            {
                try
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) { return candidate; }
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    private static Version ParseVersion(string path)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^(\d+\.\d+\.\d+)");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var v) ? v : new Version(0, 0, 0);
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        => RunAsync(arguments, logFailures: true, cancellationToken);

    /// <summary>
    /// Runs the CLI. When <paramref name="logFailures"/> is false, a non-zero exit is not
    /// written to the error log (used for best-effort operations such as unsetting a
    /// variable that may not exist), though the exception is still thrown.
    /// </summary>
    public async Task<string> RunAsync(IReadOnlyList<string> arguments, bool logFailures, CancellationToken cancellationToken = default)
    {
        var cli = ResolveCli();
        var context = string.Join(' ', arguments.Take(3));
        _log?.Info("CLI 请求：" + context + "；程序：" + cli);

        var psi = new ProcessStartInfo
        {
            FileName = cli,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) { psi.ArgumentList.Add(arg); }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            if (logFailures) { _log?.Error("无法启动 OpenCode CLI", ex); }
            throw new OpenCodeException("无法启动 OpenCode CLI，请检查安装或在设置中重新选择。", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.TimeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            if (logFailures) { _log?.Error("CLI 超时：" + context); }
            throw new OpenCodeException("OpenCode 请求超时（60 秒）。请检查后台服务。");
        }

        var output = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var category = "未分类（原始输出已隐藏）";
            foreach (var candidate in new[] { "ECONNREFUSED", "ETIMEDOUT", "EACCES", "Unauthorized", "NotFound", "InvalidRequest", "Conflict", "service unavailable" })
            {
                if ((stderr + " " + output).Contains(candidate, StringComparison.OrdinalIgnoreCase)) { category = candidate; break; }
            }

            if (logFailures) { _log?.Error("CLI 失败：" + context + "；退出码=" + process.ExitCode + "；分类=" + category); }
            throw new OpenCodeException("OpenCode 请求失败，退出码 " + process.ExitCode + "；分类：" + category + "。详情见 logs 错误日志。");
        }

        _log?.Info("CLI 成功：" + context);
        return output;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
    }

    public async Task<string> GetConfigDirectoryAsync(CancellationToken cancellationToken = default)
        => await GetDebugPathAsync("config", "无法确定全局配置目录。", cancellationToken).ConfigureAwait(false);

    /// <summary>Resolves the OpenCode data directory (holds <c>auth.json</c> / <c>opencode.db</c>).</summary>
    public async Task<string> GetDataDirectoryAsync(CancellationToken cancellationToken = default)
        => await GetDebugPathAsync("data", "无法确定 OpenCode 数据目录。", cancellationToken).ConfigureAwait(false);

    private async Task<string> GetDebugPathAsync(string prefix, string failure, CancellationToken cancellationToken)
    {
        var output = await RunAsync(new[] { "debug", "paths" }, cancellationToken).ConfigureAwait(false);
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.StartsWith(prefix + " ", StringComparison.Ordinal))
            {
                return trimmed[(prefix.Length + 1)..].Trim();
            }
        }

        throw new OpenCodeException(failure);
    }
}
