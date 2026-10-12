using System.Diagnostics;
using AccountManager.App.Services;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// Task 5 fix-round-1 contract for the real <see cref="ShellRunner"/>.
///
/// These tests pin the missing process-start seam
/// (<c>ShellRunner(TimeSpan, Func&lt;ProcessStartInfo, Process?&gt;)</c>) and the two permitted
/// production-used boundaries (<c>DiscardAsync</c>, <c>WaitForLauncherAsync</c>). They never start a
/// real process: the injected delegate returns synthetic handles, and pipe behaviour is exercised
/// with real <see cref="MemoryStream"/> / blocked cancellable streams / TCS exit tasks. The proposed
/// members do not exist yet, so this file is the accepted initial contract red.
///
/// The existing <c>ShellLauncherTests</c> fixture is deliberately left untouched.
/// </summary>
public class ShellRunnerTests
{
    private const string OAuthUrl = "https://example.test/auth?state=fake&code=fake";

    // ── Finding 3: DiscardAsync consumes without retaining; respects cancellation ────────────────

    [Fact]
    public async Task DiscardAsyncCompletesAndConsumesAFiniteStream()
    {
        using var stream = new MemoryStream(new byte[200_000]);

        await ShellRunner.DiscardAsync(stream, CancellationToken.None);

        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task DiscardAsyncStopsOnCancellationWithoutHanging()
    {
        using var blocked = new BlockingStream();
        using var cts = new CancellationTokenSource();
        var discard = ShellRunner.DiscardAsync(blocked, cts.Token);

        await blocked.Entered;
        Assert.False(discard.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discard);
    }

    // ── Finding 3: WaitForLauncherAsync is bounded, cancellable, and launcher-exit driven ────────

    [Fact]
    public async Task WaitForLauncherReturnsStartedWhenExitCompletesWithZero()
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = ShellRunner.WaitForLauncherAsync(exit.Task, () => 0, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        exit.SetResult();

        var result = await waiting;
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
    }

    [Fact]
    public async Task WaitForLauncherReturnsFailedOnNonZeroExit()
    {
        var result = await ShellRunner.WaitForLauncherAsync(Task.CompletedTask, () => 3, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(LaunchOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task WaitForLauncherReturnsTimedOutAtDeadline()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        var result = await ShellRunner.WaitForLauncherAsync(never, () => 0, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(LaunchOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task WaitForLauncherPropagatesCancellation()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        using var cts = new CancellationTokenSource();
        var waiting = ShellRunner.WaitForLauncherAsync(never, () => 0, TimeSpan.FromSeconds(2), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task WaitForLauncherReturnsSuccessWhilePipesRemainOpen()
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var blocked = new BlockingStream();
        using var cts = new CancellationTokenSource();
        var discard = ShellRunner.DiscardAsync(blocked, cts.Token);

        await blocked.Entered;
        var waiting = ShellRunner.WaitForLauncherAsync(exit.Task, () => 0, TimeSpan.FromSeconds(30), CancellationToken.None);
        exit.SetResult();

        var result = await waiting;

        // Launcher exit is success even though the (browser-owned) pipe is still open.
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.False(discard.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discard);
    }

    // ── Finding 1 / 4: actual RunAsync behaviour on the process-start seam ───────────────────────

    [Fact]
    public async Task RunAsyncWindowsShellNonNullHandleReturnsStartedWithoutTouchingHandle()
    {
        var process = new TrackingProcess();
        var starts = 0;
        var runner = new ShellRunner(TimeSpan.FromSeconds(8), _ => { starts++; return process; });

        // A Windows shell-handoff start info. An unassociated Process handle has no streams and no
        // exit task, so a correct runner returns immediately without probing either.
        var result = await runner.RunAsync(ShellLauncher.BuildWindowsStartInfo(OAuthUrl), CancellationToken.None);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal(1, starts);
        Assert.True(process.WasDisposed, "the returned process handle must be released");
    }

    [Fact]
    public async Task RunAsyncWindowsShellNullHandoffIsStarted()
    {
        var runner = new ShellRunner(TimeSpan.FromSeconds(8), _ => null);

        var result = await runner.RunAsync(ShellLauncher.BuildWindowsStartInfo(OAuthUrl), CancellationToken.None);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
    }

    [Fact]
    public async Task RunAsyncUnixNullProcessIsFailed()
    {
        var runner = new ShellRunner(TimeSpan.FromSeconds(8), _ => null);

        var result = await runner.RunAsync(ShellLauncher.BuildStartInfo("xdg-open", OAuthUrl), CancellationToken.None);

        Assert.Equal(LaunchOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task RunAsyncPreCancelledStartsNothing()
    {
        var starts = 0;
        var runner = new ShellRunner(TimeSpan.FromSeconds(8), _ => { starts++; return null; });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(ShellLauncher.BuildStartInfo("xdg-open", OAuthUrl), cts.Token));

        Assert.Equal(0, starts);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Process handle that records disposal and has no associated OS process.</summary>
    private sealed class TrackingProcess : Process
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Reads never complete until cancelled; signals when the first read starts.</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
