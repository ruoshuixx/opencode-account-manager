using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AccountManager.Core.Security;

/// <summary>
/// Protects local at-rest payloads. Uses Windows DPAPI (current user) on Windows; on other
/// platforms AES-GCM with a 0600 key file under the app data directory. The key material is
/// never logged or returned.
/// </summary>
public static class SecretProtector
{
    private static readonly Lazy<UnixSecretStore> DefaultUnixStore = new(
        static () => new UnixSecretStore(GetDefaultUnixDirectory()));

    public static byte[] Protect(byte[] data)
        => OperatingSystem.IsWindows() ? DpapiProtect(data) : DefaultUnixStore.Value.Protect(data);

    public static byte[] Unprotect(byte[] data)
        => OperatingSystem.IsWindows() ? DpapiUnprotect(data) : DefaultUnixStore.Value.Unprotect(data);

    /// <summary>Protects a short text secret (e.g. a stored password) as base64.</summary>
    public static string ProtectText(string text)
        => Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(text)));

    /// <summary>Unprotects text from <see cref="ProtectText"/>; null when empty or unreadable.</summary>
    public static string? UnprotectText(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) { return null; }
        try { return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(protectedBase64))); }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiProtect(byte[] data)
        => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiUnprotect(byte[] data)
        => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);

    /// <summary>
    /// Default Unix key directory, kept byte-for-byte compatible with the previous
    /// implementation: <c>~/.local/share/OpenCodeAccountImporter/backup.key</c>.
    /// </summary>
    private static string GetDefaultUnixDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "OpenCodeAccountImporter");
    }
}
