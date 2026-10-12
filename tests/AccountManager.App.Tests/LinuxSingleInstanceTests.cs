using AccountManager.App.Services;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Xunit;

namespace AccountManager.App.Tests;

/// <summary>
/// Linux-only readiness and in-process activation checks. These run against a real Unix-domain
/// socket and lock in a throwaway runtime directory; on Windows they are reported as skipped, never
/// fake-passed. Cross-process lock exclusion, abnormal-exit release and different-user isolation are
/// deliberately not claimed here — Task 10's real harness must prove them.
/// </summary>
public class LinuxSingleInstanceTests
{
    private static string NewRuntimeDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "am-single-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Throwaway runtime dirs default to 0755 under the test umask; production requires a private
        // 0700 owned directory, and the fixture must satisfy that rather than relaxing the check.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return dir;
    }

    private static void DeleteQuietly(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ReadinessIsFalseUntilListenBindsAndDispatches()
    {
        var dir = NewRuntimeDir();
        try
        {
            using var owner = new LinuxSingleInstance("am-p7", dir, a => a());
            Assert.True(owner.IsFirstInstance);
            Assert.False(owner.CanActivate);
            Assert.False(owner.ActivationFailed);

            owner.Listen(() => { });

            Assert.True(owner.CanActivate);
        }
        finally { DeleteQuietly(dir); }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ExactShowFromTheSameUserInvokesTheCallback()
    {
        var dir = NewRuntimeDir();
        try
        {
            var calls = 0;
            using var owner = new LinuxSingleInstance("am-p7", dir, a => a());
            owner.Listen(() => Interlocked.Increment(ref calls));

            using var second = new LinuxSingleInstance("am-p7", dir, a => a());
            Assert.False(second.IsFirstInstance);
            Assert.False(second.CanActivate);

            Assert.True(second.TryActivatePrimary());
            Assert.False(second.ActivationFailed);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 1, TimeSpan.FromSeconds(2)));
            Assert.Equal(1, calls);
        }
        finally { DeleteQuietly(dir); }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ListeningTwiceKeepsOneChannelAndStillDeliversOnce()
    {
        var dir = NewRuntimeDir();
        try
        {
            var calls = 0;
            using var owner = new LinuxSingleInstance("am-p7", dir, a => a());
            owner.Listen(() => Interlocked.Increment(ref calls));
            owner.Listen(() => Interlocked.Increment(ref calls)); // idempotent, no competing listener

            Assert.True(owner.CanActivate);

            using var second = new LinuxSingleInstance("am-p7", dir, a => a());
            Assert.True(second.TryActivatePrimary());

            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 1, TimeSpan.FromSeconds(2)));
            Assert.Equal(1, calls);
        }
        finally { DeleteQuietly(dir); }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ActivationAgainstANonListeningOwnerFailsExplicitlyNotSilently()
    {
        var dir = NewRuntimeDir();
        try
        {
            // The owner holds the lock but never started its socket: a second launch must report the
            // failure instead of pretending activation succeeded.
            using var owner = new LinuxSingleInstance("am-p7", dir, a => a());
            using var second = new LinuxSingleInstance("am-p7", dir, a => a(), TimeSpan.FromMilliseconds(200));

            Assert.False(second.IsFirstInstance);
            Assert.False(second.TryActivatePrimary());
            Assert.True(second.ActivationFailed);
        }
        finally { DeleteQuietly(dir); }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void DisposeReleasesOwnershipSoANewInstanceCanStart()
    {
        var dir = NewRuntimeDir();
        try
        {
            var owner = new LinuxSingleInstance("am-p7", dir, a => a());
            owner.Listen(() => { });
            owner.Dispose();

            using var next = new LinuxSingleInstance("am-p7", dir, a => a());
            Assert.True(next.IsFirstInstance);
        }
        finally { DeleteQuietly(dir); }
    }

    // ── Future native checks for review findings #3. These use only existing API and are LinuxFact, so
    //    they are skipped on Windows; they must not be run/claimed as native proof in this round. ────

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ASymlinkedRuntimeDirectoryIsRefused()
    {
        var target = NewRuntimeDir();
        var link = Path.Combine(Path.GetTempPath(), "am-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateSymbolicLink(link, target);

            Assert.Throws<IOException>(() => LinuxSingleInstance.ResolveRuntimeDirectory(link));
        }
        finally
        {
            try { File.Delete(link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            DeleteQuietly(target);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void AnUnknownFileAtTheSocketEndpointIsNotUnlinkedAndListenFailsClosed()
    {
        var dir = NewRuntimeDir();
        try
        {
            var path = LinuxSingleInstance.ComputeSocketPath("am-p7", dir);
            File.WriteAllText(path, "not a socket");

            using var instance = new LinuxSingleInstance("am-p7", dir, a => a());
            Assert.True(instance.IsFirstInstance);

            instance.Listen(() => { });

            Assert.False(instance.CanActivate);
            Assert.True(File.Exists(path));
            Assert.Equal("not a socket", File.ReadAllText(path));
        }
        finally { DeleteQuietly(dir); }
    }

    // ── Contract: LinuxSingleInstance.ReceiveCompleteAsync(stream, cap, remaining, ct). The owned
    //    socket path uses this for complete EOF framing under one total budget. These are controlled
    //    Stream oracles (no socket/process/dbus): a truncated read, a per-read timeout or a missing
    //    EOF must fail them; only a complete EOF at/below cap may return. ────────────────────────────

    [Fact]
    public async Task ReceiveCompleteReturnsThePayloadOnlyAfterACompleteEof()
    {
        var payload = Encoding.ASCII.GetBytes("shown");
        using var stream = new MemoryStream(payload);

        var result = await LinuxSingleInstance.ReceiveCompleteAsync(
            stream, 16, TimeSpan.FromSeconds(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task ReceiveCompleteRejectsOverflowBeyondTheCap()
    {
        using var stream = new MemoryStream(new byte[17]);

        await Assert.ThrowsAsync<InvalidDataException>(() => LinuxSingleInstance.ReceiveCompleteAsync(
            stream, 16, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task ReceiveCompleteDoesNotAcceptShownWithoutAnEof()
    {
        // A payload that delivers "shown" but never reaches EOF must not be returned as a success.
        using var stream = new NeverEndingStream(Encoding.ASCII.GetBytes("shown"));

        await AssertFailsWithinAsync(
            () => LinuxSingleInstance.ReceiveCompleteAsync(stream, 16, TimeSpan.FromMilliseconds(150), CancellationToken.None),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReceiveCompleteUsesOneTotalBudgetAcrossSlowChunks()
    {
        // Five 1-byte chunks at 60 ms = 300 ms, past a 150 ms total budget. A per-read timeout would
        // wrongly accept every 60 ms read and return a partial-success payload.
        using var stream = new SlowChunkStream(Encoding.ASCII.GetBytes("shown"), TimeSpan.FromMilliseconds(60));

        await AssertFailsWithinAsync(
            () => LinuxSingleInstance.ReceiveCompleteAsync(stream, 16, TimeSpan.FromMilliseconds(150), CancellationToken.None),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReceiveCompleteSucceedsForSlowChunksWithinTheTotalBudget()
    {
        using var stream = new SlowChunkStream(Encoding.ASCII.GetBytes("show"), TimeSpan.FromMilliseconds(30));

        var result = await LinuxSingleInstance.ReceiveCompleteAsync(
            stream, 16, TimeSpan.FromSeconds(2), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(Encoding.ASCII.GetBytes("show"), result);
    }

    [Fact]
    public async Task ReceiveCompleteIsBoundedWhenTheStreamIgnoresCancellation()
    {
        // The stream never ends and ignores the token; the method must still return within its own
        // deadline/cancellation instead of blocking on the stream.
        using var stream = new NeverEndingStream(Encoding.ASCII.GetBytes("sh"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await AssertFailsWithinAsync(
            () => LinuxSingleInstance.ReceiveCompleteAsync(stream, 16, TimeSpan.FromSeconds(10), cts.Token),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReceiveCompleteRejectsAnEofThatCancelsTheCallerFirst()
    {
        // Completed-task cancellation race: the stream synchronously cancels the caller and then returns
        // a completed EOF, so WaitAsync returns that result. The method must re-check cancellation before
        // accepting the EOF and throw; a valid prefix + immediate EOF (ReceiveCompleteReturnsThePayloadOnlyAfterACompleteEof)
        // remains a success.
        using var cts = new CancellationTokenSource();
        using var stream = new CancellingEofStream(Encoding.ASCII.GetBytes("sh"), cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LinuxSingleInstance.ReceiveCompleteAsync(
            stream, 16, TimeSpan.FromSeconds(5), cts.Token));
    }

    [Fact]
    public async Task ServerActivationDispatchesOnlyExactShowFromTheSameUser()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("show"));
        var calls = 0;

        var dispatched = await LinuxSingleInstance.DispatchServerActivationAsync(
            stream,
            SingleInstanceProtocol.MaxMessageBytes,
            1000,
            1000,
            () => TimeSpan.FromSeconds(1),
            () => true,
            () => calls++,
            CancellationToken.None);

        Assert.True(dispatched);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("show!", 1000, 1000)]
    [InlineData("show", 1000, 1001)]
    public async Task ServerActivationRejectsWrongMessageOrUserWithoutDispatch(string payload, long serverUid, long peerUid)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(payload));
        var calls = 0;

        var dispatched = await LinuxSingleInstance.DispatchServerActivationAsync(
            stream,
            SingleInstanceProtocol.MaxMessageBytes,
            serverUid,
            peerUid,
            () => TimeSpan.FromSeconds(1),
            () => true,
            () => calls++,
            CancellationToken.None);

        Assert.False(dispatched);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ServerActivationDoesNotDispatchWhenTheSharedDeadlineExpiresAtEof()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("show"));
        var calls = 0;
        var remainingCalls = 0;

        var dispatched = await LinuxSingleInstance.DispatchServerActivationAsync(
            stream,
            SingleInstanceProtocol.MaxMessageBytes,
            1000,
            1000,
            () => Interlocked.Increment(ref remainingCalls) == 1
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.Zero,
            () => true,
            () => calls++,
            CancellationToken.None);

        Assert.False(dispatched);
        Assert.Equal(0, calls);
    }

    // ── Contract: LinuxSingleInstance.ComputeLockPath(name, directory) + native-only future checks.
    //    LinuxFact on Windows, so UNRUN here; they use only the constructor's own path computation and
    //    a private throwaway directory. ──────────────────────────────────────────────────────────────

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ASymlinkedLockTargetIsRefusedAndUntouched()
    {
        var dir = NewRuntimeDir();
        try
        {
            var lockPath = LinuxSingleInstance.ComputeLockPath("am-p7", dir);
            var victim = Path.Combine(dir, "victim.dat");
            File.WriteAllText(victim, "secret");
            var before = ModeOf(victim);
            File.CreateSymbolicLink(lockPath, victim);

            using var instance = new LinuxSingleInstance("am-p7", dir, a => a());

            // The no-follow open refuses the symlinked lock (it never opens/chmods the link target).
            Assert.False(instance.IsFirstInstance);
            Assert.False(instance.LockContended);
            Assert.Equal("secret", File.ReadAllText(victim));
            Assert.Equal(before, ModeOf(victim));
        }
        finally { DeleteQuietly(dir); }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ASymlinkedRuntimeDirectoryWithATrailingSlashIsRefused()
    {
        var target = NewRuntimeDir();
        var link = Path.Combine(Path.GetTempPath(), "am-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateSymbolicLink(link, target);

            // Trailing separators may be normalized, but the link component must still be detected
            // (the check must not erase the symlink before failing closed).
            Assert.Throws<IOException>(
                () => LinuxSingleInstance.ResolveRuntimeDirectory(link + Path.DirectorySeparatorChar));
        }
        finally
        {
            try { File.Delete(link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            DeleteQuietly(target);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ARuntimeDirectoryWithALinkedAncestorIsRefused()
    {
        var real = NewRuntimeDir();
        var linkParent = Path.Combine(Path.GetTempPath(), "am-parent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var child = Path.Combine(real, "child");
            Directory.CreateDirectory(child);
            SetPrivateMode(child);
            Directory.CreateSymbolicLink(linkParent, real);

            var alias = Path.Combine(linkParent, "child");
            Assert.Throws<IOException>(() => LinuxSingleInstance.ResolveRuntimeDirectory(alias));
        }
        finally
        {
            try { File.Delete(linkParent); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            DeleteQuietly(real);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void AnUnknownSameUserBoundSocketAtTheEndpointIsNotRemoved()
    {
        var dir = NewRuntimeDir();
        Socket? endpoint = null;
        try
        {
            var path = LinuxSingleInstance.ComputeSocketPath("am-p7", dir);
            endpoint = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            endpoint.Bind(new UnixDomainSocketEndPoint(path));
            endpoint.Listen(1);

            using var instance = new LinuxSingleInstance("am-p7", dir, a => a());
            Assert.True(instance.IsFirstInstance);

            instance.Listen(() => { });

            // An unrelated same-user endpoint is not evidence of our stale socket: it must never be
            // unlinked, so listening fails closed instead of stealing the name.
            Assert.False(instance.CanActivate);
            Assert.True(File.Exists(path));
        }
        finally
        {
            try { endpoint?.Dispose(); } catch { }
            DeleteQuietly(dir);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ThePersistentLockFileIsNeverUnlinkedAndStaysPrivate()
    {
        var dir = NewRuntimeDir();
        try
        {
            var lockPath = LinuxSingleInstance.ComputeLockPath("am-p7", dir);
            var owner = new LinuxSingleInstance("am-p7", dir, a => a());
            owner.Listen(() => { });
            owner.Dispose();

            // Ownership uses a persistent 0600 lock file that outlives the process, never unlinked.
            Assert.True(File.Exists(lockPath));
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(lockPath));
            }
        }
        finally { DeleteQuietly(dir); }
    }

    // ── Native follow-up RED for review finding #3 (current lock path / safe ancestors). LinuxFact, so
    //    skipped on Windows; they exercise only existing API in a unique private temp dir. ───────────

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void AReplacedLockPathRefusesTheBindInsteadOfListeningUnderANameItDoesNotOwn()
    {
        var dir = NewRuntimeDir();
        try
        {
            var lockPath = LinuxSingleInstance.ComputeLockPath("am-p7", dir);
            using var instance = new LinuxSingleInstance("am-p7", dir, a => a());
            Assert.True(instance.IsFirstInstance);

            // Swap the persistent lock pathname for a different inode while the owner still holds the old
            // descriptor: the recheck immediately before bind must verify the current path, not just the
            // descriptor it already holds, and refuse rather than bind under a name it no longer owns.
            File.Delete(lockPath);
            File.WriteAllText(lockPath, "replaced");

            instance.Listen(() => { });

            Assert.False(instance.CanActivate);
        }
        finally { DeleteQuietly(dir); }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void ARemovedSocketEndpointClearsReadiness()
    {
        var dir = NewRuntimeDir();
        try
        {
            var socketPath = LinuxSingleInstance.ComputeSocketPath("am-p7", dir);
            using var instance = new LinuxSingleInstance("am-p7", dir, a => a());
            instance.Listen(() => { });
            Assert.True(instance.CanActivate);

            // Readiness must not stay true for an endpoint that no longer exists on disk, or a later
            // caller would be told activation is possible into a name that cannot be reached.
            File.Delete(socketPath);

            Assert.False(instance.CanActivate);
        }
        finally { DeleteQuietly(dir); }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void AWorldWritableNonStickyAncestorIsRefused()
    {
        var baseDir = NewRuntimeDir();
        try
        {
            var shared = Path.Combine(baseDir, "shared");
            var leaf = Path.Combine(shared, "leaf");
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(leaf);
            // World-writable and NOT sticky: any local user could swap a component under us, so the path
            // is not a trusted per-user location even though the leaf itself is ours and 0700. A
            // root-owned sticky ancestor such as /tmp stays legal and is exercised by the other fixtures.
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    shared,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                File.SetUnixFileMode(
                    leaf,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Assert.Throws<IOException>(() => LinuxSingleInstance.ResolveRuntimeDirectory(leaf));
        }
        finally { DeleteQuietly(baseDir); }
    }

    // ── Endpoint ownership record: the bounded positive proof that lets a later lock owner recognise
    //    a known-stale endpoint without unlinking an unknown same-user socket. Pure and in-memory. ───

    [Fact]
    public void TheEndpointRecordRoundTripsASocketIdentity()
    {
        var info = new LinuxStatInfo(1000, 0xC180, 42, 8, 1);

        var encoded = LinuxInstanceEndpointRecord.Encode(info, 1000);

        Assert.Equal(LinuxInstanceEndpointRecord.Size, encoded.Length);
        Assert.True(LinuxInstanceEndpointRecord.TryDecode(encoded, out var decoded, out var uid));
        Assert.Equal(1000u, uid);
        Assert.True(decoded.SameFile(info));
        Assert.True(decoded.IsSocket);
    }

    [Fact]
    public void AnUnknownOrForeignEndpointRecordIsRejected()
    {
        Assert.False(LinuxInstanceEndpointRecord.TryDecode(ReadOnlySpan<byte>.Empty, out _, out _));
        Assert.False(LinuxInstanceEndpointRecord.TryDecode(new byte[LinuxInstanceEndpointRecord.Size], out _, out _));

        var info = new LinuxStatInfo(1000, 0xC180, 42, 8, 1);
        var encoded = LinuxInstanceEndpointRecord.Encode(info, 1000);
        Assert.True(LinuxInstanceEndpointRecord.TryDecode(encoded, out var decoded, out var recordedUid));

        // A different inode, a foreign owner, or a non-socket endpoint is never our recorded socket.
        Assert.False(LinuxInstanceEndpointRecord.Matches(decoded, recordedUid, new LinuxStatInfo(1000, 0xC180, 99, 8, 1), 1000));
        Assert.False(LinuxInstanceEndpointRecord.Matches(decoded, recordedUid, info, 1001));
        Assert.False(LinuxInstanceEndpointRecord.Matches(decoded, recordedUid, new LinuxStatInfo(1000, 0x8180, 42, 8, 1), 1000));
        Assert.True(LinuxInstanceEndpointRecord.Matches(decoded, recordedUid, info, 1000));
    }

    [Fact]
    public void AnEndpointRecordWithExtraTrailingBytesIsRejected()
    {
        // Exact framing: a valid record plus any trailing byte is not the bounded record and must not
        // authorize stale-endpoint cleanup.
        var info = new LinuxStatInfo(1000, 0xC180, 42, 8, 1);
        var encoded = LinuxInstanceEndpointRecord.Encode(info, 1000);
        var withExtra = new byte[encoded.Length + 1];
        encoded.CopyTo(withExtra, 0);
        withExtra[^1] = 0x7F;

        Assert.False(LinuxInstanceEndpointRecord.TryDecode(withExtra, out _, out _));
    }

    private static async Task AssertFailsWithinAsync(Func<Task> action, TimeSpan bound)
    {
        var task = action();
        var winner = await Task.WhenAny(task, Task.Delay(bound));
        Assert.True(ReferenceEquals(winner, task), "the operation did not fail within its bound");

        // A bounded failure is specifically a timeout/cancellation; a reader bug must not masquerade.
        var exception = await Record.ExceptionAsync(() => task);
        Assert.True(
            exception is TimeoutException or OperationCanceledException,
            $"expected a bounded timeout/cancellation, got {exception?.GetType().Name ?? "success"}");
    }

    private static UnixFileMode ModeOf(string path)
        => OperatingSystem.IsLinux() ? File.GetUnixFileMode(path) : default;

    private static void SetPrivateMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

// Controlled streams for the ReceiveCompleteAsync contract. Neither is real IO and neither contacts a
// socket, process or dbus: they only exercise EOF/budget/cancellation framing.
internal sealed class NeverEndingStream : Stream
{
    private readonly byte[] _prefix;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _offset;

    public NeverEndingStream(byte[] prefix) => _prefix = prefix;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < _prefix.Length)
        {
            var n = Math.Min(buffer.Length, _prefix.Length - _offset);
            _prefix.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        // Deliberately ignore cancellationToken: the caller's own bound must end the wait. Disposal
        // releases this test-only operation so no synthetic abandoned IO is left pending forever.
        await _release.Task.ConfigureAwait(false);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _release.TrySetResult(); }
        base.Dispose(disposing);
    }
}

// Returns a short prefix, then on the next read synchronously cancels the caller and returns a completed
// EOF. This reproduces the completed-task cancellation race where WaitAsync returns an already-finished
// result instead of throwing.
internal sealed class CancellingEofStream : Stream
{
    private readonly byte[] _prefix;
    private readonly CancellationTokenSource _caller;
    private int _offset;

    public CancellingEofStream(byte[] prefix, CancellationTokenSource caller)
    {
        _prefix = prefix;
        _caller = caller;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < _prefix.Length)
        {
            var n = Math.Min(buffer.Length, _prefix.Length - _offset);
            _prefix.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return new ValueTask<int>(n);
        }

        // Final read: cancel the caller synchronously, then return a completed EOF.
        _caller.Cancel();
        return new ValueTask<int>(0);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}

internal sealed class SlowChunkStream : Stream
{
    private readonly byte[] _data;
    private readonly TimeSpan _chunkDelay;
    private int _offset;

    public SlowChunkStream(byte[] data, TimeSpan chunkDelay)
    {
        _data = data;
        _chunkDelay = chunkDelay;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset >= _data.Length)
        {
            return 0;
        }

        await Task.Delay(_chunkDelay, cancellationToken).ConfigureAwait(false);
        var n = Math.Min(1, Math.Min(buffer.Length, _data.Length - _offset));
        _data.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}
