using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using AccountManager.Core.Updates;

namespace AccountManager.App.Services;

/// <summary>
/// A content- and identity-attributed replacement transaction. Every operation uses the same
/// implementation in production and in isolated synthetic-file tests.
/// </summary>
internal sealed class LinuxUpdateSwap
{
    private const string RecoveryMessage = "Linux 更新未能安全完成；已保留旧版本备份。请勿删除备份，按手动恢复说明恢复程序。";
    private readonly string _source;
    private readonly string _current;
    private readonly string _staged;
    private readonly string _old;
    private FileProof? _originalProof;
    private FileProof? _sourceProof;
    private FileProof? _stagedProof;
    private bool _oldMoved;
    private bool _newMoved;
    private bool _stagedCreated;

    internal LinuxUpdateSwap(string source, string current, string staged, string old)
    {
        _source = Path.GetFullPath(source);
        _current = Path.GetFullPath(current);
        _staged = Path.GetFullPath(staged);
        _old = Path.GetFullPath(old);
        if (string.Equals(_current, _staged, PathComparison) || string.Equals(_current, _old, PathComparison) ||
            string.Equals(_staged, _old, PathComparison) || string.Equals(_source, _current, PathComparison))
        {
            throw new ArgumentException("Linux update paths must identify distinct files.");
        }
    }

    internal FileProof? BackupProof { get; private set; }

    internal FileProof? CurrentProof { get; private set; }

    internal string OldPath => _old;

    internal string StagedPath => _staged;

    internal void Stage()
    {
        if (_stagedProof is not null) { throw new InvalidOperationException("The Linux update candidate is already staged."); }
        EnsureOrdinaryFile(_source);
        EnsureOrdinaryFile(_current);
        EnsureSameDirectory(_current, _staged);
        EnsureSameDirectory(_current, _old);
        if (File.Exists(_staged) || Directory.Exists(_staged) || File.Exists(_old) || Directory.Exists(_old))
        {
            throw new IOException("Linux update temporary or backup path already exists.");
        }

        _sourceProof = Capture(_source);
        _originalProof = Capture(_current);
        CopyPrivateAndVerify(_sourceProof);
        _stagedProof = Capture(_staged);
        if (!IsLinuxX64Executable(_staged))
        {
            DeleteStagedOnlyIfOwned();
            throw new InvalidDataException("The staged update is not a complete Linux x64 ELF executable.");
        }
    }

    internal void Execute()
    {
        if (_stagedProof is null) { Stage(); }
        if (_sourceProof is null || _originalProof is null || _stagedProof is null) { throw new InvalidOperationException("The Linux update candidate is not staged."); }

        if (!SameIdentity(_originalProof, Capture(_current)))
        {
            throw new IOException("The current executable changed before the update swap.");
        }
        if (!SameIdentity(_sourceProof, Capture(_source)) || !SameIdentity(_stagedProof, Capture(_staged)))
        {
            throw new IOException("The verified update changed before the update swap.");
        }

        File.Move(_current, _old, overwrite: false);
        _oldMoved = true;
        var movedOldProof = Capture(_old);
        if (!SameMovedFile(_originalProof, movedOldProof))
        {
            throw new IOException("The old executable identity changed during the update swap.");
        }
        BackupProof = _originalProof with { Name = Path.GetFileName(_old) };

        if (File.Exists(_current) || Directory.Exists(_current))
        {
            throw new IOException("The executable path was concurrently replaced during the update swap.");
        }

        File.Move(_staged, _current, overwrite: false);
        _newMoved = true;
        var movedNewProof = Capture(_current);
        if (!SameMovedFile(_stagedProof, movedNewProof))
        {
            throw new IOException("The new executable identity changed during the update swap.");
        }
        CurrentProof = _stagedProof with { Name = Path.GetFileName(_current) };

        if (!SameIdentity(_sourceProof, Capture(_source)))
        {
            throw new IOException("The verified download changed during the update swap.");
        }
    }

