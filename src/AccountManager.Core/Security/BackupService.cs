using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccountManager.Core.Security;

/// <summary>
/// Encrypted backup of credentials + config text before any destructive write.
/// Uses Windows DPAPI (current user) on Windows; on other platforms an AES-GCM key
/// stored with 0600 permissions in the app data directory is used instead. On Unix the owned
/// backup directory is created at 0700 (tightened on reuse) and each backup file is created at
/// 0600. A linked/reparse backup directory is refused on every save, so a directory swapped for
/// a symlink after construction cannot redirect writes.
/// </summary>
public sealed class BackupService
{
    private readonly string _directory;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;

    public BackupService(string? directory = null)
        : this(directory ?? GetDefaultDirectory(), SecretProtector.Protect, SecretProtector.Unprotect)
    {
    }

    /// <summary>
    /// Test/DI constructor: callers supply the protection pair so an explicit temporary
    /// directory can be used without touching the process-wide default key store.
    /// </summary>
    internal BackupService(string directory, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        _protect = protect ?? throw new ArgumentNullException(nameof(protect));
        _unprotect = unprotect ?? throw new ArgumentNullException(nameof(unprotect));
        EnsurePrivateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    private static string GetDefaultDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "OpenCodeAccountImporter", "Backups");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "OpenCodeAccountImporter", "Backups");
    }

    public string Save(IEnumerable<JsonNode?> credentials, string? configPath, string? configText)
    {
        // Re-check on every save: the owned directory may have been replaced by a link, or its
        // permissions may have changed, since construction.
        EnsurePrivateDirectory(_directory);

        var payload = new JsonObject
        {
            ["version"] = 1,
            ["created"] = DateTimeOffset.Now.ToString("o"),
            ["credentials"] = new JsonArray(credentials.Select(c => c?.DeepClone()).ToArray()),
            ["configPath"] = configPath,
            ["configText"] = configText,
        };

        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        byte[]? encrypted = null;
        string? path = null;
        try
        {
            encrypted = _protect(bytes);
            var ext = OperatingSystem.IsWindows() ? ".dpapi" : ".enc";
            path = Path.Combine(_directory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ext);
            WriteBackupFile(path, encrypted);
            return path;
        }
        catch
        {
            // Never hand back a path for a backup that did not fully land, and never leave this
            // attempt's half-written file behind.
            if (path is not null)
            {
                TryDelete(path);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    /// <summary>Decrypt a backup file (used by tooling/tests). Never logs plaintext.</summary>
    public JsonNode? Read(string path)
    {
        var data = File.ReadAllBytes(path);
        try
        {
            var plain = _unprotect(data);
            try
            {
                return JsonNode.Parse(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        if (IsLink(directory))
        {
            throw new CryptographicException($"Backup directory '{directory}' is a link; refusing to use it.");
        }

        if (Directory.Exists(directory))
        {
            if (!OperatingSystem.IsWindows())
            {
                RestrictOwnedDirectory(directory);
            }

            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            CreatePrivateDirectory(directory);
        }

        if (IsLink(directory))
        {
            throw new CryptographicException($"Backup directory '{directory}' is a link; refusing to use it.");
        }

        if (!OperatingSystem.IsWindows())
        {
            RestrictOwnedDirectory(directory);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void RestrictOwnedDirectory(string directory)
    {
        try
        {
            SetPrivateDirectoryUnixMode(directory);
            if (GetUnixMode(directory)
                != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            {
                throw new CryptographicException($"Backup directory '{directory}' is not mode 0700.");
            }
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new CryptographicException($"Cannot restrict permissions on backup directory '{directory}'.", ex);
        }
    }

    /// <summary>
    /// True when the path itself is a symbolic link (including a dangling one), combining
    /// reparse-point attributes with <see cref="FileSystemInfo.LinkTarget"/>. Inspection errors
    /// fail closed rather than being treated as a safe path.
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

    private static void WriteBackupFile(string path, byte[] data)
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
            // Created at 0600; there is no group/other-readable window.
            options = CreatePrivateFileOptions();
        }

        using var stream = new FileStream(path, options);
        stream.Write(data, 0, data.Length);
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
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };

    [UnsupportedOSPlatform("windows")]
    private static void CreatePrivateDirectory(string directory)
        => Directory.CreateDirectory(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    [UnsupportedOSPlatform("windows")]
    private static void SetPrivateDirectoryUnixMode(string directory)
        => File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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
