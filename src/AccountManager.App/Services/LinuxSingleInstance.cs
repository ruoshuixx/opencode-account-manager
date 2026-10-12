using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;

namespace AccountManager.App.Services;

/// <summary>
/// The fixed, bounded single-instance handshake. The client sends exactly the four ASCII bytes
/// <c>show</c> and half-closes; the server accepts it only when the peer is the same effective user
/// and the request is complete, dispatches activation, and then replies with the exact positive
/// <c>shown</c>. A closed connection without that exact ack is a failure, never a silent success.
/// </summary>
public static class SingleInstanceProtocol
{
    public const int MaxMessageBytes = 128;
    public const int AckMaxBytes = 16;

    /// <summary>The only accepted request: four ASCII bytes, with no trailing byte.</summary>
    public static ReadOnlySpan<byte> ShowMessage => "show"u8;

    /// <summary>The only positive acknowledgment, sent after acceptance and dispatch succeed.</summary>
    public static ReadOnlySpan<byte> AckMessage => "shown"u8;

    /// <summary>True only for the complete fixed <c>show</c> request.</summary>
    public static bool IsValidActivation(ReadOnlySpan<byte> payload) => payload.SequenceEqual(ShowMessage);

    /// <summary>True only for the exact positive <c>shown</c> ack; EOF/empty/anything else is rejected.</summary>
    public static bool IsValidAck(ReadOnlySpan<byte> payload) => payload.SequenceEqual(AckMessage);

    /// <summary>Both sides must prove the same known effective UID; negative/unknown is refused.</summary>
    public static bool IsSameEffectiveUser(long serverUid, long peerUid)
        => serverUid >= 0 && peerUid >= 0 && serverUid == peerUid;

    /// <summary>Server decision: complete, same-UID <c>show</c> only.</summary>
    public static bool CanAcceptActivation(ReadOnlySpan<byte> payload, long serverUid, long peerUid)
        => IsValidActivation(payload) && IsSameEffectiveUser(serverUid, peerUid);

    /// <summary>Whether a socket path fits <c>sockaddr_un</c> (108 bytes including the trailing NUL).</summary>
    public static bool IsSocketPathWithinLimit(string? path)
        => !string.IsNullOrEmpty(path) && Encoding.UTF8.GetByteCount(path) < 108;
}

/// <summary>The narrow stat fields needed for ownership, type and inode attribution.</summary>
internal readonly record struct LinuxStatInfo(uint Uid, ushort Mode, ulong Ino, uint DeviceMajor, uint DeviceMinor)
{
    public const uint TypeMask = 0xF000;
    public const uint Regular = 0x8000;
    public const uint Directory = 0x4000;
    public const uint Symlink = 0xA000;
    public const uint Socket = 0xC000;
    public const ushort PermissionMask = 0x1FF;
    public const ushort PrivateOwner = 0x1C0; // 0700
    public const ushort PrivateFile = 0x180;  // 0600

    public bool IsRegular => (Mode & TypeMask) == Regular;
    public bool IsDirectory => (Mode & TypeMask) == Directory;
    public bool IsSymlink => (Mode & TypeMask) == Symlink;
    public bool IsSocket => (Mode & TypeMask) == Socket;
    public bool IsPrivateOwner => (Mode & PermissionMask) == PrivateOwner;
    public bool IsPrivateFile => (Mode & PermissionMask) == PrivateFile;

    public bool SameFile(LinuxStatInfo other)
        => Ino == other.Ino && DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor;
}

/// <summary>Outcome of acquiring the persistent exclusive lock; only real contention may try activation.</summary>
internal enum LockAcquireResult
{
    Acquired,
    Contended,
    Error,
}

/// <summary>
/// A bounded ownership record stored inside the persistent (never-unlinked) lock file. After a clean
/// bind the owner records the endpoint's device/inode/uid here, so a later invocation that legitimately
/// holds the same lock can recognise a known-stale endpoint instead of blind-unlinking a same-user
/// socket that belongs to someone else.
/// </summary>
internal static class LinuxInstanceEndpointRecord
{
    public const int Size = 32;

    private static ReadOnlySpan<byte> Magic => "OCIEP001"u8;

    public static byte[] Encode(LinuxStatInfo info, uint uid)
    {
        var buffer = new byte[Size];
        Magic.CopyTo(buffer);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), info.DeviceMajor);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), info.DeviceMinor);
        BitConverter.TryWriteBytes(buffer.AsSpan(16), info.Ino);
        BitConverter.TryWriteBytes(buffer.AsSpan(24), uid);
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out LinuxStatInfo info, out uint uid)
    {
        info = default;
        uid = 0;
        // Exact framing: a truncated or extra-byte record is not the bounded record and must never
        // authorize stale-endpoint cleanup.
        if (data.Length != Size) { return false; }
        if (!data[..8].SequenceEqual(Magic)) { return false; }

        var major = BitConverter.ToUInt32(data[8..12]);
        var minor = BitConverter.ToUInt32(data[12..16]);
        var ino = BitConverter.ToUInt64(data[16..24]);
        uid = BitConverter.ToUInt32(data[24..28]);
        // S_IFSOCK is 0xC000, which fits the ushort mode field; make the bound explicit.
        info = new LinuxStatInfo(uid, (ushort)LinuxStatInfo.Socket, ino, major, minor);
        return true;
    }

    /// <summary>Positive proof: the recorded identity matches the current path for the same known user.</summary>
    public static bool Matches(LinuxStatInfo decoded, uint decodedUid, LinuxStatInfo current, uint euid)
        => decodedUid == euid && current.Uid == euid && current.IsSocket && decoded.SameFile(current);
}

