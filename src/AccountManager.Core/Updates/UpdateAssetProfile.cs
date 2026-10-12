namespace AccountManager.Core.Updates;

/// <summary>The supported fixed update targets. Asset names are selected locally, never remotely.</summary>
public enum UpdatePlatform
{
    WindowsX64,
    LinuxX64,
    /// <summary>The .deb package of a system-wide Ubuntu/Debian installation.</summary>
    LinuxDeb,
}

/// <summary>A fixed executable/checksum pair for one supported update platform.</summary>
internal sealed class UpdateAssetProfile
{
    private static readonly UpdateAssetProfile Windows = new(
        UpdatePlatform.WindowsX64,
        GitHubUpdateService.ExecutableAssetName,
        GitHubUpdateService.ChecksumAssetName);

    private static readonly UpdateAssetProfile Linux = new(
        UpdatePlatform.LinuxX64,
        "OpenCodeAccountManager-linux-x64",
        "OpenCodeAccountManager-linux-x64.sha256");

    // The .deb file name carries the version, so it is derived from the release tag.
    private static readonly UpdateAssetProfile Deb = new(UpdatePlatform.LinuxDeb, string.Empty, string.Empty);

    private UpdateAssetProfile(UpdatePlatform platform, string executableAssetName, string checksumAssetName)
    {
        Platform = platform;
        ExecutableAssetName = executableAssetName;
        ChecksumAssetName = checksumAssetName;
    }

    internal UpdatePlatform Platform { get; }

    internal string ExecutableAssetName { get; }

    internal string ChecksumAssetName { get; }

    internal bool IsLinux => Platform is UpdatePlatform.LinuxX64 or UpdatePlatform.LinuxDeb;

    internal bool IsDeb => Platform == UpdatePlatform.LinuxDeb;

    /// <summary>Executable (or package) asset name for a stable tag such as <c>v1.2.3</c>.</summary>
    internal string ExecutableAssetNameFor(string tag)
        => IsDeb ? "opencode-account-manager_" + TagVersion(tag) + "_amd64.deb" : ExecutableAssetName;

    /// <summary>The sha256sum file that accompanies <see cref="ExecutableAssetNameFor"/>.</summary>
    internal string ChecksumAssetNameFor(string tag)
        => IsDeb ? ExecutableAssetNameFor(tag) + ".sha256" : ChecksumAssetName;

    private static string TagVersion(string tag) => tag.StartsWith('v') ? tag[1..] : tag;

    internal static UpdateAssetProfile For(UpdatePlatform platform)
        => platform switch
        {
            UpdatePlatform.WindowsX64 => Windows,
            UpdatePlatform.LinuxX64 => Linux,
            UpdatePlatform.LinuxDeb => Deb,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported update platform."),
        };
}
