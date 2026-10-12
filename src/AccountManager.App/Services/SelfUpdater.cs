using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// The pure, testable helpers stay internal without widening the public app surface.
[assembly: InternalsVisibleTo("AccountManager.App.Tests")]
[assembly: InternalsVisibleTo("AccountManager.Linux.IntegrationHarness")]

namespace AccountManager.App.Services;

/// <summary>
/// Minimal self-update hand-over for the portable Windows x64 single-file build.
///
/// The old process stages the already-verified exe beside the running exe, renames the running
/// exe to a unique <c>.old-&lt;32 hex&gt;.exe</c> backup, moves the new exe into place and launches
/// it with a one-shot handshake; it reports success only after the new managed entry signals it is
/// running. The new process (<see cref="HandleStartup"/>) validates every argument against its own
/// directory and base name, opens the parent handle first (defeating PID reuse), signals, then waits
/// for the old process to release the single-instance mutex.
///
/// Compensation only ever touches files this hand-over created and its own child process. If a
/// post-hand-over UI crash leaves the backup behind, it is recovered manually, not automatically.
/// </summary>
public static class SelfUpdater
{
    private const string WaitForPidArg = "--wait-for-pid";
    private const string WaitForStartArg = "--wait-for-start";
    private const string ReadyEventArg = "--update-ready-event";
    private const string CleanupOldArg = "--cleanup-old";

    internal const string ReadyEventPrefix = @"Local\OpenCodeAccountManager-update-";
    internal const ushort PeMachineAmd64 = 0x8664;
    internal const string OldFileMarker = ".old-";

    private static readonly string[] UpdateFlags =
    {
        WaitForPidArg, WaitForStartArg, ReadyEventArg, CleanupOldArg,
    };

    /// <summary>
    /// Assemblies the post-swap launch/rollback path can touch that a plain startup has not loaded
    /// yet. They must be loaded before the running bundle is replaced; see
    /// <see cref="PreloadAssembliesUsedAfterSwap"/>.
    /// </summary>
    private static readonly string[] PostSwapAssemblyNames =
    {
        "System.Memory",
        "System.Threading.Thread",
        "System.Text.Encoding.Extensions",
        "System.Collections.NonGeneric",
        "Microsoft.Win32.Primitives",
    };

    private static readonly object StartupSync = new();
    private static string? _pendingCleanupOldFileName;

    /// <summary>
    /// True only for the Windows x64 self-contained single-file build; false for
    /// <c>dotnet</c>/testhost/dev launches, which must never run the updater.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                if (OperatingSystem.IsLinux()) { return LinuxSelfUpdater.IsSupported; }
                if (!OperatingSystem.IsWindows()) { return false; }
                if (RuntimeInformation.ProcessArchitecture != Architecture.X64) { return false; }

                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) ||
                    !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var fileName = Path.GetFileNameWithoutExtension(exe);
                if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, "testhost", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var entry = Assembly.GetEntryAssembly();
                if (entry is null) { return false; }

#pragma warning disable IL3000 // intentional single-file probe: Location is empty only in the bundle
                if (!string.IsNullOrEmpty(entry.Location)) { return false; }
