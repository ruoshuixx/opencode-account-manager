using AccountManager.App.Services;
using System.Text;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// The tray probe runs two fixed, shell-free busctl queries within one budget and parses each strictly
/// into a fresh tray state. The command runner is injected here, so no DBus, busctl or real
/// StatusNotifier host is contacted and no secret-bearing stderr is ever read.
/// </summary>
public class LinuxTrayAvailabilityTests
{
    private static TrayCommandResult Result(int exitCode, string? stdout, bool timedOut = false)
        => new(exitCode, stdout, timedOut);

    [Fact]
    public void TheOwnerQueryOnlyAsksNameHasOwner()
    {
        Assert.Equal(new[]
        {
            "--user",
            "call",
            "org.freedesktop.DBus",
            "/org/freedesktop/DBus",
            "org.freedesktop.DBus",
            "NameHasOwner",
            "s",
            "org.kde.StatusNotifierWatcher",
        }, LinuxTrayAvailability.BuildNameHasOwnerArguments());
    }

    [Fact]
    public void TheHostQueryOnlyReadsTheHostRegisteredProperty()
    {
        Assert.Equal(new[]
        {
            "--user",
            "get-property",
            "org.kde.StatusNotifierWatcher",
            "/StatusNotifierWatcher",
            "org.kde.StatusNotifierWatcher",
            "IsStatusNotifierHostRegistered",
        }, LinuxTrayAvailability.BuildHostRegisteredArguments());
    }

    [Fact]
    public void NeitherFixedQueryGoesThroughAShell()
    {
        foreach (var args in new[]
        {
            LinuxTrayAvailability.BuildNameHasOwnerArguments(),
            LinuxTrayAvailability.BuildHostRegisteredArguments(),
        })
        {
            Assert.DoesNotContain(args, a => a is "sh" or "bash" or "-c");
            Assert.DoesNotContain(args, a => a.Contains(';') || a.Contains('&') || a.Contains('|'));
        }
    }

    [Theory]
    [InlineData(0, "b true", false, TrayAvailability.Available)]
    [InlineData(0, "b true\n", false, TrayAvailability.Available)]
    [InlineData(0, "b false", false, TrayAvailability.Unavailable)]
    [InlineData(0, "b false\n", false, TrayAvailability.Unavailable)]
    [InlineData(0, "true", false, TrayAvailability.Unknown)]
    [InlineData(0, "b TRUE", false, TrayAvailability.Unknown)]
    [InlineData(0, "b 1", false, TrayAvailability.Unknown)]
    [InlineData(0, "b maybe", false, TrayAvailability.Unknown)]
    [InlineData(0, "b true\nb false", false, TrayAvailability.Unknown)]
    [InlineData(1, "b true", false, TrayAvailability.Unknown)]
    [InlineData(1, null, false, TrayAvailability.Unknown)]
    [InlineData(0, "b true", true, TrayAvailability.Unknown)]
    [InlineData(0, null, false, TrayAvailability.Unknown)]
    [InlineData(0, "", false, TrayAvailability.Unknown)]
    public void OnlyAStrictBooleanResponseMapsToAFreshTrayState(
        int exitCode, string? stdout, bool timedOut, TrayAvailability expected)
    {
        Assert.Equal(expected, LinuxTrayAvailability.ParseNameHasOwner(Result(exitCode, stdout, timedOut)));
        Assert.Equal(expected, LinuxTrayAvailability.ParseHostRegistered(Result(exitCode, stdout, timedOut)));
    }