/// <summary>
/// Narrow, guarded libc boundary for the single-instance lock/socket: effective UID, a no-follow open
/// of the persistent lock, flock tied to the open file description, a 256-byte statx result whose
/// returned mask is verified, and SO_PEERCRED at its exact length. Every member fails closed off Linux
/// or when libc is unavailable; no same-user path-substitution race is claimed to be impossible.
/// </summary>
internal static class LinuxInstanceNative
{
    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x7FF;
    // TYPE | MODE | UID | INO: the fields this file actually trusts.
    private const uint StatxNlink = 0x4;
    private const uint StatxRequired = 0x1 | 0x2 | StatxNlink | 0x8 | 0x100;

    private const int OpenReadWrite = 0x2;
    private const int OpenCreate = 0x40;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenNonBlock = 0x800;
    private const uint ModePrivateFile = 0x180; // 0600

    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int Again = 11; // EAGAIN == EWOULDBLOCK on Linux

    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0x00)] public uint Mask;
        [FieldOffset(0x10)] public uint Nlink;
        [FieldOffset(0x14)] public uint Uid;
        [FieldOffset(0x1C)] public ushort Mode;
        [FieldOffset(0x20)] public ulong Ino;
        [FieldOffset(0x88)] public uint DeviceMajor;
        [FieldOffset(0x8C)] public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Ucred
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);

    [DllImport("libc", SetLastError = true, EntryPoint = "statx")]
    private static extern int statx(int dirfd, string pathname, int flags, uint mask, out StatxBuffer buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int sockfd, int level, int optname, out Ucred optionValue, ref uint optionLength);

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int open(string pathname, int flags, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(int fd, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    internal static int CurrentEffectiveUid()
    {
        if (!OperatingSystem.IsLinux()) { return -1; }
        try { return unchecked((int)geteuid()); }
        catch { return -1; }
    }

    internal static bool TryStat(string path, out LinuxStatInfo info)
    {
        info = default;
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path)) { return false; }
        try
        {
            if (statx(AtFdcwd, path, AtSymlinkNofollow, StatxBasicStats, out var buffer) != 0) { return false; }
            // Never treat an absent field as proof: the kernel must have filled the mask bits we trust.
            if ((buffer.Mask & StatxRequired) != StatxRequired) { return false; }
            info = ToInfo(buffer);
            return true;
        }
        catch { return false; }
    }

    internal static bool TryStatFd(int fd, out LinuxStatInfo info, out uint linkCount)
    {
        info = default;
        linkCount = 0;
        if (!OperatingSystem.IsLinux()) { return false; }
        try
        {
            if (statx(fd, string.Empty, AtEmptyPath, StatxBasicStats, out var buffer) != 0) { return false; }
            if ((buffer.Mask & StatxRequired) != StatxRequired) { return false; }
            info = ToInfo(buffer);
            linkCount = buffer.Nlink;
            return true;
        }
        catch { return false; }
    }

    internal static bool TryGetPeerUid(Socket socket, out long uid)
    {
        uid = -1;
        if (!OperatingSystem.IsLinux()) { return false; }
        try
        {
            // Socket.SafeHandle is the SafeSocketHandle whose reference must be held across the raw
            // fd call.
            var handle = socket.SafeHandle;
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                var fd = (int)handle.DangerousGetHandle();

                var expected = (uint)Marshal.SizeOf<Ucred>();
                var length = expected;
                if (getsockopt(fd, SolSocket, SoPeerCred, out var cred, ref length) != 0) { return false; }

                // The kernel must have reported exactly a full ucred before any field is trusted.
                if (length != expected) { return false; }

                uid = cred.Uid;
                return true;
            }
            finally
            {
                if (added) { handle.DangerousRelease(); }
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Opens the persistent lock without following links, takes flock on the descriptor, validates
    /// type/UID/private mode on that descriptor and confirms the current path still names the same
    /// inode. Only a genuine EAGAIN/EWOULDBLOCK is reported as contention; everything else is an error.
    /// </summary>
    internal static LockAcquireResult TryAcquireExclusiveLock(string lockPath, out FileStream? stream, out LinuxStatInfo lockInfo)
    {
        stream = null;
        lockInfo = default;
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(lockPath)) { return LockAcquireResult.Error; }

        var fd = -1;
        try
        {
            // O_NOFOLLOW makes a symlinked lock target fail with ELOOP; never open/chmod a link target.
            fd = open(lockPath, OpenReadWrite | OpenCreate | OpenNoFollow | OpenCloseOnExec | OpenNonBlock, ModePrivateFile);
            if (fd < 0) { return LockAcquireResult.Error; }

            if (flock(fd, LockExclusive | LockNonBlocking) != 0)
            {
                var err = Marshal.GetLastPInvokeError();
                return err == Again ? LockAcquireResult.Contended : LockAcquireResult.Error;
            }

            // Reject a known nonregular or hardlinked target before any chmod: a hardlink is an
            // unconfirmed unrelated inode and must not be modified. NLINK is read only after its statx
            // mask bit is confirmed, and a single-link owner file is required.
            if (!TryStatFd(fd, out var info, out var linkCount) || !info.IsRegular || linkCount != 1)
            {
                return LockAcquireResult.Error;
            }

            var euid = CurrentEffectiveUid();
            if (euid < 0 || info.Uid != (uint)euid) { return LockAcquireResult.Error; }

            if (!info.IsPrivateFile)
            {
                // Enforce 0600 on the descriptor only; a failure is never swallowed.
                if (fchmod(fd, ModePrivateFile) != 0) { return LockAcquireResult.Error; }
                if (!TryStatFd(fd, out info, out linkCount) || !info.IsPrivateFile || linkCount != 1)
                {
                    return LockAcquireResult.Error;
                }
            }

            // The current path must still name the descriptor we locked.
            if (!TryStat(lockPath, out var pathInfo) || !pathInfo.IsRegular || !pathInfo.SameFile(info))
            {
                return LockAcquireResult.Error;
            }

            // Transfer the raw fd to exactly one owner. If the FileStream constructor fails, dispose the
            // SafeFileHandle we created rather than also raw-closing an fd a managed handle owns.
            var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            fd = -1; // the handle now owns the descriptor; the finally must not raw-close it
            try
            {
                stream = new FileStream(handle, FileAccess.ReadWrite);
            }
            catch
            {
                try { handle.Dispose(); } catch { }
                return LockAcquireResult.Error;
            }
            lockInfo = info;
            return LockAcquireResult.Acquired;
        }
        catch
        {
            return LockAcquireResult.Error;
        }
        finally
        {
            if (fd >= 0) { try { close(fd); } catch { } }
        }
    }

    private static LinuxStatInfo ToInfo(StatxBuffer buffer)
        => new(buffer.Uid, buffer.Mode, buffer.Ino, buffer.DeviceMajor, buffer.DeviceMinor);
}