    /// <summary>Compensates only after the caller confirms its exact child has exited.</summary>
    internal string? Rollback(bool childExitConfirmed)
    {
        if (!childExitConfirmed) { return RecoveryMessage; }

        try
        {
            FileProof? backupBeforeRollback = null;

            // Establish that every object needed to recover exists and is still attributable
            // before the first destructive operation. Recheck both proofs immediately at use.
            if (_newMoved)
            {
                if (!File.Exists(_current) || CurrentProof is null || !SameIdentity(CurrentProof, Capture(_current)))
                {
                    return RecoveryMessage;
                }
            }
            else if (File.Exists(_current) && _originalProof is not null && !SameIdentity(_originalProof, Capture(_current)) && !_oldMoved)
            {
                return RecoveryMessage;
            }

            if (_oldMoved)
            {
                if (!File.Exists(_old) || BackupProof is null || !SameIdentity(BackupProof, Capture(_old)))
                {
                    return RecoveryMessage;
                }
                backupBeforeRollback = Capture(_old);
                if ((!_newMoved && File.Exists(_current)) || Directory.Exists(_current)) { return RecoveryMessage; }
            }

            if (_newMoved)
            {
                if (CurrentProof is null || !SameIdentity(CurrentProof, Capture(_current))) { return RecoveryMessage; }
                if (_oldMoved && (BackupProof is null || backupBeforeRollback is null ||
                    !SameIdentity(BackupProof, Capture(_old)) || !SameIdentity(backupBeforeRollback, Capture(_old)))) { return RecoveryMessage; }
                File.Delete(_current);
                _newMoved = false;
            }

            if (_oldMoved)
            {
                if (!File.Exists(_old) || BackupProof is null || backupBeforeRollback is null ||
                    !SameIdentity(BackupProof, Capture(_old)) || !SameIdentity(backupBeforeRollback, Capture(_old)) ||
                    File.Exists(_current) || Directory.Exists(_current))
                {
                    return RecoveryMessage;
                }
                File.Move(_old, _current, overwrite: false);
                var restored = Capture(_current);
                if (!SameMovedFile(BackupProof, restored)) { return RecoveryMessage; }
                _oldMoved = false;
            }

            DeleteStagedOnlyIfOwned();
            return null;
        }
        catch
        {
            return RecoveryMessage;
        }
    }

