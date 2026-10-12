using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccountManager.Core.Updates;

/// <summary>
/// Read-only client for the public GitHub Releases API of the configured repository.
/// <para>
/// It only ever issues unauthenticated GET requests to <c>api.github.com</c> and
/// <c>github.com</c>. It carries no account, configuration or authentication state and never
/// touches <c>SecretRegistry</c>. Callers own the <see cref="HttpClient"/> (and therefore the
/// timeout / proxy / cancellation token).
/// </para>
/// </summary>
public sealed class GitHubUpdateService
{
    /// <summary>Fixed GitHub asset name of the Windows x64 executable.</summary>
    public const string ExecutableAssetName = "OpenCodeAccountManager-win-x64.exe";

    /// <summary>Fixed GitHub asset name of the sha256sum file for <see cref="ExecutableAssetName"/>.</summary>
    public const string ChecksumAssetName = "OpenCodeAccountManager-win-x64.exe.sha256";

    private const string UserAgent = "OpenCodeAccountManager-Updater";
    private const string ApiAccept = "application/vnd.github+json";
    private const long MaxExecutableBytes = 200L * 1024 * 1024;
    private const int MaxMetadataBytes = 1024 * 1024;
    private const int MaxChecksumBytes = 4 * 1024;
    private const int CopyBufferSize = 81920;
    private const string PublicReleaseNotes = "已通过公开发布页面检查更新。";
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly HttpClient http;
    private readonly string owner;
    private readonly string repo;
    private readonly UpdatePlatform platform;
    private readonly UpdateAssetProfile assetProfile;