/// <summary>
/// Linux recovery channel: a persistent exclusive lock (flock on the open file description) picks one
/// owner independent of executable version, and a per-user Unix socket carries the fixed handshake.
/// The lock inode is never unlinked. The socket is removed only while its current inode, type and UID
/// match the endpoint this invocation bound (or a positively-recorded stale endpoint under the lock),
/// so an unknown path is never blindly unlinked.
/// </summary>
public sealed class LinuxSingleInstance : IDisposable
{
    private const int Backlog = 8;
    private const int RetryDelayMs = 25;
    private static readonly TimeSpan DefaultActivationBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ServerConnectionBudget = TimeSpan.FromMilliseconds(500);

    private readonly Action<Action> _dispatch;
    private readonly TimeSpan _activationBudget;
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _socketPath;
    private readonly string? _lockPath;
    private readonly string? _directory;

    private FileStream? _lockStream;
    private Socket? _socket;
    private Thread? _acceptThread;
    private Action? _onActivate;
    private LinuxStatInfo _lockedDirectory;
    private LinuxStatInfo _lockedLock;
    private LinuxStatInfo? _ownedSocket;
    private volatile Socket? _activeConnection;
    private volatile bool _ready;
    private bool _listening;
    private volatile bool _disposed;
    private int _resourcesReleased;

    public LinuxSingleInstance(string name, string? runtimeDirectory = null, Action<Action>? dispatch = null, TimeSpan? activationBudget = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _dispatch = dispatch ?? (a => Dispatcher.UIThread.Post(a));
        _activationBudget = activationBudget ?? DefaultActivationBudget;

        if (!OperatingSystem.IsLinux()) { return; }

        var directory = ResolveRuntimeDirectory(runtimeDirectory);
        _directory = directory;
        _socketPath = ComputeSocketPath(name, directory);
        _lockPath = ComputeLockPath(name, directory);

        LockResult = LinuxInstanceNative.TryAcquireExclusiveLock(_lockPath, out var stream, out var lockInfo);
        _lockStream = stream;
        IsFirstInstance = LockResult == LockAcquireResult.Acquired;
        if (IsFirstInstance && LinuxInstanceNative.TryStat(directory, out var dirInfo))
        {
            _lockedDirectory = dirInfo;
            _lockedLock = lockInfo;
        }
        else if (IsFirstInstance)
        {
            // Could not fingerprint the directory we hold the lock in: refuse ownership rather than
            // start a primary we cannot re-verify before binding.
            LockResult = LockAcquireResult.Error;
            IsFirstInstance = false;
            try { _lockStream?.Dispose(); } catch { }
            _lockStream = null;
        }
    }

    public bool IsFirstInstance { get; private set; }

    /// <summary>
    /// True only for a live accept worker whose bound endpoint and persistent lock are still attributed
    /// to this owner at the current pathname. A ready flag alone is not enough: a removed/replaced
    /// socket or lock pathname must clear readiness so a later caller cannot be told activation is
    /// possible into a name this process no longer owns.
    /// </summary>
    public bool CanActivate => !_disposed && _ready && EndpointStillAttributed() && RecheckOwnershipFingerprint();

