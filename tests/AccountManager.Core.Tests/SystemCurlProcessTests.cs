using System.Diagnostics;
using System.Text;
using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>
/// Task 6 contract tests (tests-only RED) for the small curl-specific process boundary that the
/// production <c>SubscriptionService.RunCurlAsync</c> must call — not a test-only parallel transport.
///
/// Proposed production seam (see task-6-report.md; missing until implemented):
///
///   internal interface ICurlProcessHost : IAsyncDisposable
///   {
///       Stream StandardInput { get; }   // raw redirected stdin
///       Stream StandardOutput { get; }  // raw redirected stdout
///       Stream StandardError { get; }   // raw redirected stderr
///       int ExitCode { get; }
///       Task WaitForExitAsync(CancellationToken cancellationToken);
///       void KillOwned();               // the created curl only, never a tree / pid by name
///   }
///
///   internal delegate ICurlProcessHost CurlProcessFactory(ProcessStartInfo startInfo);
///
///   internal readonly record struct CurlFetchResult(bool TransportOk, int Status, byte[] Body, string? Error);
///
///   internal sealed class SystemCurlTransport
///   {
///       internal SystemCurlTransport(CurlProcessFactory? factory = null);
///       internal Task&lt;CurlFetchResult&gt; FetchAsync(
///           string executable, string url, string? proxy, string stdinConfig,
///           TimeSpan? budget = null, CancellationToken cancellationToken = default);
///   }
///
/// Rules under test: config is fed over stdin while stdout/stderr are drained concurrently from the
/// start; one monotonic budget covers write/exit/drains; cancellation, oversize and budget expiry
/// stop at once and kill only the owned child; a non-zero curl exit is failure even with a plausible
/// 200 tail; stderr is bounded/discarded and never returned; the host is always disposed. Every
/// await is bounded by <see cref="WithTimeout"/> so a broken implementation cannot hang the host,
/// and every fake payload is tiny/synthetic (no real curl, request or account data).
/// </summary>
public class SystemCurlProcessTests
{
    // ── success / classification ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FetchReturnsTheBoundedBodyOnExitZeroWithAValidSuffix()
    {
        var body = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var host = new FakeCurlProcessHost(new MemoryStream(), new MemoryStream(WithSuffix(body, "200")), new MemoryStream());
        host.CompleteExit(0);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.True(result.TransportOk);
        Assert.Equal(200, result.Status);
        Assert.Equal(body, result.Body);
        Assert.Null(result.Error);
        Assert.Equal(0, host.KillCount);
        Assert.True(host.DisposeCount >= 1);
    }

    [Fact]
    public async Task FetchFailsWhenCurlExitsNonZeroEvenWithAPlausible200Tail()
    {
        var body = Encoding.UTF8.GetBytes("{\"ok\":true}");
        var host = new FakeCurlProcessHost(new MemoryStream(), new MemoryStream(WithSuffix(body, "200")), new MemoryStream());
        host.CompleteExit(7);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.False(result.TransportOk);
        Assert.NotNull(result.Error);
        // A non-zero exit with plausible partial output is a failure, and the body text is not leaked.
        Assert.DoesNotContain("ok", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchFailsOnAMissingStatusSuffix()
    {
        var host = new FakeCurlProcessHost(
            new MemoryStream(),
            new MemoryStream(Encoding.UTF8.GetBytes("network broke")),
            new MemoryStream());
        host.CompleteExit(0);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.False(result.TransportOk);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task FetchFailsWhenParsedBodyExceedsTheCapEvenUnderTheRawCap()
    {
        // One byte over the *body* cap, with a valid suffix, stays under the raw stdout cap — the
        // separate parsed-body check must reject it.
        var raw = WithSuffix(new byte[SubscriptionService.MaxBytes + 1], "200");
        Assert.True(raw.Length < SubscriptionService.MaxCurlStdoutBytes);
        var host = new FakeCurlProcessHost(new MemoryStream(), new MemoryStream(raw), new MemoryStream());
        host.CompleteExit(0);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.False(result.TransportOk);
    }

    // ── oversize / budget: stop immediately, not at the total budget ───────────────────────

    [Fact]
    public async Task FetchStopsImmediatelyOnStdoutOverflowWhileExitIsPending()
    {
        var host = new FakeCurlProcessHost(
            new MemoryStream(),
            new MemoryStream(new byte[SubscriptionService.MaxCurlStdoutBytes + 1]),
            new MemoryStream());
        // Exit never completes: a broken transport would wait out the whole 18s budget.
        var stopwatch = Stopwatch.StartNew();

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));
        stopwatch.Stop();

        Assert.False(result.TransportOk);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"returned after {stopwatch.Elapsed}");
        Assert.Equal(1, host.KillCount);
    }

