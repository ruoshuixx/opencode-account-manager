using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AccountManager.App.Services;
using AccountManager.Core.OpenCode;

namespace AccountManager.Linux.IntegrationHarness;

[SupportedOSPlatform("linux")]
internal static class HarnessSupport
{
    internal static void RequireLinuxX64()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The native harness requires Linux x64.");
        }
    }

    internal static void RequireArgs(string[] args, int count)
    {
        if (args.Length != count) { throw new ArgumentException("Invalid harness argument count."); }
    }

    internal static string NewPrivateDirectory(string parent, string prefix)
    {
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, PrivateDirectoryMode);
        var path = Path.Combine(parent, prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, PrivateDirectoryMode);
        return path;
    }

    internal static UnixFileMode PrivateDirectoryMode =>
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static UnixFileMode PrivateFileMode => UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static void AssertMode(string path, UnixFileMode expected, string purpose)
    {
        var actual = File.GetUnixFileMode(path);
        if (actual != expected) { throw new InvalidOperationException(purpose + " has an unexpected Unix mode."); }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executable,
        IEnumerable<string> arguments,
        string? homeOverride = null,
        bool redirectOutput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) { startInfo.ArgumentList.Add(argument); }

        // Every scenario child gets a deliberately small synthetic environment. It cannot inherit
        // credentials, real account/config paths, proxy settings or user CLI overrides.
        startInfo.Environment.Clear();
        var harnessRoot = Environment.GetEnvironmentVariable("OCAM_LINUX_HARNESS_ROOT")
            ?? Environment.GetEnvironmentVariable("HOME")
            ?? Path.GetTempPath();
        Directory.CreateDirectory(harnessRoot);
        var home = homeOverride ?? Path.Combine(harnessRoot, "child-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        File.SetUnixFileMode(home, PrivateDirectoryMode);
        var temp = Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath();
        var runtimeDirectory = Path.Combine(home, ".runtime");
        Directory.CreateDirectory(runtimeDirectory);
        File.SetUnixFileMode(runtimeDirectory, PrivateDirectoryMode);
        var bundleDirectory = Path.Combine(home, ".bundle-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundleDirectory);
        File.SetUnixFileMode(bundleDirectory, PrivateDirectoryMode);
        startInfo.Environment["HOME"] = home;
        startInfo.Environment["TMPDIR"] = temp;
        startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share");
        startInfo.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
        startInfo.Environment["XDG_CACHE_HOME"] = Path.Combine(home, ".cache");
        startInfo.Environment["XDG_RUNTIME_DIR"] = runtimeDirectory;
        startInfo.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundleDirectory;
        startInfo.Environment["OCAM_LINUX_HARNESS_ROOT"] = harnessRoot;
        startInfo.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        startInfo.Environment["LANG"] = "C.UTF-8";
        startInfo.Environment["LC_ALL"] = "C.UTF-8";
        return startInfo;
    }

    internal static Process StartSelf(IEnumerable<string> arguments, string? homeOverride = null)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) { throw new InvalidOperationException("Harness process path is unavailable."); }
        var process = Process.Start(CreateStartInfo(executable, arguments, homeOverride))
            ?? throw new InvalidOperationException("Could not start a harness-owned child process.");
        return process;
    }

    internal static Process StartTrackedProcess(
        ICollection<Process> ownedProcesses,
        IEnumerable<string> arguments,
        string? homeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(ownedProcesses);
        var process = StartSelf(arguments, homeOverride);
        try
        {
            ownedProcesses.Add(process);
            return process;
        }
        catch
        {
            StopOwnedProcessAfterGracefulRequest(process);
            process.Dispose();
            throw;
        }
    }

    internal static Process TrackOwnedProcess(ICollection<Process> ownedProcesses, Process process)
    {
        ArgumentNullException.ThrowIfNull(ownedProcesses);
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            ownedProcesses.Add(process);
            return process;
        }
        catch
        {
            StopOwnedProcessAfterGracefulRequest(process);
            process.Dispose();
            throw;
        }
    }

    internal static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await RequestGracefulStopAsync(process, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            StopOwnedProcess(process);
            throw new TimeoutException("A harness-owned child process exceeded its bounded wait.");
        }
    }

    internal static void StopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: false); }
            process.WaitForExit(3000);
        }
        catch { }
    }

    private static void StopOwnedProcessAfterGracefulRequest(Process process)
    {
        RequestGracefulStop(process);
        try
        {
            if (process.WaitForExit(1000)) { return; }
        }
        catch { }
        StopOwnedProcess(process);
    }

    internal static async Task CleanupOwnedProcessesAsync(
        IEnumerable<Process> ownedProcesses,
        TimeSpan gracefulWait)
    {
        Exception? cleanupFailure = null;
        foreach (var process in ownedProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    RequestGracefulStop(process);
                    using var timeout = new CancellationTokenSource(gracefulWait);
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        StopOwnedProcess(process);
                    }
                }
                if (!process.HasExited)
                {
                    StopOwnedProcess(process);
                }
                if (!process.HasExited)
                {
                    throw new InvalidOperationException("A harness-owned child did not stop after bounded cleanup.");
                }
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
                StopOwnedProcess(process);
                var mayStillBeRunning = true;
                try { mayStillBeRunning = !process.HasExited; }
                catch { }
                if (mayStillBeRunning)
                {
                    WriteUnconfirmedChildMarker(process);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        if (cleanupFailure is not null)
        {
            throw new OwnedChildCleanupException(cleanupFailure);
        }
    }

    internal sealed class OwnedChildCleanupException(Exception innerException)
        : InvalidOperationException("Harness-owned child cleanup could not be confirmed.", innerException);

    private static async Task RequestGracefulStopAsync(Process process, TimeSpan wait)
    {
        RequestGracefulStop(process);
        if (process.HasExited) { return; }
        using var timeout = new CancellationTokenSource(wait);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private static void RequestGracefulStop(Process process)
    {
        try
        {
            if (!process.HasExited) { _ = kill(process.Id, 15); }
        }
        catch { }
    }

    private static void WriteUnconfirmedChildMarker(Process process)
    {
        var root = Environment.GetEnvironmentVariable("OCAM_LINUX_HARNESS_ROOT");
        if (string.IsNullOrWhiteSpace(root)) { return; }
        try
        {
            var marker = Path.Combine(root, "owned-child-" + Guid.NewGuid().ToString("N") + ".marker");
            var identity = ProcessIdentityReader.Read(process.Id);
            if (identity is null || identity.ProcessId != process.Id)
            {
                WriteAtomic(Path.Combine(root, "owned-child-identity-unavailable"), "unconfirmed\n");
                return;
            }
            WriteAtomic(marker, $"harness-child:{identity.ProcessId}:{identity.StartTimeTicks}:{Environment.TickCount64}\n");
        }
        catch
        {
            try { WriteAtomic(Path.Combine(root, "owned-child-identity-unavailable"), "unconfirmed\n"); }
            catch { }
        }
    }

    internal static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (File.Exists(path)) { return; }
            await Task.Delay(20).ConfigureAwait(false);
        }
        throw new TimeoutException("A harness-owned scenario marker did not arrive within its bound.");
    }

    internal static void WriteAtomic(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("A harness marker has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N"));
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        File.Move(temp, fullPath, overwrite: true);
    }

    internal static string Sha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    internal static void DeleteOwnedDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    internal static IDisposable UseChildRoot(string root)
    {
        var prior = Environment.GetEnvironmentVariable("OCAM_LINUX_HARNESS_ROOT");
        Environment.SetEnvironmentVariable("OCAM_LINUX_HARNESS_ROOT", Path.GetFullPath(root));
        return new EnvironmentRestore("OCAM_LINUX_HARNESS_ROOT", prior);
    }

    private sealed class EnvironmentRestore(string name, string? value) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