    internal static bool TryCleanupOwnedBackup(string path, FileProof? proof)
    {
        if (proof is null) { return false; }
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(fullPath), proof.Name, StringComparison.Ordinal) || !File.Exists(fullPath) || Directory.Exists(fullPath)) { return false; }
            if (!SameIdentity(proof, Capture(fullPath))) { return false; }
            File.Delete(fullPath);
            return !File.Exists(fullPath);
        }
        catch { return false; }
    }

    internal static bool IsSafeOwnedInstallation(string executablePath)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) { return false; }
            var fullExe = Path.GetFullPath(executablePath);
            EnsureOrdinaryFile(fullExe);
            var directory = Path.GetDirectoryName(fullExe);
            if (string.IsNullOrEmpty(directory)) { return false; }
            EnsureNoLinksInPath(directory);
            var info = ReadLinuxStat(directory);
            var uid = LinuxSelfUpdater.GetEffectiveUid();
            const ushort groupOrOtherWrite = 0x0012;
            var executable = Capture(fullExe);
            return info.IsDirectory && info.Uid == uid && (info.Mode & groupOrOtherWrite) == 0
                && (info.Mode & 0x0080) != 0 && (info.Mode & 0x0040) != 0
                && IsLinuxX64Executable(fullExe)
                && executable.OwnerUid == uid && (executable.Mode & 0x0040) != 0;
        }
        catch { return false; }
    }

    internal static bool IsLinuxX64Executable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 64) { return false; }
            Span<byte> header = stackalloc byte[64];
            var offset = 0;
            while (offset < header.Length)
            {
                var count = stream.Read(header[offset..]);
                if (count == 0) { return false; }
                offset += count;
            }
            return ExecutableFormat.IsLinuxX64(header);
        }
        catch { return false; }
    }

    internal static FileProof Capture(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureOrdinaryFile(fullPath);
        var before = new FileInfo(fullPath);
        string identity;
        long ownerUid = -1;
        int mode = 0;

        if (OperatingSystem.IsLinux())
        {
            var stat = ReadLinuxStat(fullPath);
            if (!stat.IsRegularFile || stat.LinkCount != 1) { throw new IOException("Update path is not an owned ordinary file."); }
            identity = stat.DeviceMajor.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + ":"
                + stat.DeviceMinor.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + ":"
                + stat.Inode.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            ownerUid = stat.Uid;
            mode = stat.Mode;
        }
        else if (OperatingSystem.IsWindows())
        {
            using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None);
            if (!GetFileInformationByHandle(handle, out var info)) { throw new IOException("Cannot identify update file.", new Win32Exception(Marshal.GetLastWin32Error())); }
            identity = info.VolumeSerialNumber.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ":"
                + info.FileIndexHigh.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
                + info.FileIndexLow.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            mode = (int)before.Attributes;
        }
        else
        {
            identity = before.CreationTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            mode = (int)before.Attributes;
        }

        string hash;
        long length;
        using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            length = input.Length;
            hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }
        var after = new FileInfo(fullPath);
        if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc || length != after.Length)
        {
            throw new IOException("Update file changed while its identity was being captured.");
        }

        return new FileProof(Path.GetFileName(fullPath), hash, length, identity, mode, ownerUid,
            before.LastWriteTimeUtc.Ticks, before.CreationTimeUtc.Ticks);
    }

    internal static bool Matches(FileProof? expected, string path)
    {
        try { return expected is not null && SameIdentity(expected, Capture(path)); }
        catch { return false; }
    }

    internal static DirectoryProof CaptureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Update cleanup path is not an ordinary directory.");
        }

        string identity;
        long ownerUid = -1;
        int mode = (int)attributes;
        if (OperatingSystem.IsLinux())
        {
            var stat = ReadLinuxStat(fullPath);
            if (!stat.IsDirectory) { throw new IOException("Update cleanup path changed type."); }
            identity = stat.DeviceMajor.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + ":"
                + stat.DeviceMinor.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + ":"
                + stat.Inode.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            ownerUid = stat.Uid;
            mode = stat.Mode;
        }
        else if (OperatingSystem.IsWindows())
        {
            using var handle = CreateDirectoryHandle(fullPath);
            if (!GetFileInformationByHandle(handle, out var info) ||
                (info.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
                (info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Cannot identify update cleanup directory.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            identity = info.VolumeSerialNumber.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ":"
                + info.FileIndexHigh.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
                + info.FileIndexLow.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            mode = unchecked((int)info.FileAttributes);
        }
        else
        {
            var info = new DirectoryInfo(fullPath);
            identity = info.CreationTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var creationTicks = new DirectoryInfo(fullPath).CreationTimeUtc.Ticks;
        return new DirectoryProof(Path.GetFileName(fullPath), identity, mode, ownerUid, creationTicks);
    }

    internal static bool MatchesDirectory(DirectoryProof? expected, string path)
    {
        try
        {
            if (expected is null) { return false; }
            var actual = CaptureDirectory(path);
            // On Linux, DirectoryInfo.CreationTimeUtc is ctime, which changes whenever an entry is added or
            // removed inside the directory. The device/inode identity already pins the directory, so the
            // timestamp is compared only where it is a real creation time.
            return expected.Name == actual.Name && expected.Identity == actual.Identity &&
                (!OperatingSystem.IsLinux() || expected.Mode == actual.Mode) && expected.OwnerUid == actual.OwnerUid &&
                (OperatingSystem.IsLinux() || expected.CreationUtcTicks == actual.CreationUtcTicks);
        }
        catch { return false; }
    }

    internal static bool TryDeleteOwnedEmptyDirectory(string path, DirectoryProof? proof)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (proof is null || !string.Equals(Path.GetFileName(fullPath), proof.Name, StringComparison.Ordinal) ||
                !Directory.Exists(fullPath) || !MatchesDirectory(proof, fullPath) ||
                Directory.GetFileSystemEntries(fullPath).Length != 0 || !MatchesDirectory(proof, fullPath))
            {
                return false;
            }
            Directory.Delete(fullPath, recursive: false);
            return !Directory.Exists(fullPath);
        }
        catch { return false; }
    }

    private void CopyPrivateAndVerify(FileProof sourceBefore)
    {
        try
        {
            FileStream output;
            if (OperatingSystem.IsLinux())
            {
                output = new FileStream(_staged, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
            }
            else
            {
                output = new FileStream(_staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
            }
            _stagedCreated = true;

            using (output)
            using (var input = new FileStream(_source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(_staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var currentSource = Capture(_source);
            var copied = Capture(_staged);
            if (!SameIdentity(sourceBefore, currentSource) || sourceBefore.Sha256 != copied.Sha256 || sourceBefore.Length != copied.Length)
            {
                throw new InvalidDataException("The verified update changed while it was copied.");
            }
        }
        catch
        {
            DeleteStagedOnlyIfOwned();
            throw;
        }
    }

    private void DeleteStagedOnlyIfOwned()
    {
        try
        {
            if (!_stagedCreated) { return; }
            if (File.Exists(_staged) && _stagedProof is not null && SameIdentity(_stagedProof, Capture(_staged))) { File.Delete(_staged); }
            else if (File.Exists(_staged) && _stagedProof is null)
            {
                // Before a proof is captured, only remove a file we can independently tie to the
                // still-stable source bytes. This is the single CreateNew path owned by this call.
                var staged = Capture(_staged);
                var source = Capture(_source);
                if (staged.Sha256 == source.Sha256 && staged.Length == source.Length) { File.Delete(_staged); }
            }
        }
        catch { }
    }

    private static bool SameIdentity(FileProof? left, FileProof? right)
        => left is not null && right is not null
            && string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal)
            && left.Length == right.Length
            && string.Equals(left.Identity, right.Identity, StringComparison.Ordinal)
            && left.Mode == right.Mode && left.OwnerUid == right.OwnerUid;

    private static bool SameMovedFile(FileProof? left, FileProof? right)
        => SameIdentity(left, right);

    private static void EnsureOrdinaryFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Linux update path must be an ordinary file.");
        }
    }

    private static void EnsureSameDirectory(string firstPath, string secondPath)
    {
        var first = Path.GetDirectoryName(Path.GetFullPath(firstPath));
        var second = Path.GetDirectoryName(Path.GetFullPath(secondPath));
        if (!string.Equals(first, second, PathComparison)) { throw new IOException("Linux update swap files must share one directory."); }
    }

    private static void EnsureNoLinksInPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new IOException("Invalid installation path.");
        var current = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new IOException("Installation path contains a link."); }
        }
    }

    private static LinuxStat ReadLinuxStat(string path)
    {
        if (!OperatingSystem.IsLinux() || Statx(-100, path, 0x100, 0x7ff, out var stat) != 0)
        {
            throw new IOException("Cannot inspect Linux update file ownership.");
        }
        return new LinuxStat(stat.Mode, stat.Uid, stat.LinkCount, stat.Inode, stat.DeviceMajor, stat.DeviceMinor);
    }

    internal static long GetLinuxOwnerUid(string path) => ReadLinuxStat(path).Uid;

    private static SafeFileHandle CreateDirectoryHandle(string path)
    {
        var handle = CreateFileW(path, 0x80, 0x1 | 0x2 | 0x4, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("Cannot open update cleanup directory identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return handle;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Statx(int directoryFd, string path, int flags, uint mask, out StatxBuffer buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct StatxBuffer
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint Uid;
        public uint Gid;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public long AccessSeconds;
        public uint AccessNanoseconds;
        public int AccessReserved;
        public long BirthSeconds;
        public uint BirthNanoseconds;
        public int BirthReserved;
        public long ChangeSeconds;
        public uint ChangeNanoseconds;
        public int ChangeReserved;
        public long ModifySeconds;
        public uint ModifyNanoseconds;
        public int ModifyReserved;
        public uint RdevMajor;
        public uint RdevMinor;
        public uint DeviceMajor;
        public uint DeviceMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlignment;
        public uint DirectIoOffsetAlignment;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public ulong[] Spare;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private readonly record struct LinuxStat(ushort Mode, uint Uid, uint LinkCount, ulong Inode, uint DeviceMajor, uint DeviceMinor)
    {
        public bool IsRegularFile => (Mode & 0xf000) == 0x8000;
        public bool IsDirectory => (Mode & 0xf000) == 0x4000;
    }

    internal sealed record FileProof(
        string Name,
        string Sha256,
        long Length,
        string Identity,
        int Mode,
        long OwnerUid,
        long LastWriteUtcTicks,
        long CreationUtcTicks);

    internal sealed record DirectoryProof(string Name, string Identity, int Mode, long OwnerUid, long CreationUtcTicks);
}
