using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>
/// Behaviour tests for Task 1 (Unix private storage and first-create safety).
///
/// Keys, payloads and directories are synthetic; every directory is unique and created under
/// the process temp root. Expected values are literals or independent AES-GCM, never the
/// production algorithm under test.
/// </summary>
public class UnixSecretStoreTests
{
    // 32 bytes on purpose: a syntactically valid key so the store reads it instead of creating
    // one. Fixed literal so the test never derives it from the code under test.
    private static readonly byte[] SyntheticKey =
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
    };

    // ── compatibility contract ──────────────────────────────────────────────────────────

    /// <summary>Catches a break where the store stops using the legacy <c>backup.key</c> name.</summary>
    [Fact]
    public void KeyPathMatchesLegacyBackupKeyName()
    {
        using var temp = new TempDirectory();

        var store = new UnixSecretStore(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "backup.key"), store.KeyPath);
    }

    /// <summary>
    /// Catches a store that never actually writes the legacy key file, or that replaces it on
    /// reuse (which would orphan data encrypted under the first key).
    /// </summary>
    [Fact]
    public void ProtectCreatesAndReusesLegacyKeyFile()
    {
        using var temp = new TempDirectory();
        var writer = new UnixSecretStore(temp.Path);
        var payload = Encoding.UTF8.GetBytes("synthetic-key-persistence-probe");

        var ciphertext = writer.Protect(payload);

        Assert.Equal(Path.Combine(temp.Path, "backup.key"), writer.KeyPath);
        Assert.True(File.Exists(writer.KeyPath));
        Assert.Equal(32, File.ReadAllBytes(writer.KeyPath).Length);
        var keyBeforeReuse = File.ReadAllBytes(writer.KeyPath);

        // A second, independent store must read the on-disk key and leave it untouched.
        var reader = new UnixSecretStore(temp.Path);
        Assert.Equal(payload, reader.Unprotect(ciphertext));
        Assert.Equal(keyBeforeReuse, File.ReadAllBytes(writer.KeyPath));
    }

    // ── corrupt / wrong-length / unreadable keys must never be rebuilt ───────────────────

    /// <summary>Catches silently rotating a corrupt key: old backups become permanently unreadable.</summary>
    [Fact]
    public void CorruptKeyIsNeverReplaced()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "backup.key");
        File.WriteAllBytes(keyPath, new byte[] { 1, 2, 3 });

        Assert.Throws<CryptographicException>(() =>
        {
            _ = new UnixSecretStore(temp.Path).Protect(new byte[] { 42 });
        });

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(keyPath));
    }

    /// <summary>Catches accepting a too-long key (e.g. using the first 32 bytes) instead of refusing it.</summary>
    [Fact]
    public void Non32ByteKeyIsNeverReplaced()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "backup.key");
        var overlong = new byte[64];
        Array.Fill(overlong, (byte)0x5A);
        File.WriteAllBytes(keyPath, overlong);

        Assert.Throws<CryptographicException>(() =>
        {
            _ = new UnixSecretStore(temp.Path).Protect(new byte[] { 42 });
        });

        Assert.Equal(overlong, File.ReadAllBytes(keyPath));
    }

    /// <summary>
    /// Catches opening a st_size-0 key path before rejecting it. The pre-open length check makes
    /// this fail fast; stable FIFOs/devices share the same non-32 st_size and are refused the
    /// same way (the FIFO blocking scenario itself is owned by the Task 10 subprocess harness,
    /// so this host never opens a blocking special file).
    /// </summary>
    [Fact]
    public void EmptyKeyFileIsRejectedWithoutReplacement()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "backup.key");
        File.WriteAllBytes(keyPath, Array.Empty<byte>());

        Assert.Throws<CryptographicException>(() =>
        {
            _ = new UnixSecretStore(temp.Path).Protect(new byte[] { 42 });
        });

        Assert.Equal(0, new FileInfo(keyPath).Length);
        Assert.Empty(File.ReadAllBytes(keyPath));
    }

    /// <summary>
    /// Catches treating an unreadable key location as "missing" and generating a fresh key,
    /// which would overwrite the unusable path or publish a second key.
    /// </summary>
    [Fact]
    public void UnreadableKeyPathIsNotSilentlyRebuilt()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "backup.key");
        Directory.CreateDirectory(keyPath);

        Assert.ThrowsAny<Exception>(() =>
        {
            _ = new UnixSecretStore(temp.Path).Protect(new byte[] { 42 });
        });

        Assert.True(Directory.Exists(keyPath));
        Assert.False(File.Exists(keyPath));
    }

    /// <summary>
    /// Catches Unprotect minting a fresh key when the real key is gone: ciphertext with a
    /// missing key must be reported, not silently "recovered" under a new key.
    /// </summary>
    [Fact]
    public void UnprotectWithMissingKeyReportsMissingAndCreatesNothing()
    {
        using var temp = new TempDirectory();
        var store = new UnixSecretStore(temp.Path);

        Assert.Throws<CryptographicException>(() => { _ = store.Unprotect(new byte[64]); });

        Assert.False(File.Exists(store.KeyPath));
    }

    // ── concurrent first-use publishes exactly one key ───────────────────────────────────

    /// <summary>
    /// Catches a first-use race that publishes more than one key or exposes a partially written
    /// key: ciphertexts encrypted with a losing key are then undecryptable. All waits are
    /// bounded so a deadlock fails instead of hanging the run.
    /// </summary>
    [Fact]
    public void ConcurrentProtectPublishesOneKeyAndAllCiphertextsStayReadable()
    {
        using var temp = new TempDirectory();
        const int workers = 24;
        var payloads = new byte[workers][];
        var ciphertexts = new byte[workers][];
        var errors = new Exception?[workers];
        for (var i = 0; i < workers; i++)
        {
            payloads[i] = Encoding.UTF8.GetBytes("synthetic-payload-" + i);
        }

        using var barrier = new Barrier(workers);
        var threads = new Thread[workers];
        for (var i = 0; i < workers; i++)
        {
            var index = i;
            threads[index] = new Thread(() =>
            {
                try
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                    {
                        errors[index] = new TimeoutException("Workers did not reach the barrier in time.");
                        return;
                    }

                    ciphertexts[index] = new UnixSecretStore(temp.Path).Protect(payloads[index]);
                }
                catch (Exception ex)
                {
                    errors[index] = ex;
                }
            })
            {
                IsBackground = true,
            };
            threads[index].Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(
                thread.Join(TimeSpan.FromSeconds(30)),
                "A protect worker did not finish within the timeout.");
        }

        Assert.All(errors, error => Assert.Null(error));
        var keyPath = Path.Combine(temp.Path, "backup.key");
        Assert.Equal(32, File.ReadAllBytes(keyPath).Length);

        // Every ciphertext from every racing worker must decrypt under the single published key.
        var reader = new UnixSecretStore(temp.Path);
        for (var i = 0; i < workers; i++)
        {
            Assert.Equal(payloads[i], reader.Unprotect(ciphertexts[i]));
        }
    }

    // ── legacy nonce(12) || tag(16) || cipher layout is preserved both ways ──────────────

    /// <summary>
    /// Catches changing the on-disk layout of newly written data so existing readers can no
    /// longer decrypt it. Verified against an independent AES-GCM decode at literal offsets.
    /// </summary>
    [Fact]
    public void ProtectWritesLegacyNonceTagCipherLayout()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "backup.key"), SyntheticKey);
        var plaintext = Encoding.UTF8.GetBytes("synthetic-legacy-layout-probe");

        var output = new UnixSecretStore(temp.Path).Protect(plaintext);

        Assert.Equal(12 + 16 + plaintext.Length, output.Length);
        var nonce = output.AsSpan(0, 12).ToArray();
        var tag = output.AsSpan(12, 16).ToArray();
        var cipher = output.AsSpan(28).ToArray();
        var decrypted = new byte[cipher.Length];
        using (var aes = new AesGcm(SyntheticKey, 16))
        {
            aes.Decrypt(nonce, cipher, tag, decrypted);
        }

        Assert.Equal(plaintext, decrypted);
    }

    /// <summary>
    /// Catches a reader that no longer accepts the legacy nonce/tag/cipher ordering.
    /// The ciphertext is produced by an independent AES-GCM encoder in this test.
    /// </summary>
    [Fact]
    public void UnprotectReadsLegacyNonceTagCipherLayout()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "backup.key"), SyntheticKey);
        var plaintext = Encoding.UTF8.GetBytes("synthetic-legacy-read-probe");
        var nonce = new byte[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB };
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(SyntheticKey, 16))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag);
        }

        var sealedBytes = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(sealedBytes, 0);
        tag.CopyTo(sealedBytes, 12);
        cipher.CopyTo(sealedBytes, 28);

        var plain = new UnixSecretStore(temp.Path).Unprotect(sealedBytes);

        Assert.Equal(plaintext, plain);
    }

    /// <summary>Catches nonce reuse (fixed nonce), which breaks AES-GCM confidentiality.</summary>
    [Fact]
    public void ProtectUsesFreshNoncePerCall()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "backup.key"), SyntheticKey);
        var store = new UnixSecretStore(temp.Path);
        var plaintext = Encoding.UTF8.GetBytes("synthetic-nonce-reuse-probe");

        var first = store.Protect(plaintext);
        var second = store.Protect(plaintext);

        Assert.NotEqual(first, second);
        Assert.Equal(plaintext, store.Unprotect(first));
        Assert.Equal(plaintext, store.Unprotect(second));
    }

    // ── damaged ciphertext must fail closed ──────────────────────────────────────────────

    /// <summary>
    /// Catches swallowing an authentication-tag mismatch and returning garbage. AesGcm throws
    /// the derived AuthenticationTagMismatchException, so the assertion must accept subtypes.
    /// </summary>
    [Fact]
    public void TamperedCiphertextIsRejected()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "backup.key"), SyntheticKey);
        var store = new UnixSecretStore(temp.Path);
        var sealedBytes = store.Protect(Encoding.UTF8.GetBytes("synthetic-tamper-probe"));
        sealedBytes[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => { _ = store.Unprotect(sealedBytes); });
    }

    /// <summary>Catches an empty/short input silently succeeding instead of failing.</summary>
    [Fact]
    public void TruncatedCiphertextIsRejected()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "backup.key"), SyntheticKey);
        var store = new UnixSecretStore(temp.Path);

        Assert.ThrowsAny<Exception>(() => { _ = store.Unprotect(new byte[] { 1, 2, 3 }); });
    }

    /// <summary>
    /// Catches allocating/reading a whole oversized file instead of a bounded 33-byte probe,
    /// and confirms the too-large key is refused rather than truncated.
    /// </summary>
    [Fact]
    public void OversizedKeyIsRejectedWithoutReplacement()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "backup.key");
        var oversized = new byte[1024 * 1024];
        Array.Fill(oversized, (byte)0x7E);
        File.WriteAllBytes(keyPath, oversized);

        Assert.Throws<CryptographicException>(() =>
        {
            _ = new UnixSecretStore(temp.Path).Protect(new byte[] { 42 });
        });

        Assert.Equal(oversized.Length, new FileInfo(keyPath).Length);
    }

    /// <summary>
    /// Catches Unprotect creating the private directory (or key) when the key is absent:
    /// existing ciphertext with a missing key must be reported, not "recovered".
    /// </summary>
    [Fact]
    public void UnprotectOnMissingDirectoryReportsMissingWithoutCreatingIt()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "missing-private");
        var store = new UnixSecretStore(missing);

        Assert.Throws<CryptographicException>(() => { _ = store.Unprotect(new byte[64]); });

        Assert.False(Directory.Exists(missing));
        Assert.False(File.Exists(store.KeyPath));
    }

    // ── Linux-only filesystem semantics (reported skipped elsewhere, never falsely green) ─

    /// <summary>Catches a key left group/other-readable or a private directory left 0755.</summary>
    [LinuxFact]
    [UnsupportedOSPlatform("windows")]
    public void KeyFileIs0600AndPrivateDirectoryIs0700()
    {
        using var temp = new TempDirectory();

        new UnixSecretStore(temp.Path).Protect(Encoding.UTF8.GetBytes("synthetic-permissions-probe"));

        var keyMode = File.GetUnixFileMode(Path.Combine(temp.Path, "backup.key"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, keyMode);
        var directoryMode = File.GetUnixFileMode(temp.Path);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            directoryMode);
    }

    /// <summary>
    /// Catches following an unidentified symlinked key and overwriting its target, or replacing
    /// the link with a rebuilt key.
    /// </summary>
    [LinuxFact]
    public void SymlinkedKeyTargetIsNotOverwritten()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "outside-target.key");
        File.WriteAllBytes(target, new byte[] { 9, 8, 7 });
        var keyPath = Path.Combine(temp.Path, "backup.key");
        File.CreateSymbolicLink(keyPath, target);

        Assert.ThrowsAny<Exception>(() =>
        {
            _ = new UnixSecretStore(temp.Path).Protect(new byte[] { 42 });
        });

        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(target));
        Assert.NotNull(File.ResolveLinkTarget(keyPath, returnFinalTarget: true));
    }

    /// <summary>
    /// Catches leaving an existing legacy key world/group-readable or its owned directory 0755
    /// on the encrypt path; key contents must be preserved while permissions are tightened.
    /// </summary>
    [LinuxFact]
    [UnsupportedOSPlatform("windows")]
    public void ExistingKeyPermissionsAreTightenedByProtect()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "backup.key");
        File.WriteAllBytes(keyPath, SyntheticKey);
        File.SetUnixFileMode(keyPath, LooseFileMode);
        File.SetUnixFileMode(temp.Path, LooseDirectoryMode);

        _ = new UnixSecretStore(temp.Path).Protect(Encoding.UTF8.GetBytes("synthetic-existing-key-probe"));

        Assert.Equal(PrivateFileMode, File.GetUnixFileMode(keyPath));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(temp.Path));
        Assert.Equal(SyntheticKey, File.ReadAllBytes(keyPath));
    }

    /// <summary>
    /// Catches the decrypt/read path bypassing private-directory/key restrictions: reuse must
    /// tighten permissions without replacing the existing key.
    /// </summary>
    [LinuxFact]
    [UnsupportedOSPlatform("windows")]
    public void ExistingKeyPermissionsAreTightenedByUnprotect()
    {
        using var temp = new TempDirectory();
        var keyPath = Path.Combine(temp.Path, "backup.key");
        File.WriteAllBytes(keyPath, SyntheticKey);
        File.SetUnixFileMode(keyPath, LooseFileMode);
        File.SetUnixFileMode(temp.Path, LooseDirectoryMode);
        var plaintext = Encoding.UTF8.GetBytes("synthetic-unprotect-perms-probe");
        var sealedBytes = SealLegacy(SyntheticKey, LegacyNonce, plaintext);

        var plain = new UnixSecretStore(temp.Path).Unprotect(sealedBytes);

        Assert.Equal(plaintext, plain);
        Assert.Equal(PrivateFileMode, File.GetUnixFileMode(keyPath));
        Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(temp.Path));
        Assert.Equal(SyntheticKey, File.ReadAllBytes(keyPath));
    }

    /// <summary>
    /// Catches chmod'ing through a linked private directory: the external target's mode and
    /// sentinel must be preserved and no key/temp file created inside it.
    /// </summary>
    [LinuxFact]
    [UnsupportedOSPlatform("windows")]
    public void LinkedPrivateDirectoryIsRejectedAndTargetUntouched()
    {
        using var temp = new TempDirectory();
        var external = Path.Combine(temp.Path, "external");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "external-sentinel");
        File.SetUnixFileMode(external, LooseDirectoryMode);
        var link = Path.Combine(temp.Path, "private-link");
        Directory.CreateSymbolicLink(link, external);
        var store = new UnixSecretStore(link);

        Assert.ThrowsAny<Exception>(() => { _ = store.Protect(new byte[] { 42 }); });
        Assert.ThrowsAny<Exception>(() => { _ = store.Unprotect(new byte[64]); });

        Assert.Equal("external-sentinel", File.ReadAllText(sentinel));
        Assert.Equal(LooseDirectoryMode, File.GetUnixFileMode(external));
        Assert.Empty(Directory.GetFiles(external, "backup.key"));
        Assert.Empty(Directory.GetFiles(external, "*.tmp"));
    }

    // ── BackupService must use injected protection and refuse unsuccessful backups ────────

    /// <summary>
    /// Catches Save/Read ignoring the injected protect/unprotect and falling back to global
    /// SecretProtector, which would make an explicit test directory unusable.
    /// </summary>
    [Fact]
    public void SaveAndReadUseInjectedProtection()
    {
        using var backupDir = new TempDirectory();
        var protectCalls = 0;
        var unprotectCalls = 0;
        var service = new BackupService(
            backupDir.Path,
            data => { protectCalls++; return Xor(data, 0x5A); },
            data => { unprotectCalls++; return Xor(data, 0x5A); });

        var path = service.Save(
            new JsonNode?[] { JsonValue.Create("synthetic-account") },
            "/synthetic/config.json",
            "synthetic-config-text");

        Assert.True(File.Exists(path));
        Assert.Equal(1, protectCalls);
        Assert.DoesNotContain("synthetic-config-text", Encoding.UTF8.GetString(File.ReadAllBytes(path)));

        var node = service.Read(path)!;

        Assert.Equal(1, unprotectCalls);
        Assert.Equal("synthetic-account", node["credentials"]![0]!.GetValue<string>());
        Assert.Equal("synthetic-config-text", node["configText"]!.GetValue<string>());
    }

    /// <summary>
    /// Catches reporting a backup as successful when protection failed: Save must throw and
    /// leave no partial file for callers to depend on.
    /// </summary>
    [Fact]
    public void SavePropagatesProtectionFailureAndWritesNoBackupFile()
    {
        using var backupDir = new TempDirectory();
        var service = new BackupService(
            backupDir.Path,
            _ => throw new CryptographicException("synthetic protection failure"),
            data => data);

        Assert.Throws<CryptographicException>(() =>
        {
            _ = service.Save(
                new JsonNode?[] { JsonValue.Create("synthetic-account") },
                null,
                null);
        });

        Assert.Empty(Directory.GetFiles(backupDir.Path));
    }

    /// <summary>Catches a backup file written world/group readable.</summary>
    [LinuxFact]
    [UnsupportedOSPlatform("windows")]
    public void BackupFileIs0600()
    {
        using var keyDir = new TempDirectory();
        using var backupDir = new TempDirectory();
        var store = new UnixSecretStore(keyDir.Path);
        var service = new BackupService(backupDir.Path, store.Protect, store.Unprotect);

        var path = service.Save(
            new JsonNode?[] { JsonValue.Create("synthetic-account") },
            null,
            null);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    /// <summary>Catches a backup directory left group/other accessible.</summary>
    [LinuxFact]
    [UnsupportedOSPlatform("windows")]
    public void BackupDirectoryIs0700()
    {
        using var backupDir = new TempDirectory();

        _ = new BackupService(backupDir.Path, data => data, data => data);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(backupDir.Path));
    }

    /// <summary>
    /// Catches Save writing into an owned directory that was swapped for a symlink after
    /// construction: the save must fail and the external target stay untouched.
    /// </summary>
    [LinuxFact]
    public void BackupSaveRejectsDirectoryReplacedByLink()
    {
        using var temp = new TempDirectory();
        var backupDir = Path.Combine(temp.Path, "backups");
        var service = new BackupService(backupDir, data => data, data => data);
        var external = Path.Combine(temp.Path, "external");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "external-sentinel");
        Directory.Delete(backupDir, recursive: true);
        Directory.CreateSymbolicLink(backupDir, external);

        Assert.ThrowsAny<Exception>(() =>
        {
            _ = service.Save(new JsonNode?[] { JsonValue.Create("synthetic-account") }, null, null);
        });

        Assert.Equal("external-sentinel", File.ReadAllText(sentinel));
        Assert.Empty(Directory.GetFiles(external, "*.enc"));
        Assert.Empty(Directory.GetFiles(external, "*.dpapi"));
    }

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // 0755 directory / 0644 file: the exposure the store must tighten away on reuse.
    private const UnixFileMode LooseDirectoryMode =
        PrivateDirectoryMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode LooseFileMode =
        PrivateFileMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly byte[] LegacyNonce =
    {
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB,
    };

    /// <summary>Independent nonce(12) || tag(16) || cipher encoder; not the code under test.</summary>
    private static byte[] SealLegacy(byte[] key, byte[] nonce, byte[] plaintext)
    {
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag);
        }

        var output = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, 12);
        cipher.CopyTo(output, 28);
        return output;
    }

    private static byte[] Xor(byte[] data, byte mask)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ mask);
        }

        return result;
    }
}

/// <summary>
/// Runs a test only on Linux. Elsewhere xUnit reports it as skipped, so Windows runs never
/// pretend that Unix permission/link semantics passed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Requires Linux for Unix permission and symlink semantics.";
        }
    }
}

/// <summary>Unique scratch directory under the process temp root; test-file-private helper.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "am-unixsecret-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
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