    public GitHubUpdateService(
        HttpClient http,
        string owner,
        string repo,
        UpdatePlatform platform = UpdatePlatform.WindowsX64)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
        this.owner = owner;
        this.repo = repo;
        this.platform = platform;
        assetProfile = UpdateAssetProfile.For(platform);
    }

    /// <summary>
    /// Fetches the latest stable release and returns it when it is strictly newer than
    /// <paramref name="currentVersion"/>; otherwise (including 404, drafts, prereleases, bad tags
    /// or missing/mistrusted assets) returns <see langword="null"/>.
    /// </summary>
    public async Task<UpdateRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        var latestUri = new Uri($"https://api.github.com/repos/{owner}/{repo}/releases/latest", UriKind.Absolute);
        HttpStatusCode statusCode;
        var succeeded = false;
        byte[]? payload = null;

        using (var request = CreateRequest(latestUri, isApi: true))
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            statusCode = response.StatusCode;
            succeeded = response.IsSuccessStatusCode;
            if (succeeded)
            {
                payload = await ReadCappedAsync(response.Content, MaxMetadataBytes, cancellationToken).ConfigureAwait(false);
            }
        }

        // The anonymous GitHub API allows only 60 requests/hour per IP. When that budget is spent
        // (403) or the caller is explicitly throttled (429), fall back to the public release page,
        // which requires no authentication and no API quota.
        if (statusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            return await CheckViaPublicReleasePageAsync(currentVersion, cancellationToken).ConfigureAwait(false);
        }
        if (statusCode == HttpStatusCode.NotFound) { return null; }
        if (!succeeded || payload is null)
        {
            throw new HttpRequestException($"检查更新失败：HTTP {(int)statusCode}。");
        }
        return ParseLatest(payload, currentVersion);
    }

    /// <summary>
    /// Streams the release executable into a fresh, uniquely named staging directory inside
    /// <paramref name="destinationDirectory"/>, verifying size and SHA256 before returning the
    /// absolute path. On failure it removes this call's known payload and then removes the staging
    /// directory only when empty; unknown entries and links are never recursively deleted.
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateRelease release,
        string destinationDirectory,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!TryParseStableTag(release.TagName, out var tagVersion))
        {
            throw new InvalidDataException("发布记录的标签不是稳定版本标签。");
        }
        if (Normalize(release.Version) != Normalize(tagVersion))
        {
            throw new InvalidDataException("发布记录的版本与其标签不一致。");
        }
        if (release.Size <= 0) { throw new InvalidDataException("发布资产的长度未知，无法进行完整性校验。"); }
        if (release.Size > MaxExecutableBytes) { throw new InvalidDataException("发布资产超过 200 MiB 上限。"); }
        if (release.Platform != platform)
        {
            throw new InvalidDataException("发布记录的平台与更新服务配置不一致。");
        }
        if (!SameUri(release.DownloadUrl, ExpectedDownloadUri(release.TagName, assetProfile.ExecutableAssetNameFor(release.TagName))))
        {
            throw new InvalidDataException("下载地址不是受信任的 GitHub 发布资产。");
        }
        if (!SameUri(release.ChecksumUrl, ExpectedDownloadUri(release.TagName, assetProfile.ChecksumAssetNameFor(release.TagName))))
        {
            throw new InvalidDataException("校验和地址不是受信任的 GitHub 发布资产。");
        }

        var expectedHash = await FetchChecksumAsync(release.ChecksumUrl, assetProfile.ExecutableAssetNameFor(release.TagName), cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(destinationDirectory);
        var stagingDirectory = Path.Combine(destinationDirectory, "update-" + Guid.NewGuid().ToString("N"));
        var targetPath = Path.Combine(stagingDirectory, assetProfile.ExecutableAssetNameFor(release.TagName));
        StagingOwnership? stagingOwnership = null;
        var downloadedArtifact = new DownloadedArtifact();

        try
        {
            stagingOwnership = CreateOwnedStagingDirectory(stagingDirectory);

            await DownloadVerifiedAsync(
                release,
                targetPath,
                expectedHash,
                progress,
                stagingOwnership,
                downloadedArtifact,
                cancellationToken).ConfigureAwait(false);
            if (!IsVerifiedDownloadedExecutable(stagingOwnership, targetPath, downloadedArtifact, release.Size, expectedHash))
            {
                throw new InvalidDataException(assetProfile.IsDeb
                    ? "下载的文件已被替换或不是有效的 .deb 安装包。"
                    : assetProfile.IsLinux
                        ? "下载的文件已被替换或不是有效的 Linux x64 ELF 可执行文件。"
                        : "下载的文件已被替换或不是有效的 Windows 可执行文件。");
            }

            RemoveStagingOwnershipMarker(stagingOwnership);
            return Path.GetFullPath(targetPath);
        }
        catch
        {
            if (stagingOwnership is not null) { TryCleanupStagingDirectory(stagingOwnership, targetPath, downloadedArtifact); }
            throw;
        }
    }

    /// <summary>
    /// Unauthenticated fallback used only when the API is rate limited (403/429). It derives the
    /// newest version from the public <c>/releases/latest</c> redirect and confirms the two fixed
    /// public assets with HEAD requests. No cookies, tokens or account state are involved.
    /// </summary>
    private async Task<UpdateRelease?> CheckViaPublicReleasePageAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        var latestPage = new Uri($"https://github.com/{owner}/{repo}/releases/latest", UriKind.Absolute);

        var tag = string.Empty;
        var version = new Version(0, 0, 0);
        var releasePage = latestPage;

        using (var request = CreateRequest(latestPage, isApi: false))
        {
            request.Method = HttpMethod.Head;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) { return null; }
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"检查更新失败：HTTP {(int)response.StatusCode}。");
            }

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null) { return null; }
            if (!TryParsePublicReleasePage(finalUri, out tag, out version, out releasePage)) { return null; }
            if (Normalize(version) <= Normalize(currentVersion)) { return null; }
        }

        var downloadUri = ExpectedDownloadUri(tag, assetProfile.ExecutableAssetNameFor(tag));
        var checksumUri = ExpectedDownloadUri(tag, assetProfile.ChecksumAssetNameFor(tag));

        var size = await HeadExecutableSizeAsync(downloadUri, cancellationToken).ConfigureAwait(false);
        if (size is null) { return null; }
        if (!await HeadChecksumExistsAsync(checksumUri, cancellationToken).ConfigureAwait(false)) { return null; }

        return new UpdateRelease(
            Normalize(version),
            tag,
            PublicReleaseNotes,
            releasePage,
            downloadUri,
            checksumUri,
            size.Value,
            platform);
    }

    private async Task<long?> HeadExecutableSizeAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri, isApi: false);
        request.Method = HttpMethod.Head;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) { return null; }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"检查发布资产失败：HTTP {(int)response.StatusCode}。");
        }

        var declared = response.Content.Headers.ContentLength;
        // Unknown length cannot be verified against the downloaded bytes, so no update is offered.
        if (declared is not long length || length <= 0 || length > MaxExecutableBytes) { return null; }
        return length;
    }

    private async Task<bool> HeadChecksumExistsAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(uri, isApi: false);
        request.Method = HttpMethod.Head;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) { return false; }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"检查发布资产失败：HTTP {(int)response.StatusCode}。");
        }

        var declared = response.Content.Headers.ContentLength;
        if (declared is long length && (length <= 0 || length > MaxChecksumBytes)) { return false; }
        return true;
    }

    private async Task DownloadVerifiedAsync(
        UpdateRelease release,
        string targetPath,
        string expectedHash,
        IProgress<UpdateDownloadProgress>? progress,
        StagingOwnership stagingOwnership,
        DownloadedArtifact downloadedArtifact,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(release.DownloadUrl, isApi: false);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"下载更新失败：HTTP {(int)response.StatusCode}。");
        }

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is long declared && declared != release.Size)
        {
            throw new InvalidDataException("响应长度与发布信息不一致。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        EnsureOwnedStagingDirectory(stagingOwnership);
        EnsurePathAbsent(targetPath);
        await using var destination = CreateStagingFile(targetPath, downloadedArtifact);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[CopyBufferSize];
        long received = 0;
        int read;
        var digestCaptured = false;
        try
        {
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > release.Size || received > MaxExecutableBytes)
                {
                    throw new InvalidDataException("下载内容超过发布的长度。");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                downloadedArtifact.BytesWritten += read;
                progress?.Report(new UpdateDownloadProgress(received, release.Size));
            }

            var actualHash = sha.GetHashAndReset();
            downloadedArtifact.StreamHash = actualHash;
            digestCaptured = true;
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (received != release.Size)
            {
                throw new InvalidDataException("下载被截断，长度与发布信息不一致。");
            }

            var expectedBytes = Convert.FromHexString(expectedHash);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedBytes))
            {
                throw new InvalidDataException("SHA256 校验失败。");
            }
        }
        catch
        {
            if (!digestCaptured) { downloadedArtifact.StreamHash = sha.GetHashAndReset(); }
            throw;
        }
    }

    private async Task<string> FetchChecksumAsync(Uri checksumUri, string expectedFileName, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(checksumUri, isApi: false);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"下载校验和失败：HTTP {(int)response.StatusCode}。");
        }

        var bytes = await ReadCappedAsync(response.Content, MaxChecksumBytes, cancellationToken).ConfigureAwait(false);
        return ParseChecksum(Encoding.UTF8.GetString(bytes), expectedFileName);
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is long declared && declared > maxBytes)
        {
            throw new InvalidDataException("响应元数据超过允许的大小。");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("响应元数据超过允许的大小。");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private UpdateRelease? ParseLatest(byte[] payload, Version currentVersion)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { return null; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return null; }
            if (GetBool(root, "draft") || GetBool(root, "prerelease")) { return null; }

            var tag = GetString(root, "tag_name");
            if (string.IsNullOrEmpty(tag) || !TryParseStableTag(tag, out var version)) { return null; }
            if (Normalize(version) <= Normalize(currentVersion)) { return null; }

            var pageText = GetString(root, "html_url");
            if (pageText is null || !TryCreateHttpsUri(pageText, out var releasePage)) { return null; }
            if (!SameUri(releasePage, ExpectedReleasePage(tag))) { return null; }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) { return null; }

            JsonElement? exeAsset = null;
            JsonElement? checksumAsset = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) { continue; }
                switch (GetString(asset, "name"))
                {
                    case var name when string.Equals(name, assetProfile.ExecutableAssetNameFor(tag), StringComparison.Ordinal): exeAsset = asset; break;
                    case var name when string.Equals(name, assetProfile.ChecksumAssetNameFor(tag), StringComparison.Ordinal): checksumAsset = asset; break;
                }
            }
            if (exeAsset is null || checksumAsset is null) { return null; }

            var exeText = GetString(exeAsset.Value, "browser_download_url");
            if (exeText is null || !TryCreateHttpsUri(exeText, out var exeUri)) { return null; }
            if (!SameUri(exeUri, ExpectedDownloadUri(tag, assetProfile.ExecutableAssetNameFor(tag)))) { return null; }

            var checksumText = GetString(checksumAsset.Value, "browser_download_url");
            if (checksumText is null || !TryCreateHttpsUri(checksumText, out var checksumUri)) { return null; }
            if (!SameUri(checksumUri, ExpectedDownloadUri(tag, assetProfile.ChecksumAssetNameFor(tag)))) { return null; }

            var size = GetLong(exeAsset.Value, "size");
            if (size <= 0 || size > MaxExecutableBytes) { return null; }

            return new UpdateRelease(
                Normalize(version),
                tag,
                GetString(root, "body") ?? string.Empty,
                releasePage,
                exeUri,
                checksumUri,
                size,
                platform);
        }
    }

    private HttpRequestMessage CreateRequest(Uri uri, bool isApi)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (isApi) { request.Headers.Accept.ParseAdd(ApiAccept); }
        return request;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static long GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt64(out var value)
            ? value
            : 0;

    private Uri ExpectedDownloadUri(string tag, string assetName)
        => new($"https://github.com/{owner}/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{assetName}", UriKind.Absolute);

    private Uri ExpectedReleasePage(string tag)
        => new($"https://github.com/{owner}/{repo}/releases/tag/{Uri.EscapeDataString(tag)}", UriKind.Absolute);

    private static bool SameUri(Uri left, Uri right)
        => left.Scheme == right.Scheme
           && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
           && left.Port == right.Port
           && left.UserInfo.Length == 0
           && left.Query.Length == 0
           && left.Fragment.Length == 0
           && right.UserInfo.Length == 0
           && right.Query.Length == 0
           && right.Fragment.Length == 0
           && string.Equals(left.AbsolutePath, right.AbsolutePath, StringComparison.Ordinal);

    private static Version Normalize(Version version)
        => new(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);

    private static bool TryParseStableTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrEmpty(tag) || tag.Length < 6 || tag[0] != 'v') { return false; }

        var parts = tag[1..].Split('.');
        if (parts.Length != 3) { return false; }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || (part.Length > 1 && part[0] == '0')) { return false; }
            foreach (var c in part)
            {
                if (c is < '0' or > '9') { return false; }
            }
            if (!int.TryParse(part, out numbers[i])) { return false; }
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    private bool TryParsePublicReleasePage(Uri uri, out string tag, out Version version, out Uri releasePage)
    {
        tag = string.Empty;
        version = new Version(0, 0, 0);
        releasePage = uri;

        if (uri.Scheme != Uri.UriSchemeHttps) { return false; }
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) { return false; }
        if (uri.Port != 443) { return false; }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) { return false; }

        var prefix = $"/{owner}/{repo}/releases/tag/";
        var path = uri.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) { return false; }

        var candidate = path[prefix.Length..];
        if (candidate.Length == 0 || candidate.Contains('/')) { return false; }
        if (!TryParseStableTag(candidate, out version)) { return false; }

        tag = candidate;
        releasePage = uri;
        return true;
    }

    private static bool TryCreateHttpsUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) { return false; }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) { return false; }
        if (parsed.Scheme != Uri.UriSchemeHttps) { return false; }
        uri = parsed;
        return true;
    }

    private static string ParseChecksum(string content, string expectedFileName)
    {
        var line = content.TrimStart('\uFEFF').Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')[0].Trim();
        var separator = line.IndexOf("  ", StringComparison.Ordinal);
        if (separator <= 0) { throw new InvalidDataException("校验和格式无效。"); }

        var hash = line[..separator];
        var name = line[(separator + 2)..].Trim();
        if (!string.Equals(name, expectedFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("校验和对应的文件名不匹配。");
        }
        if (hash.Length != 64 || !IsHex(hash))
        {
            throw new InvalidDataException("校验和不是有效的 SHA256。");
        }
        return hash.ToLowerInvariant();
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))) { return false; }
        }
        return true;
    }

    private bool IsVerifiedDownloadedExecutable(
        StagingOwnership stagingOwnership,
        string path,
        DownloadedArtifact artifact,
        long expectedSize,
        string expectedHash)
    {
        EnsureOwnedStagingDirectory(stagingOwnership);
        EnsureRegularFilePath(path);
        if (!artifact.WasCreatedByCall || artifact.StreamHash is null) { return false; }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != expectedSize || stream.Length != artifact.BytesWritten || stream.Length > MaxExecutableBytes)
            {
                return false;
            }

            var fileHash = SHA256.HashData(stream);
            var declaredHash = Convert.FromHexString(expectedHash);
            if (!CryptographicOperations.FixedTimeEquals(fileHash, artifact.StreamHash)
                || !CryptographicOperations.FixedTimeEquals(fileHash, declaredHash))
            {
                return false;
            }

            stream.Position = 0;
            if (!assetProfile.IsLinux)
            {
                return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
            }

            Span<byte> header = stackalloc byte[64];
            var received = 0;
            while (received < header.Length)
            {
                var read = stream.Read(header[received..]);
                if (read == 0) { break; }
                received += read;
            }
            return assetProfile.IsDeb
                ? ExecutableFormat.IsDebPackage(header[..received])
                : ExecutableFormat.IsLinuxX64(header[..received]);
        }
        catch (IOException) { return false; }
    }

    private FileStream CreateStagingFile(string path, DownloadedArtifact artifact)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = CopyBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (assetProfile.IsLinux && !OperatingSystem.IsWindows()) { options.UnixCreateMode = PrivateFileMode; }

        var stream = new FileStream(path, options);
        artifact.WasCreatedByCall = true;
        artifact.StreamHash = SHA256.HashData(Array.Empty<byte>());
        if (!assetProfile.IsLinux || OperatingSystem.IsWindows()) { return stream; }

        try
        {
            EnsurePrivateFile(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private StagingOwnership CreateOwnedStagingDirectory(string path)
    {
        EnsurePathAbsent(path);
        if (assetProfile.IsLinux && !OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
        }
        else
        {
            Directory.CreateDirectory(path);
        }

        EnsureSafeStagingDirectory(path);
        if (assetProfile.IsLinux && !OperatingSystem.IsWindows()) { EnsurePrivateDirectory(path); }

        var ownership = new StagingOwnership(
            path,
            Path.Combine(path, ".update-owner"),
            Guid.NewGuid().ToString("N"));
        var markerOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (assetProfile.IsLinux && !OperatingSystem.IsWindows()) { markerOptions.UnixCreateMode = PrivateFileMode; }

        using (var marker = new FileStream(ownership.MarkerPath, markerOptions))
        {
            var token = Encoding.ASCII.GetBytes(ownership.Token);
            marker.Write(token);
            marker.Flush();
        }
        if (assetProfile.IsLinux && !OperatingSystem.IsWindows()) { EnsurePrivateFile(ownership.MarkerPath); }
        EnsureOwnedStagingDirectory(ownership);
        return ownership;
    }

    private void EnsureOwnedStagingDirectory(StagingOwnership ownership)
    {
        try
        {
            EnsureSafeStagingDirectory(ownership.DirectoryPath);
            // Ownership first: a replaced directory has no marker and must be reported as replaced, not as a
            // permission problem. The directory mode is checked only after the marker proves identity.
            EnsureRegularFilePath(ownership.MarkerPath);
            if (assetProfile.IsLinux && !OperatingSystem.IsWindows()) { EnsurePrivateFile(ownership.MarkerPath); }

            var markerToken = File.ReadAllText(ownership.MarkerPath, Encoding.ASCII);
            if (!string.Equals(markerToken, ownership.Token, StringComparison.Ordinal))
            {
                throw new InvalidDataException("更新暂存目录的所有权标记已改变。");
            }

            if (assetProfile.IsLinux && !OperatingSystem.IsWindows()) { EnsurePrivateDirectory(ownership.DirectoryPath); }
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidDataException("更新暂存目录已被替换或移除。", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new InvalidDataException("更新暂存目录已被替换或移除。", exception);
        }
    }

    private static void EnsurePathAbsent(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            throw new IOException("更新暂存路径已存在。");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void EnsureSafeStagingDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("更新暂存目录不是安全的普通目录。");
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Unix directory permissions are unavailable on Windows."); }
        EnsureSafeStagingDirectory(path);
        if (File.GetUnixFileMode(path) != PrivateDirectoryMode)
        {
            throw new UnauthorizedAccessException("无法以 0700 权限创建更新暂存目录。");
        }
    }

    private static void EnsurePrivateFile(string path)
    {
        if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Unix file permissions are unavailable on Windows."); }
        EnsureRegularFilePath(path);
        if (File.GetUnixFileMode(path) != PrivateFileMode)
        {
            throw new UnauthorizedAccessException("无法以 0600 权限创建更新暂存文件。");
        }
    }

    private static void EnsureRegularFilePath(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("更新暂存文件不是安全的普通文件。");
        }
    }

    private void RemoveStagingOwnershipMarker(StagingOwnership ownership)
    {
        EnsureOwnedStagingDirectory(ownership);
        File.Delete(ownership.MarkerPath);
    }

    private static bool FileMatchesDownloadedBytes(string path, DownloadedArtifact artifact)
    {
        if (!artifact.WasCreatedByCall || artifact.StreamHash is null) { return false; }

        try
        {
            EnsureRegularFilePath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != artifact.BytesWritten || stream.Length > MaxExecutableBytes) { return false; }
            var currentHash = SHA256.HashData(stream);
            return CryptographicOperations.FixedTimeEquals(currentHash, artifact.StreamHash);
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
    }

    private void TryCleanupStagingDirectory(StagingOwnership ownership, string payloadPath, DownloadedArtifact artifact)
    {
        try
        {
            EnsureOwnedStagingDirectory(ownership);
            if (FileMatchesDownloadedBytes(payloadPath, artifact)) { File.Delete(payloadPath); }

            EnsureOwnedStagingDirectory(ownership);
            File.Delete(ownership.MarkerPath);
            EnsureSafeStagingDirectory(ownership.DirectoryPath);
            if (Directory.GetFileSystemEntries(ownership.DirectoryPath).Length == 0)
            {
                Directory.Delete(ownership.DirectoryPath, recursive: false);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
    }

    private sealed class StagingOwnership(string directoryPath, string markerPath, string token)
    {
        internal string DirectoryPath { get; } = directoryPath;

        internal string MarkerPath { get; } = markerPath;

        internal string Token { get; } = token;
    }

    private sealed class DownloadedArtifact
    {
        internal bool WasCreatedByCall { get; set; }

        internal long BytesWritten { get; set; }

        internal byte[]? StreamHash { get; set; }
    }
}
