using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace AccountManager.Core.Security;

/// <summary>
/// Unix at-rest secret storage: AES-GCM with a 32-byte key kept in <c>backup.key</c> (mode
/// 0600) inside an application-private directory (mode 0700). The on-disk ciphertext layout is
/// unchanged — nonce(12) || tag(16) || cipher — so payloads written by the previous
/// implementation stay readable.
///
/// Safety rules: the owned leaf directory is created at 0700 (or tightened on reuse) and the
/// key at 0600; an existing key is never replaced, only its permissions are tightened. A key
/// path that is a link, a directory, unreadable or the wrong size is refused. Permission or
/// link-inspection failures fail closed rather than being treated as absence. Only the
/// application-owned leaf is touched; HOME and other ancestors are never chmod'ed.
/// </summary>
internal sealed class UnixSecretStore
{
    internal const string KeyFileName = "backup.key";

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _directory;

    internal UnixSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
    }

    /// <summary>Legacy-compatible key location: <c>&lt;directory&gt;/backup.key</c>.</summary>
    internal string KeyPath => Path.Combine(_directory, KeyFileName);

    /// <summary>Encrypts with the existing key, creating and publishing one on first use.</summary>
    internal byte[] Protect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var key = LoadOrCreateKey();
        try
        {
            return Encrypt(key, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Decrypts with the existing key. A missing key is reported, never created: ciphertext
    /// whose key is gone cannot be recovered by minting a new one.
    /// </summary>
    internal byte[] Unprotect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var key = LoadKeyOrThrow();
        try
        {
            return Decrypt(key, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Encrypt(byte[] key, byte[] data)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, data, cipher, tag);
        }

        var output = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, output, NonceSize + TagSize, cipher.Length);
        return output;
    }

    private static byte[] Decrypt(byte[] key, byte[] data)
    {
        if (data.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Protected payload is too short.");
        }

        var nonce = data.AsSpan(0, NonceSize);
        var tag = data.AsSpan(NonceSize, TagSize);
        var cipher = data.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        return plain;
    }

    private byte[] LoadOrCreateKey()
    {
        EnsurePrivateDirectory(create: true);

        var keyPath = KeyPath;
        if (IsLink(keyPath))
        {
            throw new CryptographicException($"Secret key path '{keyPath}' is a link; refusing to follow it.");
        }

        if (TryGetAttributes(keyPath, out var attributes))
        {
            // Existing key: contents are preserved, only permissions are tightened.
            return ReadAndTightenExistingKey(keyPath, attributes);
        }

        return CreateKeyAtomically();
    }

    private byte[] LoadKeyOrThrow()
    {
        // Read path also tightens an existing owned directory/key, but never creates them.
        EnsurePrivateDirectory(create: false);

        var keyPath = KeyPath;
        if (IsLink(keyPath))
        {
            throw new CryptographicException($"Secret key path '{keyPath}' is a link; refusing to follow it.");
        }

        if (!TryGetAttributes(keyPath, out var attributes))
        {
            throw new CryptographicException($"Secret key is missing at '{keyPath}'.");
        }

        return ReadAndTightenExistingKey(keyPath, attributes);
    }

    /// <summary>
    /// Reads an existing key and tightens its on-disk permissions. The key buffer is zeroed if
    /// the permission change fails, so a failed reuse never leaves key material in memory and
    /// never triggers creation of a replacement.
    /// </summary>
    private static byte[] ReadAndTightenExistingKey(string keyPath, FileAttributes attributes)
    {
        var key = ReadValidatedKey(keyPath, attributes);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                EnsurePrivateFileMode(keyPath);
            }

            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private void EnsurePrivateDirectory(bool create)
    {
        RejectLinkedDirectory();

        if (Directory.Exists(_directory))
        {
            if (!OperatingSystem.IsWindows())
            {
                RestrictOwnedDirectory();
            }

            return;
        }

        if (!create)
        {
            throw new CryptographicException($"Private directory is missing at '{_directory}'.");
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
        }
        else
        {
            CreatePrivateDirectory(_directory);
        }

        // Re-check after creation so a path swapped for a link is not then chmod'ed.
        RejectLinkedDirectory();
        if (!OperatingSystem.IsWindows())
        {
            RestrictOwnedDirectory();
        }
    }

    private void RejectLinkedDirectory()
    {
        if (IsLink(_directory))
        {
            throw new CryptographicException($"Private directory '{_directory}' is a link; refusing to use it.");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private void RestrictOwnedDirectory()
    {
        try
        {
            SetPrivateDirectoryUnixMode(_directory);
            if (GetUnixMode(_directory) != PrivateDirectoryMode)
            {
                throw new CryptographicException($"Private directory '{_directory}' is not mode 0700.");
            }
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new CryptographicException($"Cannot restrict permissions on private directory '{_directory}'.", ex);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void EnsurePrivateFileMode(string keyPath)
    {
        try
        {
            SetPrivateFileUnixMode(keyPath);
            if (GetUnixMode(keyPath) != PrivateFileMode)
            {
                throw new CryptographicException($"Secret key at '{keyPath}' is not mode 0600.");
            }
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new CryptographicException($"Cannot restrict permissions on secret key '{keyPath}'.", ex);
        }
    }

    /// <summary>
    /// True when the path itself is a symbolic link (including a dangling one), combining
    /// reparse-point attributes with <see cref="FileSystemInfo.LinkTarget"/> so detection does
    /// not depend on whether <see cref="File.GetAttributes"/> follows links. Inspection errors
    /// fail closed.
    /// </summary>
    private static bool IsLink(string path)
    {
        try
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Possibly a dangling link; fall through to the link-target check.
            }

            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException($"Cannot inspect '{path}' for links.", ex);
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Permission or inspection failure: fail closed, never treat as absence.
            throw new CryptographicException($"Cannot inspect secret key at '{path}'.", ex);
        }
    }

    private static byte[] ReadValidatedKey(string keyPath, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new CryptographicException($"Secret key path '{keyPath}' is a link; refusing to follow it.");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new CryptographicException($"Secret key path '{keyPath}' is a directory.");
        }

        return ReadKeyBounded(keyPath);
    }

    /// <summary>
    /// Rejects any key file whose reported on-disk length is not exactly 32 bytes before
    /// opening it — stable FIFOs, devices and short/long files all have a non-32 st_size and are
    /// refused without a synchronous open that could block. Then still reads at most 33 bytes
    /// and requires exactly 32, so a file that grows/shrinks between the length check and the
    /// read is caught. This is not race-proof file-type validation; see the same-user TOCTOU
    /// limit in the report.
    /// </summary>
    private static byte[] ReadKeyBounded(string keyPath)
    {
        long length;
        try
        {
            length = new FileInfo(keyPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException($"Cannot inspect secret key at '{keyPath}'.", ex);
        }

        if (length != KeySize)
        {
            throw new CryptographicException($"Secret key at '{keyPath}' is not {KeySize} bytes.");
        }

        var buffer = new byte[KeySize + 1];
        var total = 0;
        try
        {
            using var stream = new FileStream(
                keyPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                });

            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CryptographicException($"Secret key at '{keyPath}' is unreadable.", ex);
        }

        if (total != KeySize)
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new CryptographicException($"Secret key at '{keyPath}' is not {KeySize} bytes.");
        }

        var key = new byte[KeySize];
        Array.Copy(buffer, key, KeySize);
        CryptographicOperations.ZeroMemory(buffer);
        return key;
    }

    private byte[] CreateKeyAtomically()
    {
        RejectLinkedDirectory();

        var key = RandomNumberGenerator.GetBytes(KeySize);
        var tempPath = Path.Combine(
            _directory,
            "." + KeyFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteKeyFile(tempPath, key);
            if (PublishKeyExclusively(tempPath))
            {
                return key;
            }

            // Lost the race; adopt the key the winner published.
            CryptographicOperations.ZeroMemory(key);
            return LoadKeyOrThrow();
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Publishes the temp key under <see cref="KeyPath"/> only if no key exists yet; returns false when
    /// another creator already published one. On Linux, <c>File.Move(overwrite: false)</c> is a
    /// check-then-rename and silently replaces a key that a concurrent creator just published, so the
    /// publish uses <c>link(2)</c>, which fails atomically with EEXIST.
    /// </summary>
    private bool PublishKeyExclusively(string tempPath)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.Move(tempPath, KeyPath, overwrite: false);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        if (link(tempPath, KeyPath) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == EEXIST)
        {
            return false;
        }

        throw new CryptographicException($"Cannot publish the secret key at '{KeyPath}' (errno {error}).");
    }

    private const int EEXIST = 17;

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);

    private static void WriteKeyFile(string path, byte[] key)
    {
        FileStreamOptions options;
        if (OperatingSystem.IsWindows())
        {
            options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
        }
        else
        {
            // Created at 0600 from the first byte; there is no world-readable window.
            options = CreatePrivateFileOptions();
        }

        using var stream = new FileStream(path, options);
        stream.Write(key, 0, key.Length);
        stream.Flush(flushToDisk: true);
    }

    [UnsupportedOSPlatform("windows")]
    private static FileStreamOptions CreatePrivateFileOptions()
        => new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
            UnixCreateMode = PrivateFileMode,
        };

    [UnsupportedOSPlatform("windows")]
    private static void CreatePrivateDirectory(string directory)
        => Directory.CreateDirectory(directory, PrivateDirectoryMode);

    [UnsupportedOSPlatform("windows")]
    private static void SetPrivateDirectoryUnixMode(string directory)
        => File.SetUnixFileMode(directory, PrivateDirectoryMode);

    [UnsupportedOSPlatform("windows")]
    private static void SetPrivateFileUnixMode(string keyPath)
        => File.SetUnixFileMode(keyPath, PrivateFileMode);

    [UnsupportedOSPlatform("windows")]
    private static UnixFileMode GetUnixMode(string path) => File.GetUnixFileMode(path);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