    [Fact]
    public async Task FetchKillsTheOwnedChildWhenTheBudgetExpires()
    {
        var host = new FakeCurlProcessHost(new MemoryStream(), new BlockingReadStream(), new BlockingReadStream());

        var result = await WithTimeout(
            Fetch(_ => host, budget: TimeSpan.FromMilliseconds(150)),
            TimeSpan.FromSeconds(3));

        Assert.False(result.TransportOk);
        Assert.Equal(1, host.KillCount);
        Assert.True(host.DisposeCount >= 1);
    }

    // ── post-start host faults: fixed failure, kill once, observe, always dispose ───────────

    [Fact]
    public async Task FetchFailsFixedWhenTheHostStandardOutputGetterThrowsAfterStart()
    {
        var host = new FaultingCurlProcessHost(
            new MemoryStream(),
            new MemoryStream(),
            new MemoryStream(),
            throwOutput: true);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.False(result.TransportOk);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("SENTINEL", result.Error!, StringComparison.Ordinal);
        Assert.Equal(1, host.KillCount);
        Assert.True(host.DisposeCount >= 1);
    }

    [Fact]
    public async Task FetchFailsFixedAndObservesPendingReadsWhenWaitForExitAsyncThrowsAfterStart()
    {
        // stdout/stderr/stdin are already opened and a stderr read is still pending when the
        // exit-wait throws: the pending read must be cancelled/observed, never leaked.
        var host = new FaultingCurlProcessHost(
            new MemoryStream(),
            new MemoryStream(WithSuffix(Encoding.UTF8.GetBytes("{}"), "200")),
            new BlockingReadStream(),
            throwExitWait: true);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.False(result.TransportOk);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("SENTINEL", result.Error!, StringComparison.Ordinal);
        Assert.Equal(1, host.KillCount);
        Assert.True(host.DisposeCount >= 1);
    }

    [Fact]
    public async Task FetchFailsFixedWhenTheExitCodeGetterThrows()
    {
        var host = new FaultingCurlProcessHost(
            new MemoryStream(),
            new MemoryStream(WithSuffix(Encoding.UTF8.GetBytes("{}"), "200")),
            new MemoryStream(),
            throwExitCode: true);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.False(result.TransportOk);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("SENTINEL", result.Error!, StringComparison.Ordinal);
        Assert.Equal(1, host.KillCount);
        Assert.True(host.DisposeCount >= 1);
    }

    // ── concurrency: both pipes drained, stdin written before awaiting stdout EOF ──────────

    [Fact]
    public async Task FetchReadsStdoutAndStderrConcurrently()
    {
        // Each stream refuses to emit anything until its peer has been read at least once. A
        // sequential drain deadlocks; a concurrent one completes.
        var (stdout, stderr) = RendezvousStream.CreatePair(
            WithSuffix(Encoding.UTF8.GetBytes("{}"), "200"),
            Encoding.UTF8.GetBytes("chatter"));
        var host = new FakeCurlProcessHost(new MemoryStream(), stdout, stderr);
        host.CompleteExit(0);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.True(result.TransportOk);
        Assert.Equal(200, result.Status);
    }

    [Fact]
    public async Task FetchWritesStdinBeforeAwaitingStdoutEof()
    {
        // curl only produces its body once it has read the config on stdin. If the transport drains
        // stdout to EOF before writing stdin, this never completes.
        var stdin = new SignalOnWriteStream();
        var host = new FakeCurlProcessHost(
            stdin,
            new GatedReadStream(WithSuffix(Encoding.UTF8.GetBytes("{}"), "200"), stdin.Written),
            new MemoryStream());
        host.CompleteExit(0);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.True(result.TransportOk);
    }

