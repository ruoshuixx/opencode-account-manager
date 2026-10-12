using System.Diagnostics;
using AccountManager.App.Services;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// Task 5 contract for the desktop opener (browser URL + log directory).
///
/// These tests never start a real browser or file manager: every external launch is reached only
/// through the injected <see cref="IShellRunner"/> boundary plus the platform flag. The production
/// types (<see cref="ShellLauncher"/>, <see cref="IShellRunner"/>, <see cref="ShellLaunchResult"/>,
/// <see cref="ShellLaunchException"/>) live in <c>AccountManager.App.Services</c> and are internal;
/// this assembly reaches them through <c>InternalsVisibleTo</c>.
/// </summary>
public class ShellLauncherTests
{
    private const string OAuthUrl = "https://example.test/auth?state=fake&code=fake";

    // ── BuildStartInfo: no shell text, one literal argument ─────────────────────────────────

    [Fact]
    public void UnixTargetIsOneArgumentNotShellText()
    {
        var info = ShellLauncher.BuildStartInfo("xdg-open", "https://example.test/auth?state=fake&code=fake");

        Assert.False(info.UseShellExecute);
        Assert.Single(info.ArgumentList);
        Assert.Equal("https://example.test/auth?state=fake&code=fake", info.ArgumentList[0]);
    }