    [Fact]
    public async Task AnAvailableHostNeedsBothTheOwnerAndTheHostPropertyToSucceed()
    {
        var calls = new List<IReadOnlyList<string>>();
        var probe = new LinuxTrayAvailability((args, _) =>
        {
            calls.Add(args);
            return Task.FromResult(Result(0, "b true\n"));
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Available, await probe.CheckAsync());
        Assert.Equal(2, calls.Count);
        Assert.Equal(LinuxTrayAvailability.BuildNameHasOwnerArguments(), calls[0]);
        Assert.Equal(LinuxTrayAvailability.BuildHostRegisteredArguments(), calls[1]);
    }

    [Fact]
    public async Task AMissingOwnerNameIsUnavailableAndThePropertyIsNotQueried()
    {
        var calls = 0;
        var probe = new LinuxTrayAvailability((_, _) =>
        {
            calls++;
            return Task.FromResult(Result(0, "b false"));
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Unavailable, await probe.CheckAsync());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AnUncertainOwnerQueryStaysUnknownAndThePropertyIsNotQueried()
    {
        var calls = 0;
        var probe = new LinuxTrayAvailability((_, _) =>
        {
            calls++;
            return Task.FromResult(Result(1, null));
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Unknown, await probe.CheckAsync());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AFalseHostPropertyAfterALiveOwnerIsUnavailable()
    {
        var calls = 0;
        var probe = new LinuxTrayAvailability((_, _) =>
        {
            calls++;
            return Task.FromResult(Result(0, calls == 1 ? "b true" : "b false"));
        }, TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Unavailable, await probe.CheckAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CheckBoundsTheQueryByItsBudgetAndReportsUnknown()
    {
        var runner = new TrayCommandRunner(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Result(0, "b true");
        });
        var probe = new LinuxTrayAvailability(runner, TimeSpan.FromMilliseconds(150));
        var started = DateTime.UtcNow;

        var result = await probe.CheckAsync();

        Assert.Equal(TrayAvailability.Unknown, result);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AThrowingQueryIsUnknownAndTheRawErrorIsNotSurfaced()
    {
        var probe = new LinuxTrayAvailability((_, _) => throw new InvalidOperationException("secret-dbus-error"), TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Unknown, await probe.CheckAsync());
    }

    [Fact]
    public async Task APreCancelledCheckDoesNotStartTheQuery()
    {
        var started = false;
        var probe = new LinuxTrayAvailability((_, _) =>
        {
            started = true;
            return Task.FromResult(Result(0, "b true"));
        }, TimeSpan.FromSeconds(3));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(TrayAvailability.Unknown, await probe.CheckAsync(cts.Token));
        Assert.False(started);
    }

    // ── The real production reader (unchanged body, only exposed internal): a live MemoryStream must
    //    surface overflow instead of silently truncating a valid-looking prefix. ────────────────────

    [Fact]
    public async Task ReadCappedReturnsThePayloadWhenTheStreamEndsExactlyAtTheCap()
    {
        var payload = Encoding.ASCII.GetBytes("b true!!"); // exactly 8 bytes = cap
        using var stream = new MemoryStream(payload);

        var result = await SystemTrayCommandRunner.ReadCappedAsync(stream, 8, CancellationToken.None);

        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task ReadCappedRejectsOneByteBeyondTheCap()
    {
        using var stream = new MemoryStream(new byte[9]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SystemTrayCommandRunner.ReadCappedAsync(stream, 8, CancellationToken.None));
    }

    [Fact]
    public async Task ReadCappedRejectsAValidLookingPrefixPaddedToTheCapWithTrailingJunk()
    {
        // "b true" + spaces up to 256 + junk: must not be silently truncated into a success.
        var buffer = new byte[260];
        Encoding.ASCII.GetBytes("b true").CopyTo(buffer, 0);
        for (var i = 6; i < 256; i++) { buffer[i] = (byte)' '; }
        Encoding.ASCII.GetBytes("junk").CopyTo(buffer, 256);
        using var stream = new MemoryStream(buffer);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SystemTrayCommandRunner.ReadCappedAsync(stream, 256, CancellationToken.None));
    }

    // ── Contract: SystemTrayCommandRunner.CompleteAsync(stdout, stderr, exit, exitCode, abortOwned,
    //    ct). The real runner passes its recorded live tasks, the process exit-code accessor and a
    //    once-only owned-child kill+cancel action. Success needs all three tasks plus a successful
    //    exit; any fault, pending pipe or cancellation aborts once and yields the fixed unknown. These
    //    are TCS/Memory-only oracles: an absent or partial implementation cannot pass them. ───────────

    [Fact]
    public async Task CompleteReturnsTheResultOnlyWhenAllStreamsAndExitSucceed()
    {
        var stdout = Task.FromResult(Encoding.ASCII.GetBytes("b true\n"));
        var stderr = Task.CompletedTask;
        var exit = Task.CompletedTask;
        var aborts = 0;

        var result = await SystemTrayCommandRunner.CompleteAsync(
            stdout, stderr, exit, () => 0, () => aborts++, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("b true\n", result.StandardOutput);
        Assert.Equal(0, aborts);
    }

    [Fact]
    public async Task CompleteAbortsOnceAndFailsUnknownWhenStdoutFaultsBeforeExit()
    {
        var stdout = Task.FromException<byte[]>(new InvalidDataException("overflow"));
        var stderr = new TaskCompletionSource().Task; // never ends
        var exit = new TaskCompletionSource().Task;   // never ends
        var aborts = 0;

        var result = await SystemTrayCommandRunner.CompleteAsync(
            stdout, stderr, exit, () => 0, () => aborts++, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Unknown, LinuxTrayAvailability.ParseHostRegistered(result));
        Assert.Equal(1, aborts);
    }

    [Fact]
    public async Task CompleteAbortsOnceAndFailsUnknownWhenStderrFaultsBeforeExit()
    {
        var stdout = new TaskCompletionSource<byte[]>().Task; // never ends
        var stderr = Task.FromException(new InvalidOperationException("stderr failed"));
        var exit = new TaskCompletionSource().Task;           // never ends
        var aborts = 0;

        var result = await SystemTrayCommandRunner.CompleteAsync(
            stdout, stderr, exit, () => 0, () => aborts++, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Unknown, LinuxTrayAvailability.ParseHostRegistered(result));
        Assert.Equal(1, aborts);
    }

    [Fact]
    public async Task CompleteAbortsOnceAndFailsUnknownWhenExitFaults()
    {
        var stdout = new TaskCompletionSource<byte[]>().Task; // never ends
        var stderr = new TaskCompletionSource().Task;         // never ends
        var exit = Task.FromException(new InvalidOperationException("exit failed"));
        var aborts = 0;

        var result = await SystemTrayCommandRunner.CompleteAsync(
            stdout, stderr, exit, () => 0, () => aborts++, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Unknown, LinuxTrayAvailability.ParseHostRegistered(result));
        Assert.Equal(1, aborts);
    }

    [Fact]
    public async Task CompleteAbortsOnceWhenExitSucceedsButAPipeNeverEndsWithinTheOriginalBudget()
    {
        // The process exited, but a pipe never reaches EOF: success is impossible, and there is no
        // extra drain budget, so the caller's original deadline expires and the runner aborts once.
        var stdout = new TaskCompletionSource<byte[]>().Task; // pending pipe
        var stderr = new TaskCompletionSource().Task;         // pending pipe
        var exit = Task.CompletedTask;                        // process already exited
        var aborts = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await SystemTrayCommandRunner.CompleteAsync(
            stdout, stderr, exit, () => 0, () => aborts++, cts.Token).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Unknown, LinuxTrayAvailability.ParseHostRegistered(result));
        Assert.Equal(1, aborts);
    }

    [Fact]
    public async Task CompleteDrainsPipesThatFinishShortlyAfterExitWithinTheOriginalBudget()
    {
        // A successful exit may be followed by buffered pipe completion inside the same deadline; that
        // is a real success, not a failure, and it must never abort the owned child.
        var stdout = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborts = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var pending = SystemTrayCommandRunner.CompleteAsync(
            stdout.Task, stderr.Task, Task.CompletedTask, () => 0, () => aborts++, cts.Token);

        _ = Task.Run(async () =>
        {
            await Task.Delay(30);
            stdout.SetResult(Encoding.ASCII.GetBytes("b true\n"));
            stderr.SetResult();
        });

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("b true\n", result.StandardOutput);
        Assert.Equal(0, aborts);
    }

    [Fact]
    public async Task CompleteAbortsOnceAndFailsUnknownOnCancellation()
    {
        using var cts = new CancellationTokenSource();
        var stdout = new TaskCompletionSource<byte[]>().Task;
        var stderr = new TaskCompletionSource().Task;
        var exit = new TaskCompletionSource().Task;
        var aborts = 0;

        var pending = SystemTrayCommandRunner.CompleteAsync(
            stdout, stderr, exit, () => 0, () => aborts++, cts.Token);
        cts.Cancel();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Unknown, LinuxTrayAvailability.ParseHostRegistered(result));
        Assert.Equal(1, aborts);
    }

    [Fact]
    public async Task CompleteAbortsOnceAndFailsUnknownWhenTheExitCodeAccessorThrows()
    {
        var stdout = Task.FromResult(Encoding.ASCII.GetBytes("b true"));
        var stderr = Task.CompletedTask;
        var exit = Task.CompletedTask;
        var aborts = 0;

        var result = await SystemTrayCommandRunner.CompleteAsync(
            stdout, stderr, exit, () => throw new InvalidOperationException("no exit code"), () => aborts++, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TrayAvailability.Unknown, LinuxTrayAvailability.ParseHostRegistered(result));
        Assert.Equal(1, aborts);
    }

    // ── Contract: a completed exit whose code is already known bad (or unreadable) must fail promptly,
    //    not wait out the caller's deadline for never-ending pipes. The external WaitAsync bound is much
    //    shorter than the helper's own deadline, so the only way to pass is an immediate failure. ───────

    [Fact]
    public async Task CompleteFailsImmediatelyWhenExitIsAlreadyNonzeroWithPendingPipes()
    {
        var stdout = new TaskCompletionSource<byte[]>().Task; // pending pipe
        var stderr = new TaskCompletionSource().Task;         // pending pipe
        var aborts = 0;

        var result = await CompleteWithBoundAsync(
            stdout, stderr, Task.CompletedTask, () => 2, () => aborts++, TimeSpan.FromMilliseconds(300));

        Assert.Equal(TrayAvailability.Unknown, LinuxTrayAvailability.ParseHostRegistered(result));
        Assert.Equal(1, aborts);
    }

    [Fact]
    public async Task CompleteFailsImmediatelyWhenTheExitAccessorThrowsWithPendingPipes()
    {
        var stdout = new TaskCompletionSource<byte[]>().Task; // pending pipe
        var stderr = new TaskCompletionSource().Task;         // pending pipe
        var aborts = 0;

        var result = await CompleteWithBoundAsync(
            stdout, stderr, Task.CompletedTask, () => throw new InvalidOperationException("no exit code"),
            () => aborts++, TimeSpan.FromMilliseconds(300));

        Assert.Equal(TrayAvailability.Unknown, LinuxTrayAvailability.ParseHostRegistered(result));
        Assert.Equal(1, aborts);
    }

    [Fact]
    public async Task AHostResultThatCancelsTheCallerFirstIsUnknown()
    {
        // Same completed-task cancellation race at the public two-query level: the host runner cancels
        // the caller and then returns a completed `b true`. The outcome must be Unknown, not Available.
        var calls = 0;
        using var cts = new CancellationTokenSource();
        var probe = new LinuxTrayAvailability((_, _) =>
        {
            calls++;
            if (calls == 2) { cts.Cancel(); }
            return Task.FromResult(Result(0, "b true\n"));
        }, TimeSpan.FromSeconds(3));

        var result = await probe.CheckAsync(cts.Token);

        Assert.Equal(TrayAvailability.Unknown, result);
    }

    private static async Task<TrayCommandResult> CompleteWithBoundAsync(
        Task<byte[]> stdout,
        Task stderr,
        Task exit,
        Func<int> exitCode,
        Action abort,
        TimeSpan bound)
    {
        // The helper's own deadline is far longer than the test bound, so a pass cannot come from the
        // helper self-cancelling on expiry; only prompt failure passes.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = SystemTrayCommandRunner.CompleteAsync(stdout, stderr, exit, exitCode, abort, cts.Token);
        try
        {
            return await pending.WaitAsync(bound);
        }
        finally
        {
            // Never leave the helper pending on its abandoned outer deadline.
            cts.Cancel();
            SystemTrayCommandRunner.Observe(pending);
        }
    }
}