    [Fact]
    public async Task FetchObservesAStdinWriteFailureWithoutLeakingOrHanging()
    {
        var host = new FakeCurlProcessHost(
            new ThrowingWriteStream(new IOException("stdin refused SENTINEL")),
            new MemoryStream(WithSuffix(Encoding.UTF8.GetBytes("{}"), "200")),
            new MemoryStream());
        host.CompleteExit(0);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.DoesNotContain("SENTINEL", result.Error ?? string.Empty, StringComparison.Ordinal);
        Assert.True(host.DisposeCount >= 1);
    }

    [Fact]
    public async Task FetchFailsWhenStdinCloseThrowsEvenWithAValid200AndExitZero()
    {
        // Body, status and exit are all plausible, but the config was never confirmed closed. That is
        // not a successful request: it must surface as a fixed failure and still clean up the child.
        var host = new FakeCurlProcessHost(
            new CloseThrowingStream(),
            new MemoryStream(WithSuffix(Encoding.UTF8.GetBytes("{}"), "200")),
            new MemoryStream());
        host.CompleteExit(0);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.False(result.TransportOk);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("SENTINEL", result.Error!, StringComparison.Ordinal);
        Assert.Equal(1, host.KillCount);
        Assert.True(host.DisposeCount >= 1);
    }