    [Theory]
    [InlineData("https://example.test/auth?state=fake&code=fake")]
    [InlineData("https://例子.测试/路径/授权?state=一 二&code=a b")]
    [InlineData("/home/用户/我的 日志")]
    public void UnixTargetKeepsSpacesChineseAndAmpersandAsOneLiteralArgument(string target)
    {
        var info = ShellLauncher.BuildStartInfo("xdg-open", target);

        Assert.Equal("xdg-open", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Single(info.ArgumentList);
        Assert.Equal(target, info.ArgumentList[0]);
    }

    [Fact]
    public void GioFallbackUsesTheFixedOpenSubcommandAsSeparateArguments()
    {
        var info = ShellLauncher.BuildGioStartInfo(OAuthUrl);

        Assert.Equal("gio", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(new[] { "open", OAuthUrl }, info.ArgumentList);
    }

    [Fact]
    public void WindowsOpenKeepsTheShellAndPassesTheTargetVerbatim()
    {
        var info = ShellLauncher.BuildWindowsStartInfo(@"C:\logs\我的 日志");

        Assert.True(info.UseShellExecute);
        Assert.Equal(@"C:\logs\我的 日志", info.FileName);
        Assert.Empty(info.ArgumentList);
    }

    // ── target allow-list ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("https://example.test")]
    [InlineData("https://example.test/auth?state=fake&code=fake")]
    [InlineData("https://例子.测试/路径/授权")]
    [InlineData("http://127.0.0.1:1455/auth/callback")]
    public void AbsoluteHttpAndHttpsTargetsAreAllowed(string target)
        => Assert.True(ShellLauncher.IsAllowedTarget(target));

    [Fact]
    public void AnExistingLocalDirectoryIsAllowed()
    {
        var directory = NewTempDirectory();
        try { Assert.True(ShellLauncher.IsAllowedTarget(directory)); }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    [InlineData("./logs")]
    [InlineData("logs/sub")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/x")]
    [InlineData("mailto:user@example.test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https")]
    [InlineData("//example.test/x")]
    [InlineData("https://example.test/a\u0000b")]
    [InlineData("https://example.test/a\nb")]
    [InlineData("https://example.test/\u0007")]
    public void RelativeUnsupportedAndControlCharacterTargetsAreRejected(string target)
        => Assert.False(ShellLauncher.IsAllowedTarget(target));

    [Fact]
    public void ARegularFileIsRejected()
    {
        var file = Path.Combine(Path.GetTempPath(), "shell-launcher-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "x");
        try { Assert.False(ShellLauncher.IsAllowedTarget(file)); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void AMissingAbsoluteDirectoryIsRejected()
    {
        var missing = Path.Combine(Path.GetTempPath(), "shell-launcher-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(ShellLauncher.IsAllowedTarget(missing));
    }

    // ── OpenAsync: rejected / pre-cancelled never reach the OS ──────────────────────────────

    [Fact]
    public async Task DisallowedTargetIsRejectedWithoutLaunchingAnything()
    {
        var runner = new RecordingRunner(Started());
        var launcher = new ShellLauncher(runner, isWindows: false);

        await Assert.ThrowsAsync<ArgumentException>(() => launcher.OpenAsync("file:///etc/passwd"));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task PreexistingCancellationNeverLaunchesAnything()
    {
        var runner = new RecordingRunner(Started());
        var launcher = new ShellLauncher(runner, isWindows: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await Record.ExceptionAsync(() => launcher.OpenAsync(OAuthUrl, cts.Token));

        Assert.Empty(runner.Calls);
        Assert.True(error is null or OperationCanceledException);
    }

    // ── OpenAsync: xdg-open, then gio open only on a missing xdg-open ───────────────────────

    [Fact]
    public async Task MissingXdgOpenFallsBackToGioOpenWithFixedArguments()
    {
        var runner = new RecordingRunner(Missing(), Started());
        var launcher = new ShellLauncher(runner, isWindows: false);

        await launcher.OpenAsync(OAuthUrl);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal("xdg-open", runner.Calls[0].FileName);
        Assert.Equal(new[] { OAuthUrl }, runner.Calls[0].ArgumentList);
        Assert.Equal("gio", runner.Calls[1].FileName);
        Assert.Equal(new[] { "open", OAuthUrl }, runner.Calls[1].ArgumentList);
    }

    [Theory]
    [InlineData((int)LaunchOutcome.PermissionDenied, 0)]
    [InlineData((int)LaunchOutcome.Failed, 3)]
    [InlineData((int)LaunchOutcome.TimedOut, 0)]
    public async Task OnlyAMissingLauncherFallsBackToGio(int outcome, int exitCode)
    {
        var runner = new RecordingRunner(new ShellLaunchResult((LaunchOutcome)outcome, exitCode, "boom"));
        var launcher = new ShellLauncher(runner, isWindows: false);

        await Assert.ThrowsAsync<ShellLaunchException>(() => launcher.OpenAsync(OAuthUrl));

        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task MissingXdgAndMissingGioTellTheUserToOpenManually()
    {
        var runner = new RecordingRunner(Missing(), Missing());
        var launcher = new ShellLauncher(runner, isWindows: false);

        var error = await Assert.ThrowsAsync<ShellLaunchException>(() => launcher.OpenAsync(OAuthUrl));

        Assert.Contains("手动", error.Message);
    }

    // ── OpenAsync: fixed, safe failure text (no target / no raw stderr) ─────────────────────

    [Fact]
    public async Task LauncherFailureNeverLeaksTheTargetOrRawStderr()
    {
        const string stderr = "sh: 1: xdg-open: " + OAuthUrl + " state=fake&code=fake";
        var runner = new RecordingRunner(new ShellLaunchResult(LaunchOutcome.Failed, 127, stderr));
        var launcher = new ShellLauncher(runner, isWindows: false);

        var error = await Assert.ThrowsAsync<ShellLaunchException>(() => launcher.OpenAsync(OAuthUrl));

        Assert.DoesNotContain(OAuthUrl, error.Message);
        Assert.DoesNotContain("state=fake", error.Message);
        Assert.DoesNotContain("code=fake", error.Message);
        Assert.DoesNotContain(stderr, error.Message);
        Assert.DoesNotContain("sh: 1", error.Message);
    }

    // ── Windows keeps the shell open; the log directory uses the same launcher ──────────────

    [Fact]
    public async Task WindowsUsesTheShellOpenAndNeverTheUnixLauncher()
    {
        var runner = new RecordingRunner(Started());
        var launcher = new ShellLauncher(runner, isWindows: true);

        await launcher.OpenAsync(OAuthUrl);

        var call = Assert.Single(runner.Calls);
        Assert.True(call.UseShellExecute);
        Assert.Equal(OAuthUrl, call.FileName);
        Assert.Empty(call.ArgumentList);
    }

    [Fact]
    public async Task WindowsNeverFallsBackToTheUnixGioLauncher()
    {
        var runner = new RecordingRunner(Missing());
        var launcher = new ShellLauncher(runner, isWindows: true);

        await Assert.ThrowsAsync<ShellLaunchException>(() => launcher.OpenAsync(OAuthUrl));

        Assert.Single(runner.Calls);
        Assert.True(runner.Calls[0].UseShellExecute);
    }

    [Fact]
    public async Task AnExistingLogDirectoryIsOpenedThroughTheSameLauncher()
    {
        var directory = NewTempDirectory();
        try
        {
            var runner = new RecordingRunner(Started());
            var launcher = new ShellLauncher(runner, isWindows: false);

            await launcher.OpenAsync(directory);

            var call = Assert.Single(runner.Calls);
            Assert.Equal("xdg-open", call.FileName);
            Assert.Equal(new[] { directory }, call.ArgumentList);
        }
        finally { Directory.Delete(directory, true); }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static ShellLaunchResult Started() => new(LaunchOutcome.Started, 0, null);

    private static ShellLaunchResult Missing() => new(LaunchOutcome.Missing, 0, null);

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "shell-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Records each start info and replays scripted outcomes; never starts a process.</summary>
    private sealed class RecordingRunner : IShellRunner
    {
        private readonly Queue<ShellLaunchResult> _results;

        public RecordingRunner(params ShellLaunchResult[] results)
            => _results = new Queue<ShellLaunchResult>(results);

        public List<ProcessStartInfo> Calls { get; } = new();

        public Task<ShellLaunchResult> RunAsync(ProcessStartInfo startInfo, CancellationToken ct)
        {
            Calls.Add(startInfo);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : Started());
        }
    }
}
