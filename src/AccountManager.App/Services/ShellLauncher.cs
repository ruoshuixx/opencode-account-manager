using System.ComponentModel;
using System.Diagnostics;

namespace AccountManager.App.Services;

/// <summary>
/// Opens one allow-listed target (an absolute <c>http</c>/<c>https</c> URL, or an existing
/// absolute local directory) with the desktop's default handler.
///
/// The target is always passed as a single literal argument: never as shell text, never
/// concatenated, never written to a log, and never echoed back in a failure message. On Unix the
/// <c>xdg-open</c> launcher is preferred and <c>gio open</c> is used only when <c>xdg-open</c> is
/// genuinely absent (ENOENT). A bounded wait observes the launcher itself; the launcher is never
/// killed on timeout/cancellation because it may hand off to a browser that the user owns.
///
/// This is a small boundary for the two concrete call sites (browser URL + log directory), not a
/// general process framework.
/// </summary>
public sealed class ShellLauncher
{
    private const string InvalidTargetMessage =
        "无法打开：仅支持以 http/https 开头的完整链接，或已存在的本地目录。";
    private const string LauncherMissingMessage =
        "无法确认已打开：系统缺少可用的打开程序，请手动打开。";
    private const string LauncherFailedMessage =
        "无法确认已打开：调用系统打开程序失败，请手动打开。";

    private readonly IShellRunner _runner;
    private readonly bool _isWindows;

    /// <summary>Production launcher: real process runner plus the current OS platform.</summary>
    public ShellLauncher() : this(new ShellRunner(), OperatingSystem.IsWindows()) { }

    internal ShellLauncher(IShellRunner runner, bool isWindows)
    {
        _runner = runner;
        _isWindows = isWindows;
    }

    /// <summary>Opens <paramref name="target"/> or throws a fixed, target-free failure.</summary>
    public async Task OpenAsync(string target, CancellationToken ct = default)
    {
        if (!IsAllowedTarget(target)) { throw new ArgumentException(InvalidTargetMessage); }
        try
        {
            ct.ThrowIfCancellationRequested();

            if (_isWindows)
            {
                var windows = await _runner.RunAsync(BuildWindowsStartInfo(target), ct).ConfigureAwait(false);
                if (windows.Outcome != LaunchOutcome.Started) { throw new ShellLaunchException(LauncherFailedMessage); }
                return;
            }

            var first = await _runner.RunAsync(BuildStartInfo("xdg-open", target), ct).ConfigureAwait(false);
            if (first.Outcome == LaunchOutcome.Started) { return; }
            // Only a genuinely missing xdg-open falls back; permission/non-zero/timeout do not.
            if (first.Outcome != LaunchOutcome.Missing) { throw new ShellLaunchException(LauncherFailedMessage); }

            var fallback = await _runner.RunAsync(BuildGioStartInfo(target), ct).ConfigureAwait(false);
            if (fallback.Outcome == LaunchOutcome.Started) { return; }
            throw new ShellLaunchException(fallback.Outcome == LaunchOutcome.Missing
                ? LauncherMissingMessage
                : LauncherFailedMessage);
        }
        catch (OperationCanceledException)
        {
            // Safe, inner-free cancellation: never carries the target or launcher output.
            throw new OperationCanceledException(ct);
        }
        catch (ShellLaunchException)
        {
            throw;
        }
        catch (Exception)
        {
            // Any unexpected runner failure becomes a fixed, target-free message (no inner).
            throw new ShellLaunchException(LauncherFailedMessage);
        }
    }

    /// <summary>Unix launcher start info: one literal argument, no shell, output discarded.</summary>
    internal static ProcessStartInfo BuildStartInfo(string launcher, string target)
    {
        var info = new ProcessStartInfo
        {
            FileName = launcher,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(target);
        return info;
    }

    /// <summary>Fixed <c>gio open &lt;target&gt;</c> fallback as separate arguments, no shell.</summary>
    internal static ProcessStartInfo BuildGioStartInfo(string target)
    {
        var info = new ProcessStartInfo
        {
            FileName = "gio",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("open");
        info.ArgumentList.Add(target);
        return info;
    }

    /// <summary>Windows keeps the shell default and passes the target verbatim (no arguments).</summary>
    internal static ProcessStartInfo BuildWindowsStartInfo(string target) => new()
    {
        FileName = target,
        UseShellExecute = true,
    };

    /// <summary>
    /// Allow-list: an absolute <c>http</c>/<c>https</c> URL with a host, or an existing absolute
    /// local directory. Empty/relative/unsupported/control-character targets are rejected.
    /// </summary>
    internal static bool IsAllowedTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) { return false; }
        for (var i = 0; i < target.Length; i++)
        {
            if (char.IsControl(target[i])) { return false; }
        }
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }
        return Path.IsPathRooted(target) && Directory.Exists(target);
    }
}

/// <summary>How a single launcher invocation ended.</summary>
internal enum LaunchOutcome
{
    Started,
    Missing,
    PermissionDenied,
    Failed,
    TimedOut,
}

/// <summary>Outcome of one launcher invocation. Raw output is never carried past the runner.</summary>
internal readonly record struct ShellLaunchResult(LaunchOutcome Outcome, int ExitCode, string? StandardError);