    // ── stderr never exposed ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task FetchNeverExposesStderrVerbatim()
    {
        var host = new FakeCurlProcessHost(
            new MemoryStream(),
            new MemoryStream(),
            new MemoryStream(Encoding.UTF8.GetBytes("SSTDERR-SECRET-TOKEN")));
        host.CompleteExit(0);

        var result = await WithTimeout(Fetch(_ => host), TimeSpan.FromSeconds(3));

        Assert.False(result.TransportOk);
        Assert.DoesNotContain("SSTDERR-SECRET-TOKEN", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    // ── cancellation: only the owned child, nothing else ──────────────────────────────────

    [Fact]
    public async Task FetchPropagatesCancellationAndKillsOnlyTheOwnedChild()
    {
        var host = new FakeCurlProcessHost(new MemoryStream(), new BlockingReadStream(), new BlockingReadStream());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new CurlProcessFactory(_ => { started.TrySetResult(); return host; });

        using var cts = new CancellationTokenSource();
        var task = new SystemCurlTransport(factory).FetchAsync(
            "/fake/curl", SubscriptionService.AccountsCheckUri, null, "header = \"x: y\"\n", null, cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithTimeout(task, TimeSpan.FromSeconds(3)));

        Assert.Equal(1, host.KillCount);
        Assert.True(host.DisposeCount >= 1);
    }

    [Fact]
    public async Task FetchStartsNothingWhenAlreadyCancelled()
    {
        var starts = 0;
        var factory = new CurlProcessFactory(_ =>
        {
            starts++;
            return new FakeCurlProcessHost(new MemoryStream(), new MemoryStream(), new MemoryStream());
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var transport = new SystemCurlTransport(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.FetchAsync(
            "/fake/curl", SubscriptionService.AccountsCheckUri, null, "cfg", null, cts.Token));

        Assert.Equal(0, starts);
    }

    // ── helpers & fakes ───────────────────────────────────────────────────────────────────

    private static Task<CurlFetchResult> Fetch(
        CurlProcessFactory factory,
        string stdinConfig = "header = \"x: y\"\n",
        TimeSpan? budget = null)
        => new SystemCurlTransport(factory).FetchAsync(
            "/fake/curl", SubscriptionService.AccountsCheckUri, null, stdinConfig, budget, CancellationToken.None);

    private static byte[] WithSuffix(byte[] body, string status)
    {
        var suffix = Encoding.ASCII.GetBytes("\n" + SubscriptionService.CurlStatusMarker + status + "___");
        var raw = new byte[body.Length + suffix.Length];
        body.CopyTo(raw, 0);
        suffix.CopyTo(raw, body.Length);
        return raw;
    }

    /// <summary>Bounds every await so a broken implementation can never hang the test host.</summary>
    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        Assert.True(ReferenceEquals(completed, task), "the curl transport did not return within the test budget");
        return await task;
    }

    private sealed class FakeCurlProcessHost : ICurlProcessHost
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _exitCode;

        public FakeCurlProcessHost(Stream stdin, Stream stdout, Stream stderr)
        {
            StandardInput = stdin;
            StandardOutput = stdout;
            StandardError = stderr;
        }

        public Stream StandardInput { get; }

        public Stream StandardOutput { get; }

        public Stream StandardError { get; }

        public int ExitCode => _exitCode;

        public int KillCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public void CompleteExit(int code)
        {
            _exitCode = code;
            _exit.TrySetResult();
        }

        public void KillOwned()
        {
            KillCount++;
            _exitCode = -1;
            _exit.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SignalOnWriteStream : Stream
    {
        private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Written => _written.Task;

        public override void Write(byte[] buffer, int offset, int count) => _written.TrySetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _written.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ThrowingWriteStream : Stream
    {
        private readonly IOException _error;

        public ThrowingWriteStream(IOException error) => _error = error;

        public override void Write(byte[] buffer, int offset, int count) => throw _error;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(_error);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class GatedReadStream : Stream
    {
        private readonly byte[] _payload;
        private readonly Task _gate;
        private int _offset;

        public GatedReadStream(byte[] payload, Task gate)
        {
            _payload = payload;
            _gate = gate;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Copy(buffer.Span);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _gate.GetAwaiter().GetResult();
            return Copy(buffer.AsSpan(offset, count));
        }

        private int Copy(Span<byte> destination)
        {
            if (_offset >= _payload.Length) { return 0; }
            var n = Math.Min(destination.Length, _payload.Length - _offset);
            _payload.AsSpan(_offset, n).CopyTo(destination);
            _offset += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A host that faults on a selected member so post-start failures are observable.</summary>
    private sealed class FaultingCurlProcessHost : ICurlProcessHost
    {
        private readonly Stream _stdin;
        private readonly Stream _stdout;
        private readonly Stream _stderr;
        private readonly bool _throwOutput;
        private readonly bool _throwExitWait;
        private readonly bool _throwExitCode;

        public FaultingCurlProcessHost(
            Stream stdin,
            Stream stdout,
            Stream stderr,
            bool throwOutput = false,
            bool throwExitWait = false,
            bool throwExitCode = false)
        {
            _stdin = stdin;
            _stdout = stdout;
            _stderr = stderr;
            _throwOutput = throwOutput;
            _throwExitWait = throwExitWait;
            _throwExitCode = throwExitCode;
        }

        public Stream StandardInput => _stdin;

        public Stream StandardOutput => _throwOutput ? throw new InvalidOperationException("stdout getter SENTINEL") : _stdout;

        public Stream StandardError => _stderr;

        public int ExitCode => _throwExitCode ? throw new InvalidOperationException("exitcode getter SENTINEL") : 0;

        public int KillCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _throwExitWait ? throw new InvalidOperationException("exit-wait SENTINEL") : Task.CompletedTask;

        public void KillOwned() => KillCount++;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Accepts writes but throws when closed — the config was never confirmed sealed.</summary>
    private sealed class CloseThrowingStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.WriteAsync(buffer, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override void Dispose(bool disposing) => throw new IOException("stdin close SENTINEL");

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// A pair of streams that each withhold their first byte until the peer's first read — the
    /// minimal deterministic proof that both pipes are drained concurrently.
    /// </summary>
    private sealed class RendezvousStream : Stream
    {
        private readonly byte[] _payload;
        private readonly TaskCompletionSource _mineProduced;
        private readonly TaskCompletionSource _peerProduced;
        private int _offset;
        private bool _advertised;

        // Both TCS instances are injected so that the one each stream *signals* is the exact same
        // object its peer *waits on* — otherwise the two waits never rendezvous.
        private RendezvousStream(byte[] payload, TaskCompletionSource mineProduced, TaskCompletionSource peerProduced)
        {
            _payload = payload;
            _mineProduced = mineProduced;
            _peerProduced = peerProduced;
        }

        public static (RendezvousStream Out, RendezvousStream Err) CreatePair(byte[] outPayload, byte[] errPayload)
        {
            var outProduced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var errProduced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return (
                new RendezvousStream(outPayload, outProduced, errProduced),
                new RendezvousStream(errPayload, errProduced, outProduced));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_advertised)
            {
                _advertised = true;
                _mineProduced.TrySetResult();
                await _peerProduced.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_offset >= _payload.Length) { return 0; }
            var n = Math.Min(buffer.Length, _payload.Length - _offset);
            _payload.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
