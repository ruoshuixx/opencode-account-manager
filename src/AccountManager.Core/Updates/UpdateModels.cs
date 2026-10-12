namespace AccountManager.Core.Updates;

/// <summary>
/// A newer stable GitHub release. Asset URLs are canonical to the configured repository, so the
/// values can be trusted for display and download without re-parsing the remote payload.
/// <para>The platform is fixed by the local update service profile.</para>
/// </summary>
/// <param name="Version">Normalized three-part version (build defaults to 0).</param>
/// <param name="TagName">The strict stable tag, for example <c>v1.2.3</c>.</param>
/// <param name="Notes">Release body as plain text; callers must not treat it as markup.</param>
/// <param name="ReleasePage">The repository release page.</param>
/// <param name="DownloadUrl">Canonical release download URL of the executable asset.</param>
/// <param name="ChecksumUrl">Canonical release download URL of the <c>.sha256</c> asset.</param>
/// <param name="Size">Declared executable size in bytes; always positive.</param>
/// <param name="Platform">Platform that owns the executable/checksum asset pair.</param>
public sealed record UpdateRelease(
    Version Version,
    string TagName,
    string Notes,
    Uri ReleasePage,
    Uri DownloadUrl,
    Uri ChecksumUrl,
    long Size,
    UpdatePlatform Platform = UpdatePlatform.WindowsX64);

/// <summary>Download progress snapshot for an executable already constrained to the 200 MiB cap.</summary>
/// <param name="BytesReceived">Bytes written so far.</param>
/// <param name="TotalBytes">Expected total bytes from release metadata.</param>
public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    /// <summary>Completion percentage in the range 0-100; 0 when the total is unknown.</summary>
    public double Percent => TotalBytes > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes * 100d, 0d, 100d)
        : 0d;
}