#pragma warning restore IL3000

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Stages <paramref name="verifiedDownloadPath"/> (already verified by the caller) as the
    /// replacement executable. Returns only after the new process confirms its managed entry is
    /// running; the caller must then exit immediately. Any failure/cancel rolls the old exe back,
    /// removes this hand-over's files and kills only the child it started.
    /// </summary>
    public static async Task PrepareAndLaunchAsync(
        string verifiedDownloadPath,
        Version expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux())
        {
            await LinuxSelfUpdater.PrepareAndLaunchAsync(verifiedDownloadPath, expectedVersion, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsSupported)
        {
            throw new InvalidOperationException(
                "自更新仅支持 Windows x64 单文件便携版；当前运行方式（开发/dotnet/测试宿主）不受支持。");
        }

        if (string.IsNullOrWhiteSpace(verifiedDownloadPath))
        {
            throw new ArgumentException("已校验的更新文件路径不能为空。", nameof(verifiedDownloadPath));
        }

        ArgumentNullException.ThrowIfNull(expectedVersion);

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe) ||
            !currentExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("无法定位当前可执行文件，无法自更新。");
        }

        var fullCurrentExe = Path.GetFullPath(currentExe);
        var directory = Path.GetDirectoryName(fullCurrentExe);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("无法定位当前可执行文件所在目录，无法自更新。");
        }

        VerifyUpdateFile(verifiedDownloadPath, expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();

        var baseName = Path.GetFileNameWithoutExtension(fullCurrentExe);
        var newTempPath = Path.Combine(directory, BuildNewTempFileName(baseName, Guid.NewGuid().ToString("N")));
        var oldName = BuildOldFileName(baseName, Guid.NewGuid().ToString("N"));
        var oldPath = Path.Combine(directory, oldName);
        var eventName = ReadyEventPrefix + Guid.NewGuid().ToString("N");

        using var readyEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var swap = new ExeSwapTransaction(verifiedDownloadPath, fullCurrentExe, newTempPath, oldPath);

        // Read the parent identity BEFORE replacing the running exe. In the single-file build the host
        // can no longer resolve assemblies that were not already loaded once the bundle at
        // Environment.ProcessPath has been swapped out, so Process.GetCurrentProcess().StartTime must
        // not be the first lazy load of Microsoft.Win32.Primitives after Execute().
        var parentStartUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

        // Process.Start itself is the next lazy load (System.Memory via the command-line builder);
        // force it in while the original bundle is still in place.
        PreloadAssembliesUsedAfterSwap();

        Process? newProcess = null;
        try
        {
            swap.Execute();
            cancellationToken.ThrowIfCancellationRequested();

            var startInfo = new ProcessStartInfo
            {
                FileName = fullCurrentExe,
                WorkingDirectory = directory,
                UseShellExecute = false,
            };
            AddLaunchArguments(
                startInfo,
                Environment.ProcessId,
                parentStartUtcTicks,
                eventName,
                oldName);

            newProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动新版本程序。");

            await WaitForReadyAsync(newProcess, readyEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var rollbackError = swap.Rollback(newProcess);
            if (rollbackError is not null)
            {
                throw new InvalidOperationException(
                    "自更新失败，且无法自动恢复旧版本（备份：" + oldPath + "）：" + rollbackError, ex);
            }

            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("无法写入或替换程序文件，请检查目录权限或杀毒软件锁定：" + ex.Message, ex);
            }

            throw;
        }
        finally
        {
            newProcess?.Dispose();
        }
    }

    /// <summary>
    /// Called by <c>Program.Main</c> before the single-instance mutex. <c>true</c> continues a
    /// normal startup; <c>false</c> exits without a UI (unsupported build or invalid handshake).
    /// </summary>
    public static bool HandleStartup(string[] args)
    {
        if (args.Any(argument => argument.StartsWith("--linux-update", StringComparison.Ordinal)))
        {
            return OperatingSystem.IsLinux() && LinuxSelfUpdater.HandleStartup(args);
        }
        if (args.Any(argument => argument.StartsWith("--update-probe", StringComparison.Ordinal))) { return false; }
        var hasWindowsUpdateArgument = args.Any(IsWindowsUpdateArgument);
        if (OperatingSystem.IsLinux())
        {
            return !hasWindowsUpdateArgument && LinuxSelfUpdater.HandleStartup(args);
        }
        if (hasWindowsUpdateArgument && !OperatingSystem.IsWindows()) { return false; }

        var kind = ClassifyUpdateArgs(args, out var parsed);
        if (kind == UpdateLaunchKind.NotUpdate) { return !hasWindowsUpdateArgument; }
        if (kind == UpdateLaunchKind.Invalid || parsed is null) { return false; }

        if (!OperatingSystem.IsWindows()) { return false; }
        if (!IsSupported) { return false; }

        var ownExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(ownExe)) { return false; }
        if (!IsValidCleanupOldFileName(parsed.CleanupOldFileName, ownExe)) { return false; }

        Process? parent = null;
        EventWaitHandle? ready = null;
        try
        {
            // Open the parent handle first so a reused PID is caught by the recorded start time.
            try
            {
                parent = Process.GetProcessById(parsed.ParentPid);
            }
            catch (ArgumentException)
            {
                return false;
            }

            long actualStartTicks;
            try
            {
                actualStartTicks = parent.StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
                return false;
            }

            if (actualStartTicks != parsed.ParentStartUtcTicks) { return false; }

            try
            {
                ready = EventWaitHandle.OpenExisting(parsed.ReadyEventName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            try
            {
                ready.Set();
            }
            catch
            {
                return false;
            }

            // The old process must actually exit so it releases the single-instance mutex; if the
            // wait times out or throws, abort conservatively rather than race a second instance.
            bool parentExited;
            try
            {
                parentExited = parent.WaitForExit(30000);
            }
            catch
            {
                return false;
            }

            if (!parentExited) { return false; }

            lock (StartupSync)
            {
                _pendingCleanupOldFileName = parsed.CleanupOldFileName;
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ready?.Dispose();
            parent?.Dispose();
        }
    }

    /// <summary>
    /// Called once the main window initialised successfully. Deletes only the specific backup this
    /// hand-over created; on failure it is kept. A normal launch never deletes any <c>.old</c> file.
    /// </summary>
    public static void CompleteStartup()
    {
        if (OperatingSystem.IsLinux())
        {
            LinuxSelfUpdater.CompleteStartup();
            return;
        }

        string? fileName;
        lock (StartupSync)
        {
            fileName = _pendingCleanupOldFileName;
            _pendingCleanupOldFileName = null;
        }

        if (string.IsNullOrEmpty(fileName)) { return; }

        var ownExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(ownExe)) { return; }

        TryCleanupBackup(fileName, ownExe);
    }

    private static void AddLaunchArguments(
        ProcessStartInfo startInfo,
        int parentPid,
        long parentStartUtcTicks,
        string eventName,
        string oldFileName)
    {
        startInfo.ArgumentList.Add(WaitForPidArg);
        startInfo.ArgumentList.Add(parentPid.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(WaitForStartArg);
        startInfo.ArgumentList.Add(parentStartUtcTicks.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(ReadyEventArg);
        startInfo.ArgumentList.Add(eventName);
        startInfo.ArgumentList.Add(CleanupOldArg);
        startInfo.ArgumentList.Add(oldFileName);
    }

    /// <summary>
    /// The single-file host resolves assemblies from the bundle located at
    /// <see cref="Environment.ProcessPath"/>. After the swap that path holds the new bundle, so any
    /// assembly not yet loaded cannot be resolved (the host re-opens the file using the original
    /// bundle's offsets). Loading the ones the post-swap launch path needs here keeps the handshake
    /// and its rollback working. Names that do not exist are ignored.
    /// </summary>
    private static void PreloadAssembliesUsedAfterSwap()
    {
        foreach (var name in PostSwapAssemblyNames)
        {
            try
            {
                Assembly.Load(new AssemblyName(name));
            }
            catch
            {
                // Not present in this build/runtime; nothing to warm up.
            }
        }
    }

    private static async Task WaitForReadyAsync(
        Process newProcess,
        EventWaitHandle readyEvent,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (newProcess.HasExited)
            {
                throw new InvalidOperationException("新版本进程在确认启动前已退出。");
            }

            if (readyEvent.WaitOne(0))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("等待新版本启动确认超时（20 秒），已取消本次更新。");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Normal startup, a fully valid handshake, or malformed update arguments.</summary>
    internal static UpdateLaunchKind ClassifyUpdateArgs(string[]? args, out UpdateLaunchArgs? parsed)
    {
        parsed = null;
        if (args is null || args.Length == 0) { return UpdateLaunchKind.NotUpdate; }

        var anyUpdateFlag = false;
        foreach (var arg in args)
        {
            if (Array.IndexOf(UpdateFlags, arg) >= 0) { anyUpdateFlag = true; break; }
        }

        if (!anyUpdateFlag) { return UpdateLaunchKind.NotUpdate; }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (Array.IndexOf(UpdateFlags, key) < 0) { return UpdateLaunchKind.Invalid; }
            if (i + 1 >= args.Length) { return UpdateLaunchKind.Invalid; }
            if (values.ContainsKey(key)) { return UpdateLaunchKind.Invalid; }

            values[key] = args[i + 1];
            i++;
        }

        if (values.Count != UpdateFlags.Length) { return UpdateLaunchKind.Invalid; }

        if (!int.TryParse(values[WaitForPidArg], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return UpdateLaunchKind.Invalid;
        }

        if (!long.TryParse(values[WaitForStartArg], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks) || startTicks <= 0)
        {
            return UpdateLaunchKind.Invalid;
        }

        if (!IsValidReadyEventName(values[ReadyEventArg])) { return UpdateLaunchKind.Invalid; }

        var cleanupOld = values[CleanupOldArg];
        if (string.IsNullOrWhiteSpace(cleanupOld)) { return UpdateLaunchKind.Invalid; }

        parsed = new UpdateLaunchArgs
        {
            ParentPid = pid,
            ParentStartUtcTicks = startTicks,
            ReadyEventName = values[ReadyEventArg],
            CleanupOldFileName = cleanupOld,
        };
        return UpdateLaunchKind.Valid;
    }

    /// <summary>Only accepts names this module generates, so no arbitrary event is opened.</summary>
    internal static bool IsValidReadyEventName(string? name)
    {
        if (string.IsNullOrEmpty(name)) { return false; }
        if (!name.StartsWith(ReadyEventPrefix, StringComparison.Ordinal)) { return false; }

        var suffix = name.Substring(ReadyEventPrefix.Length);
        return suffix.Length == 32 && AllHex(suffix);
    }

    /// <summary>
    /// A cleanup target must be a bare file name in the exe's own directory matching exactly
    /// <c>&lt;own base name&gt;.old-&lt;32 hex&gt;.exe</c>; arbitrary paths are rejected.
    /// </summary>
    internal static bool IsValidCleanupOldFileName(string? fileName, string ownExePath)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(ownExePath)) { return false; }
        if (fileName != Path.GetFileName(fileName)) { return false; }
        if (fileName.IndexOfAny(new[] { '/', '\\' }) >= 0) { return false; }
        if (Path.IsPathRooted(fileName)) { return false; }
        if (fileName.Contains("..", StringComparison.Ordinal)) { return false; }
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { return false; }

        var baseName = Path.GetFileNameWithoutExtension(ownExePath);
        if (string.IsNullOrEmpty(baseName)) { return false; }

        var prefix = baseName + OldFileMarker;
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) { return false; }

        var hex = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - 4);
        if (hex.Length != 32 || !AllHex(hex)) { return false; }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(ownExePath));
            if (string.IsNullOrEmpty(directory)) { return false; }
            var combined = Path.GetFullPath(Path.Combine(directory, fileName));
            return string.Equals(Path.GetDirectoryName(combined), directory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildOldFileName(string baseName, string guid32)
        => baseName + OldFileMarker + guid32 + ".exe";

    internal static string BuildNewTempFileName(string baseName, string guid32)
        => baseName + ".new-" + guid32 + ".exe";

    /// <summary>Deletes only the exact backup file, re-validated against the own exe path.</summary>
    internal static bool TryCleanupBackup(string fileName, string ownExePath)
    {
        if (!IsValidCleanupOldFileName(fileName, ownExePath)) { return false; }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(ownExePath));
            if (string.IsNullOrEmpty(directory)) { return false; }
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path)) { File.Delete(path); }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Verifies the PE machine is x64 and the version metadata matches.</summary>
    private static void VerifyUpdateFile(string path, Version expected)
    {
        if (!File.Exists(path) || Directory.Exists(path))
        {
            throw new FileNotFoundException("找不到已校验的更新文件：" + path, path);
        }

        if (!TryReadPeMachine(path, out var machine))
        {
            throw new InvalidOperationException("更新文件不是有效的 PE 可执行文件。");
        }

        if (machine != PeMachineAmd64)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "更新文件不是 Windows x64 可执行文件（machine=0x{0:X4}）。", machine));
        }

        FileVersionInfo info;
        try
        {
            info = FileVersionInfo.GetVersionInfo(path);
        }
        catch (Exception ex)
        {
            throw new IOException("无法读取更新文件版本信息：" + ex.Message, ex);
        }

        if (!VersionMatches(info.FileVersion, expected) && !VersionMatches(info.ProductVersion, expected))
        {
            var actual = info.FileVersion ?? info.ProductVersion ?? "未知";
            throw new InvalidOperationException($"更新文件版本（{actual}）与预期版本 {expected} 不符。");
        }
    }

    /// <summary>Reads the PE COFF machine field without external dependencies.</summary>
    internal static bool TryReadPeMachine(string path, out ushort machine)
    {
        machine = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 64) { return false; }
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt16() != 0x5A4D) { return false; } // 'MZ'

            stream.Position = 0x3C;
            var peOffset = reader.ReadUInt32();
            if (peOffset + 6 > stream.Length) { return false; }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) { return false; } // 'PE\0\0'

            machine = reader.ReadUInt16();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Matches ignoring components the expected version does not specify ("1.2.3" ≈ "1.2.3.0").</summary>
    internal static bool VersionMatches(string? raw, Version expected)
    {
        if (expected is null || string.IsNullOrWhiteSpace(raw)) { return false; }
        if (!Version.TryParse(raw.Trim(), out var actual)) { return false; }

        if (actual.Major != expected.Major) { return false; }
        if (actual.Minor != expected.Minor) { return false; }
        if (expected.Build >= 0 && actual.Build != expected.Build) { return false; }
        if (expected.Revision >= 0 && actual.Revision != expected.Revision) { return false; }
        return true;
    }

    private static bool IsWindowsUpdateArgument(string argument)
        => argument.StartsWith("--wait-for-", StringComparison.Ordinal)
            || argument.StartsWith("--update-ready-event", StringComparison.Ordinal)
            || argument.StartsWith("--cleanup-old", StringComparison.Ordinal);

    private static bool AllHex(string value)
    {
        foreach (var c in value)
        {
            if (!IsHex(c)) { return false; }
        }

        return true;
    }

    private static bool IsHex(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>
    /// Kills only this hand-over's child and confirms it exited. If exit cannot be confirmed,
    /// returns false with a message and the caller must keep the old backup untouched.
    /// </summary>
    private static bool KillIfRunning(Process? process, out string? error)
    {
        error = null;
        if (process is null) { return true; }

        var pid = "?";
        try { pid = process.Id.ToString(CultureInfo.InvariantCulture); } catch { }

        try
        {
            if (process.HasExited) { return true; }

            process.Kill(entireProcessTree: false);
            if (!process.WaitForExit(10000) && !process.HasExited)
            {
                error = "无法终止本次启动的新版本进程（PID " + pid + "），已保留旧版本备份。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (process.HasExited) { return true; }
            }
            catch
            {
                // Cannot verify exit; fall through to the conservative error.
            }

            error = "无法确认本次启动的新版本进程（PID " + pid + "）已退出，已保留旧版本备份：" + ex.Message;
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) { File.Delete(path); }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static bool TryDeleteWithRetry(string path, int attempts = 10, int delayMs = 150)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return true; }
                File.Delete(path);
                return true;
            }
            catch
            {
                Thread.Sleep(delayMs);
            }
        }

        return string.IsNullOrEmpty(path) || !File.Exists(path);
    }

    /// <summary>Restores the old exe without overwriting any file we cannot attribute.</summary>
    private static bool TryMoveWithRetry(string source, string destination, int attempts = 10, int delayMs = 150)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                File.Move(source, destination);
                return true;
            }
            catch
            {
                Thread.Sleep(delayMs);
            }
        }

        return false;
    }

    internal enum UpdateLaunchKind
    {
        NotUpdate,
        Valid,
        Invalid,
    }

    internal sealed class UpdateLaunchArgs
    {
        public int ParentPid { get; init; }

        public long ParentStartUtcTicks { get; init; }

        public string ReadyEventName { get; init; } = string.Empty;

        public string CleanupOldFileName { get; init; } = string.Empty;
    }

    /// <summary>
    /// The single file-swap transaction shared by production and tests. <see cref="Execute"/>
    /// records the completed steps; <see cref="Rollback"/> undoes exactly those steps and never
    /// overwrites a file it cannot attribute.
    /// </summary>
    internal sealed class ExeSwapTransaction
    {
        private readonly string _sourceDownload;
        private readonly string _currentExe;
        private readonly string _newTemp;
        private readonly string _oldPath;

        public ExeSwapTransaction(string sourceDownload, string currentExe, string newTemp, string oldPath)
        {
            _sourceDownload = sourceDownload;
            _currentExe = currentExe;
            _newTemp = newTemp;
            _oldPath = oldPath;
        }

        public bool OldMoved { get; private set; }

        public bool NewMovedIntoPlace { get; private set; }

        public string OldPath => _oldPath;

        public string NewTempPath => _newTemp;

        public void Execute(Action? beforeFinalMove = null)
        {
            File.Copy(_sourceDownload, _newTemp, overwrite: false);
            File.Move(_currentExe, _oldPath);
            OldMoved = true;
            beforeFinalMove?.Invoke();
            File.Move(_newTemp, _currentExe);
            NewMovedIntoPlace = true;
        }

        public string? Rollback(Process? process)
        {
            if (!KillIfRunning(process, out var killError)) { return killError; }

            if (NewMovedIntoPlace && !TryDeleteWithRetry(_currentExe))
            {
                return "无法删除未成功启动的新版本文件，已保留旧版本备份：" + _oldPath;
            }

            if (OldMoved && File.Exists(_oldPath))
            {
                if (File.Exists(_currentExe))
                {
                    return "目标位置存在无法归属的文件，已保留旧版本备份：" + _oldPath;
                }

                if (!TryMoveWithRetry(_oldPath, _currentExe))
                {
                    return "无法将旧版本恢复为原文件名：" + _currentExe;
                }
            }

            TryDelete(_newTemp);
            return null;
        }
    }
}