    private bool EndpointStillAttributed()
    {
        if (_socketPath is null || _ownedSocket is not { } owned) { return false; }
        if (!LinuxInstanceNative.TryStat(_socketPath, out var current)) { return false; }
        var euid = LinuxInstanceNative.CurrentEffectiveUid();
        return current.IsSocket && euid >= 0 && current.Uid == (uint)euid && current.SameFile(owned);
    }

    /// <summary>Set when a non-owner's activation attempt failed; never a silent success.</summary>
    public bool ActivationFailed { get; private set; }

    /// <summary>How the exclusive lock resolved; only <see cref="LockAcquireResult.Contended"/> may try activation.</summary>
    internal LockAcquireResult LockResult { get; private set; }

    /// <summary>True only for genuine flock contention, i.e. another live owner.</summary>
    internal bool LockContended => LockResult == LockAcquireResult.Contended;

    /// <summary>Starts the single recovery channel. Idempotent; a disposed or already-listening instance is a no-op.</summary>
    public void Listen(Action onActivate)
    {
        if (_disposed || !IsFirstInstance || _listening || _socketPath is null || !OperatingSystem.IsLinux()) { return; }

        // A path that cannot fit sockaddr_un has no recovery channel; the window must not hide.
        if (!SingleInstanceProtocol.IsSocketPathWithinLimit(_socketPath)) { return; }

        _onActivate = onActivate ?? (() => { });

        Socket? socket = null;
        try
        {
            // Re-check the directory and lock fingerprint after acquisition and immediately before bind.
            if (!RecheckOwnershipFingerprint())
            {
                throw new IOException("The runtime directory or lock changed after acquisition.");
            }

            PrepareEndpointForBind();

            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(_socketPath));
            socket.Listen(Backlog);

            if (!LinuxInstanceNative.TryStat(_socketPath, out var info))
            {
                throw new IOException("Cannot attribute the bound socket endpoint.");
            }

            _ownedSocket = info;

            var euid = LinuxInstanceNative.CurrentEffectiveUid();
            if (euid >= 0) { WriteEndpointRecord(info, (uint)euid); }

            _socket = socket;
            socket = null; // ownership transferred to the field
        }
        catch
        {
            _onActivate = null;
            try { socket?.Dispose(); } catch { }
            TryCloseSocket();
            SetReady(false);
            return;
        }