/// <summary>Test seam: the only place external processes are started.</summary>
internal interface IShellRunner
{
    Task<ShellLaunchResult> RunAsync(ProcessStartInfo startInfo, CancellationToken ct);
}

/// <summary>Fixed, target-free failure surfaced to the UI. Never wraps the launcher's raw output.</summary>
internal sealed class ShellLaunchException : Exception
{
    internal ShellLaunchException(string message) : base(message) { }
}

/// <summary>
/// Default process runner. Starts the launcher through an injected delegate (default
/// <see cref="Process.Start(ProcessStartInfo)"/>), concurrently discards both output pipes through a
/// fixed buffer, and waits at most a bounded, monotonically shared time for the launcher to exit —
/// never for pipe EOF. It never kills the launcher or anything it spawned: a browser the launcher
/// handed off to may be holding the inherited pipes open.
/// </summary>
internal sealed class ShellRunner : IShellRunner
{
    private static readonly TimeSpan DefaultLauncherWait = TimeSpan.FromSeconds(8);
    private readonly TimeSpan _launcherWait;
    private readonly Func<ProcessStartInfo, Process?> _start;

    internal ShellRunner() : this(DefaultLauncherWait, Process.Start) { }

    /// <summary>Test seam: swap the start delegate and/or the bounded launcher wait.</summary>
    internal ShellRunner(TimeSpan launcherWait, Func<ProcessStartInfo, Process?> start)
    {
        _launcherWait = launcherWait;
        _start = start;
    }

    public async Task<ShellLaunchResult> RunAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        // The shared monotonic deadline starts before the launcher starts, so startup time is
        // subtracted from the single bounded wait below.
        var startedAt = Stopwatch.GetTimestamp();
        ct.ThrowIfCancellationRequested();

        Process? process;
        try
        {
            process = _start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            // ENOENT / ERROR_FILE_NOT_FOUND: the launcher itself is not installed.
            return new ShellLaunchResult(LaunchOutcome.Missing, ex.NativeErrorCode, null);
        }
        catch (Win32Exception ex)
        {
            return new ShellLaunchResult(LaunchOutcome.PermissionDenied, ex.NativeErrorCode, null);
        }
        catch (Exception)
        {
            return new ShellLaunchResult(LaunchOutcome.Failed, 0, null);
        }

        // Windows shell handoff: the shell owns the launch, so a null OR non-null handle is success.
        // Never probe streams or wait: an unassociated/child-handler Process has neither.
        if (startInfo.UseShellExecute)
        {
            process?.Dispose();
            return new ShellLaunchResult(LaunchOutcome.Started, 0, null);
        }

        // Unix (UseShellExecute=false): a null handle means no process was created -> not a launch.
        if (process is null) { return new ShellLaunchResult(LaunchOutcome.Failed, 0, null); }

        Task? exit = null;
        Task? drainOut = null;
        Task? drainErr = null;
        using var drains = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            drainOut = DiscardAsync(process.StandardOutput.BaseStream, drains.Token);
            drainErr = DiscardAsync(process.StandardError.BaseStream, drains.Token);

            var remaining = _launcherWait - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero) { return new ShellLaunchResult(LaunchOutcome.TimedOut, 0, null); }

            // Wait for the launcher's exit only, never for pipe EOF (a browser may inherit them).
            exit = process.WaitForExitAsync(CancellationToken.None);
            return await WaitForLauncherAsync(exit, () => process.ExitCode, remaining, ct).ConfigureAwait(false);
        }
        finally
        {
            // Cancel our own readers, close our own pipes, release the handle. Never kill the
            // launcher or the user's browser tree on timeout/cancellation.
            drains.Cancel();
            if (drainOut is not null) { Observe(drainOut); }
            if (drainErr is not null) { Observe(drainErr); }
            if (exit is not null) { Observe(exit); }
            process.Dispose();
        }
    }

    /// <summary>Reads and drops a pipe in a fixed buffer; never retains or surfaces its text.</summary>
    internal static async Task DiscardAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0) { return; }
        }
    }

    /// <summary>
    /// Completes when <paramref name="exit"/> does, at the shared <paramref name="deadline"/>, or on
    /// <paramref name="ct"/>. A timed-out wait is unconfirmed; cancellation propagates. It never
    /// kills or waits for anything the launcher spawned.
    /// </summary>
    internal static async Task<ShellLaunchResult> WaitForLauncherAsync(
        Task exit, Func<int> exitCode, TimeSpan deadline, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (deadline <= TimeSpan.Zero) { timeout.Cancel(); }
        else { timeout.CancelAfter(deadline); }

        try
        {
            await exit.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException)
        {
            return new ShellLaunchResult(LaunchOutcome.TimedOut, 0, null);
        }
        catch (Exception)
        {
            return new ShellLaunchResult(LaunchOutcome.Failed, 0, null);
        }

        var code = exitCode();
        return code == 0
            ? new ShellLaunchResult(LaunchOutcome.Started, 0, null)
            : new ShellLaunchResult(LaunchOutcome.Failed, code, null);
    }

    private static void Observe(Task task)
        => _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
}