        StartAcceptLoop();
    }

    /// <summary>
    /// Non-owner activation: connect, prove the same effective UID, send-all the fixed <c>show</c>, and
    /// require the exact <c>shown</c> ack, all within one two-second total budget. A failure after the
    /// request may have been delivered is never retried; only a not-yet-listening connect is retried.
    /// </summary>
    public bool TryActivatePrimary()
    {
        ActivationFailed = false;
        if (_disposed || _socketPath is null || !OperatingSystem.IsLinux())
        {
            ActivationFailed = true;
            return false;
        }

        var euid = LinuxInstanceNative.CurrentEffectiveUid();
        if (euid < 0) { ActivationFailed = true; return false; }

        var clock = Stopwatch.StartNew();
        while (Remaining(clock) > TimeSpan.Zero)
        {
            Socket? client = null;
            try
            {
                client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                ConnectWithinBudget(client, _socketPath, Remaining(clock));
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or AggregateException)
            {
                try { client?.Dispose(); } catch { }
                SleepRemaining(clock);
                continue; // transient not-ready: retry only the connect while the total budget lasts
            }

            try
            {
                if (!LinuxInstanceNative.TryGetPeerUid(client, out var peerUid)
                    || !SingleInstanceProtocol.IsSameEffectiveUser(euid, peerUid))
                {
                    ActivationFailed = true;
                    return false;
                }

                if (Remaining(clock) <= TimeSpan.Zero
                    || !SendAllWithinBudget(client, SingleInstanceProtocol.ShowMessage, Remaining(clock)))
                {
                    ActivationFailed = true;
                    return false;
                }

                // Build the reader while the socket is still connected: .NET reports a socket as unconnected
                // after Shutdown(Send), and the NetworkStream constructor rejects such a socket on Linux.
                using var reader = new NetworkStream(client, ownsSocket: false);
                try { client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }

                var ack = ReceiveCompleteAsync(reader, SingleInstanceProtocol.AckMaxBytes, Remaining(clock), CancellationToken.None).GetAwaiter().GetResult();
                if (SingleInstanceProtocol.IsValidAck(ack)) { return true; }

                // No retry: the request may already have been accepted even if the ack was lost.
                ActivationFailed = true;
                return false;
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException or InvalidDataException)
            {
                ActivationFailed = true;
                return false;
            }
            finally
            {
                try { client?.Dispose(); } catch { }
            }
        }

        ActivationFailed = true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _ready = false;
        _onActivate = null;

        try { _cts.Cancel(); } catch { }

        // Unblock the worker: close the listening socket and any in-flight accepted connection.
        TryCloseSocket();
        try { _activeConnection?.Dispose(); } catch { }

        var joined = true;
        var thread = _acceptThread;
        if (thread is not null)
        {
            try { joined = thread.Join(500); } catch { joined = false; }
        }

        if (joined)
        {
            _acceptThread = null;
            // Attribute/remove the socket while this instance still owns the persistent lock.
            CleanupOwnedSocket();
            // The worker has stopped; release the retained lock/CTS exactly once.
            ReleaseOwnedResources();
        }
        // else: the worker is still live. It releases the retained lock/CTS exactly once when its own
        // finally runs, so nothing is leaked and nothing is released while the worker may still use it.
    }

    /// <summary>
    /// Releases the retained persistent lock and CTS exactly once, after the accept worker has stopped.
    /// Called either by <see cref="Dispose"/> (joined) or by the worker's own finally (late exit); the
    /// first caller wins, so a late-stopping worker never leaks and busy resources are never released
    /// from under a live worker.
    /// </summary>
    private void ReleaseOwnedResources()
    {
        if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0) { return; }

        try { _cts.Dispose(); } catch { }
        try { _lockStream?.Dispose(); } catch { }
        _lockStream = null;
    }

    internal static string ComputeSocketPath(string name, string directory)
        => Path.Combine(directory, ShortStem(name) + ".sock");

    /// <summary>The actual constructor's lock path computation.</summary>
    internal static string ComputeLockPath(string name, string directory)
        => Path.Combine(directory, ShortStem(name) + ".lock");

    /// <summary>Verified per-user runtime dir, otherwise an app-private 0700 fallback; ancestors are never chmod'ed.</summary>
    internal static string ResolveRuntimeDirectory(string? overrideDirectory)
    {
        if (!string.IsNullOrEmpty(overrideDirectory))
        {
            if (!TryNormalizeAbsolute(overrideDirectory, out var absolute))
            {
                throw new IOException("Runtime directory override is not an absolute, link-safe path.");
            }
            if (!IsVerifiedPrivateDirectory(absolute))
            {
                throw new IOException("Runtime directory override is not a verified private owned directory.");
            }
            return absolute;
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg) && TryNormalizeAbsolute(xdg, out var xdgAbsolute) && IsVerifiedPrivateDirectory(xdgAbsolute))
        {
            var appDirectory = Path.Combine(xdgAbsolute, "OpenCodeAccountManager");
            if (TryCreatePrivateDirectory(appDirectory)) { return appDirectory; }
        }

        var dataHome = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(dataHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dataHome = string.IsNullOrEmpty(home) ? Path.GetTempPath() : Path.Combine(home, ".local", "share");
        }

        var fallback = Path.Combine(dataHome, "OpenCodeAccountImporter", "instance");
        if (TryCreatePrivateDirectory(fallback)) { return fallback; }

        throw new IOException("No verified private runtime directory is available.");
    }

    /// <summary>
    /// Verified per-user private directory. The path must be absolute, contain no <c>.</c>/<c>..</c>
    /// component, and every component must be a real (non-symlink) directory. The leaf must be owned by
    /// us and 0700; every ancestor must be a trusted component (root/self owned and not group/other
    /// writable, or a root-owned sticky directory such as <c>/tmp</c>). Ancestors are never chmod'ed.
    /// </summary>
    internal static bool IsVerifiedPrivateDirectory(string directory)
    {
        if (!TryNormalizeAbsolute(directory, out var absolute)) { return false; }

        var parts = absolute.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { return false; }

        var euid = LinuxInstanceNative.CurrentEffectiveUid();
        if (euid < 0) { return false; }

        for (var i = 0; i < parts.Length; i++)
        {
            var partial = PartialPath(parts, i + 1);
            if (!LinuxInstanceNative.TryStat(partial, out var info)) { return false; }
            if (!info.IsDirectory || info.IsSymlink) { return false; }

            if (i == parts.Length - 1)
            {
                if (info.Uid != (uint)euid || !info.IsPrivateOwner) { return false; }
            }
            else if (!IsTrustedAncestor(info, (uint)euid))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A non-leaf component is trusted only when it cannot be swapped by another local user: owned by
    /// root or by us and not group/other writable. A root-owned sticky directory (e.g. <c>/tmp</c> 1777)
    /// is the one writable-by-others exception; an own-uid world-writable non-sticky directory is not.
    /// </summary>
    private static bool IsTrustedAncestor(LinuxStatInfo info, uint euid)
    {
        if (!info.IsDirectory || info.IsSymlink) { return false; }

        const ushort GroupWrite = 0x10;
        const ushort OtherWrite = 0x2;
        const ushort Sticky = 0x200;

        var writableByOthers = (info.Mode & (GroupWrite | OtherWrite)) != 0;
        if (writableByOthers)
        {
            return info.Uid == 0 && (info.Mode & Sticky) != 0;
        }

        return info.Uid == 0 || info.Uid == euid;
    }

    private static bool TryNormalizeAbsolute(string path, out string absolute)
    {
        absolute = string.Empty;
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) { return false; }

        var separator = Path.DirectorySeparatorChar;
        var trimmed = path.TrimEnd(separator);
        if (trimmed.Length == 0) { trimmed = separator.ToString(); }

        var parts = trimmed.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            // Never erase a traversal before checking it.
            if (part == "." || part == "..") { return false; }
        }

        absolute = "/" + string.Join('/', parts);
        return true;
    }

    private static string PartialPath(string[] parts, int count)
    {
        var builder = new StringBuilder("/");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) { builder.Append('/'); }
            builder.Append(parts[i]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// Creates at most the missing app-owned children, but only after each already-existing component
    /// has been checked as a trusted real directory. Existing ancestors are never chmod'ed and nothing
    /// is created beneath a symlink or an untrusted component; the create is always the deepest missing
    /// level under an already-verified parent. The path is re-verified after creation.
    /// </summary>
    private static bool TryCreatePrivateDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux()) { return false; }
        if (!TryNormalizeAbsolute(directory, out var absolute)) { return false; }
        if (IsVerifiedPrivateDirectory(absolute)) { return true; }

        var parts = absolute.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { return false; }

        var euid = LinuxInstanceNative.CurrentEffectiveUid();
        if (euid < 0) { return false; }

        for (var i = 0; i < parts.Length; i++)
        {
            var partial = PartialPath(parts, i + 1);
            var isLeaf = i == parts.Length - 1;

            if (!LinuxInstanceNative.TryStat(partial, out var info))
            {
                // Missing: the loop guarantees the parent was already trusted (or created by us), so a
                // single create at this level cannot traverse a link or write under an untrusted dir.
                if (i == 0) { return false; }
                try
                {
                    Directory.CreateDirectory(
                        partial,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch
                {
                    return false;
                }

                if (!LinuxInstanceNative.TryStat(partial, out info)) { return false; }
            }

            if (!info.IsDirectory || info.IsSymlink) { return false; }

            if (isLeaf)
            {
                if (info.Uid != (uint)euid || !info.IsPrivateOwner) { return false; }
            }
            else if (!IsTrustedAncestor(info, (uint)euid))
            {
                return false;
            }
        }

        return true;
    }

    private void StartAcceptLoop()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => AcceptLoop(started))
        {
            IsBackground = true,
            Name = "LinuxSingleInstanceListener",
        };

        try
        {
            thread.Start();
        }
        catch
        {
            // The worker never ran: clean the owned endpoint and report not-ready, never a fake listener.
            try { started.TrySetResult(false); } catch { }
            TryCloseSocket();
            CleanupOwnedSocket();
            SetReady(false);
            return;
        }

        _acceptThread = thread;

        var ready = false;
        try { ready = started.Task.Wait(TimeSpan.FromSeconds(1)); } catch { }

        if (!ready || _disposed || _cts.IsCancellationRequested)
        {
            // Never report readiness for a worker that did not actually start.
            SetReady(false);
            return;
        }

        _listening = true;
    }

    private void AcceptLoop(TaskCompletionSource<bool> started)
    {
        try
        {
            // Readiness is published only after the actual current path, directory and lock still prove
            // the listener's ownership, not merely because this worker thread began running.
            if (_disposed || _cts.IsCancellationRequested || !RecheckOwnershipFingerprint() || !EndpointStillAttributed())
            {
                started.TrySetResult(false);
                return;
            }

            SetReady(true);
            started.TrySetResult(true);

            while (!_cts.IsCancellationRequested)
            {
                var listener = _socket;
                if (listener is null) { return; }

                try
                {
                    if (!listener.Poll(200_000, SelectMode.SelectRead)) { continue; }
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException)
                {
                    if (_cts.IsCancellationRequested) { return; }
                    continue;
                }

                Socket connection;
                try { connection = listener.Accept(); }
                catch (SocketException) { if (_cts.IsCancellationRequested) { return; } continue; }
                catch (ObjectDisposedException) { return; }

                try { HandleConnection(connection); }
                catch { }
                finally { try { connection.Dispose(); } catch { } }
            }
        }
        finally
        {
            SetReady(false);
            // A timed-out Dispose leaves both endpoint cleanup and resource release to the worker, after
            // it has stopped using its socket/CTS and while it still holds the persistent lock.
            if (_disposed)
            {
                CleanupOwnedSocket();
                ReleaseOwnedResources();
            }
        }
    }

    private void HandleConnection(Socket connection)
    {
        _activeConnection = connection;
        // One original server deadline covers read, dispatch and the ack; no phase restarts the window.
        var clock = Stopwatch.StartNew();
        try
        {
            if (_disposed || _cts.IsCancellationRequested) { return; }

            var euid = LinuxInstanceNative.CurrentEffectiveUid();
            if (euid < 0) { return; }
            if (!LinuxInstanceNative.TryGetPeerUid(connection, out var peerUid)) { return; }

            var callback = _onActivate;
            if (callback is null) { return; }

            // The actual socket path uses the same bounded framing/validation/dispatch sequence exposed
            // to controlled-stream tests; the clock remains the one connection deadline started above.
            using var requestStream = new NetworkStream(connection, ownsSocket: false);
            var dispatched = DispatchServerActivationAsync(
                requestStream,
                SingleInstanceProtocol.MaxMessageBytes,
                euid,
                peerUid,
                () => RemainingOf(clock, ServerConnectionBudget),
                () => !_disposed && !_cts.IsCancellationRequested,
                () => _dispatch(callback),
                _cts.Token).GetAwaiter().GetResult();
            if (!dispatched) { return; }

            if (_disposed || _cts.IsCancellationRequested) { return; }

            // Bounded ack inside the same original server deadline, not a fresh full window.
            var remaining = RemainingOf(clock, ServerConnectionBudget);
            if (remaining > TimeSpan.Zero)
            {
                SendAllWithinBudget(connection, SingleInstanceProtocol.AckMessage, remaining);
            }
        }
        catch { }
        finally { _activeConnection = null; }
    }

    /// <summary>
    /// Reads and validates one complete server request, then dispatches it only while the caller's
    /// original connection deadline and lifecycle guard remain valid. Production passes its connection
    /// clock; the delegate keeps the same path deterministic under a controlled stream test.
    /// </summary>
    internal static async Task<bool> DispatchServerActivationAsync(
        Stream requestStream,
        int cap,
        long serverUid,
        long peerUid,
        Func<TimeSpan> remaining,
        Func<bool> mayDispatch,
        Action dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        ArgumentNullException.ThrowIfNull(remaining);
        ArgumentNullException.ThrowIfNull(mayDispatch);
        ArgumentNullException.ThrowIfNull(dispatch);
        if (!SingleInstanceProtocol.IsSameEffectiveUser(serverUid, peerUid)) { return false; }

        var initialBudget = remaining();
        if (initialBudget <= TimeSpan.Zero) { return false; }
        var payload = await ReceiveCompleteAsync(requestStream, cap, initialBudget, cancellationToken).ConfigureAwait(false);
        if (!SingleInstanceProtocol.IsValidActivation(payload)
            || cancellationToken.IsCancellationRequested
            || remaining() <= TimeSpan.Zero
            || !mayDispatch())
        {
            return false;
        }

        dispatch();
        return true;
    }

    /// <summary>Whether the lock file must clear an existing endpoint, using positive ownership proof.</summary>
    private void PrepareEndpointForBind()
    {
        var path = _socketPath;
        if (path is null) { throw new IOException("No socket endpoint is configured."); }

        if (!LinuxInstanceNative.TryStat(path, out var existing)) { return; } // nothing at the endpoint

        var euid = LinuxInstanceNative.CurrentEffectiveUid();
        if (euid < 0) { throw new IOException("Cannot determine the effective user."); }

        if (!existing.IsSocket || existing.Uid != (uint)euid)
        {
            throw new IOException("Refusing to unlink an unattributable path at the socket endpoint.");
        }

        if (!TryReadEndpointRecord(out var recorded, out var recordedUid)
            || !LinuxInstanceEndpointRecord.Matches(recorded, recordedUid, existing, (uint)euid))
        {
            // A same-user socket that this application did not record is not ours to remove.
            throw new IOException("Refusing to unlink a socket endpoint this application did not record.");
        }

        File.Delete(path);
    }

    /// <summary>
    /// Re-verifies, immediately before bind and before reporting readiness, that the runtime directory is
    /// still a fully trusted private path at its recorded inode and that the current lock pathname is a
    /// regular private file for our uid naming the exact inode we still hold open.
    /// </summary>
    private bool RecheckOwnershipFingerprint()
    {
        if (_socketPath is null || _lockStream is null || _lockPath is null || _directory is null) { return false; }

        var euid = LinuxInstanceNative.CurrentEffectiveUid();
        if (euid < 0) { return false; }

        // Full current component trust + private leaf, and the same inode we fingerprinted.
        if (!IsVerifiedPrivateDirectory(_directory)) { return false; }
        if (!LinuxInstanceNative.TryStat(_directory, out var dir) || !dir.SameFile(_lockedDirectory)) { return false; }

        // The actual lock pathname must be regular/private/ours and name the descriptor we still own.
        if (!LinuxInstanceNative.TryStat(_lockPath, out var path)) { return false; }
        if (!path.IsRegular || path.Uid != (uint)euid || !path.IsPrivateFile) { return false; }

        return TryStatLock(out var lockInfo)
            && lockInfo.SameFile(path)
            && lockInfo.SameFile(_lockedLock);
    }

    private bool TryStatLock(out LinuxStatInfo info)
    {
        info = default;
        var stream = _lockStream;
        if (stream is null) { return false; }

        var handle = stream.SafeFileHandle;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return LinuxInstanceNative.TryStatFd((int)handle.DangerousGetHandle(), out info, out _);
        }
        finally
        {
            if (added) { handle.DangerousRelease(); }
        }
    }

    private bool TryReadEndpointRecord(out LinuxStatInfo info, out uint uid)
    {
        info = default;
        uid = 0;
        var stream = _lockStream;
        if (stream is null) { return false; }

        try
        {
            // Exact framing: the persistent lock file must contain exactly one bounded record. Extra or
            // truncated content is unknown and must not authorize a stale-endpoint unlink.
            if (stream.Length != LinuxInstanceEndpointRecord.Size) { return false; }

            stream.Position = 0;
            var buffer = new byte[LinuxInstanceEndpointRecord.Size];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) { break; }
                total += read;
            }
            return total == LinuxInstanceEndpointRecord.Size
                && LinuxInstanceEndpointRecord.TryDecode(buffer, out info, out uid);
        }
        catch
        {
            return false;
        }
    }

    private void WriteEndpointRecord(LinuxStatInfo info, uint uid)
    {
        var stream = _lockStream;
        if (stream is null) { return; }

        try
        {
            var record = LinuxInstanceEndpointRecord.Encode(info, uid);
            stream.Position = 0;
            stream.SetLength(0);
            stream.Write(record, 0, record.Length);
            stream.Flush(true);
        }
        catch
        {
            // Best-effort only: a failed write/flush is not a durable record. A later owner therefore
            // cannot rely on it and refuses to unlink an unproven same-user socket (fail closed).
        }
    }

    private void CleanupOwnedSocket()
    {
        if (_socketPath is null || _ownedSocket is not { } owned) { return; }

        // Require the full current directory + lock-path proof as well as the socket inode proof; a
        // replaced pathname or unsafe ancestor is never cleaned up under stale ownership assumptions.
        if (!RecheckOwnershipFingerprint())
        {
            _ownedSocket = null;
            return;
        }

        if (LinuxInstanceNative.TryStat(_socketPath, out var current))
        {
            var euid = LinuxInstanceNative.CurrentEffectiveUid();
            if (current.IsSocket && euid >= 0 && current.Uid == (uint)euid && current.SameFile(owned))
            {
                try { File.Delete(_socketPath); } catch { }
            }
        }

        _ownedSocket = null;
    }

    private void TryCloseSocket()
    {
        try { _socket?.Dispose(); } catch { }
        _socket = null;
    }

    private void SetReady(bool value) => _ready = value;

    private TimeSpan Remaining(Stopwatch clock)
    {
        var remaining = _activationBudget - clock.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void SleepRemaining(Stopwatch clock)
    {
        var remaining = Remaining(clock);
        if (remaining <= TimeSpan.Zero) { return; }
        Thread.Sleep(remaining < TimeSpan.FromMilliseconds(RetryDelayMs) ? remaining : TimeSpan.FromMilliseconds(RetryDelayMs));
    }

    private static void ConnectWithinBudget(Socket socket, string path, TimeSpan remaining)
    {
        // ConnectAsync(EndPoint) returns a Task in net8.0; there is no ValueTask/AsTask here.
        var connect = socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        if (!connect.Wait(remaining))
        {
            // Owned close unblocks the abandoned connect; observe its completion so no fault is unobserved.
            try { socket.Dispose(); } catch { }
            ObserveAbandoned(connect);
            throw new TimeoutException("Connect exceeded the activation budget.");
        }

        connect.GetAwaiter().GetResult();
    }

    private static bool SendAllWithinBudget(Socket socket, ReadOnlySpan<byte> payload, TimeSpan remaining)
    {
        // One total remaining budget across every partial send, not a fresh full timeout per send.
        var clock = Stopwatch.StartNew();
        var sent = 0;
        while (sent < payload.Length)
        {
            var left = RemainingOf(clock, remaining);
            if (left <= TimeSpan.Zero) { return false; }

            socket.SendTimeout = (int)Math.Clamp(left.TotalMilliseconds, 1, int.MaxValue);

            int written;
            try { written = socket.Send(payload[sent..], SocketFlags.None); }
            catch (SocketException) { return false; }

            if (written <= 0) { return false; }
            sent += written;
        }
        return true;
    }

    private static TimeSpan RemainingOf(Stopwatch clock, TimeSpan budget)
    {
        var left = budget - clock.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static byte[] ReceiveComplete(Socket socket, int cap, TimeSpan remaining)
    {
        socket.ReceiveTimeout = (int)Math.Clamp(remaining.TotalMilliseconds, 1, int.MaxValue);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        return ReceiveCompleteAsync(stream, cap, remaining, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Reads a complete, bounded framed message: only a real EOF at or below <paramref name="cap"/>
    /// returns; cap+1 throws <see cref="InvalidDataException"/> and a missing EOF / expired total
    /// deadline throws <see cref="TimeoutException"/> (or the caller's cancellation), never a partial
    /// successful payload. One monotonic budget covers the whole read.
    /// </summary>
    internal static async Task<byte[]> ReceiveCompleteAsync(Stream stream, int cap, TimeSpan remaining, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (cap <= 0) { throw new ArgumentOutOfRangeException(nameof(cap)); }

        var buffer = new byte[cap + 1];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (remaining <= TimeSpan.Zero) { deadline.Cancel(); }
        else { deadline.CancelAfter(remaining); }
        var token = deadline.Token;

        var total = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                var readTask = stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), token).AsTask();
                int read;
                try
                {
                    // A stream may ignore the token; the bounded wait ends the read regardless.
                    read = await readTask.WaitAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    ObserveAbandoned(readTask);
                }

                // Re-check after the completed read: a stream can synchronously cancel the caller and
                // then return a completed EOF, which WaitAsync would otherwise hand back as a success.
                token.ThrowIfCancellationRequested();

                if (read <= 0)
                {
                    // Accept EOF only while the one monotonic budget has not actually elapsed, rather
                    // than relying on the timer callback having been scheduled.
                    if (clock.Elapsed > remaining)
                    {
                        throw new TimeoutException("The framed message did not complete within its total budget.");
                    }
                    break;
                }

                total += read;
                if (total > cap)
                {
                    throw new InvalidDataException("The framed message exceeded its bounded cap.");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The framed message did not complete within its total budget.");
        }

        return buffer[..total];
    }

    private static void ObserveAbandoned(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
            return;
        }
        if (task.IsCompleted) { return; }

        _ = task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string ShortStem(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return "instance-" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
