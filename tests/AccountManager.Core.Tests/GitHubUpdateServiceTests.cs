using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Updates;
using Xunit;

namespace AccountManager.Core.Tests;

public class GitHubUpdateServiceTests
{
    private const string Owner = "ruoshuixx";
    private const string Repo = "opencode-account-manager";
    private const string ExeName = "OpenCodeAccountManager-win-x64.exe";
    private const string ShaName = "OpenCodeAccountManager-win-x64.exe.sha256";

    private static readonly byte[] Payload = { (byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04 };

    private static string HashHex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static bool IsChecksumRequest(HttpRequestMessage request)
        => request.RequestUri!.AbsolutePath.EndsWith(ShaName, StringComparison.Ordinal);

    private static (GitHubUpdateService Service, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var http = new HttpClient(handler);
        return (new GitHubUpdateService(http, Owner, Repo), handler);
    }

    private static string ReleaseJson(
        string tag = "v1.2.3",
        bool prerelease = false,
        bool draft = false,
        long size = 9,
        bool includeExe = true,
        bool includeSha = true,
        string? exeUrl = null,
        string? shaUrl = null,
        string? pageUrl = null,
        string body = "Release notes")
    {
        var exe = exeUrl ?? $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{ExeName}";
        var sha = shaUrl ?? $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{ShaName}";
        var page = pageUrl ?? $"https://github.com/{Owner}/{Repo}/releases/tag/{tag}";

        var assets = new JsonArray();
        if (includeExe) { assets.Add(new JsonObject { ["name"] = ExeName, ["size"] = size, ["browser_download_url"] = exe }); }
        if (includeSha) { assets.Add(new JsonObject { ["name"] = ShaName, ["size"] = 80, ["browser_download_url"] = sha }); }

        var root = new JsonObject
        {
            ["tag_name"] = tag,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["html_url"] = page,
            ["body"] = body,
            ["assets"] = assets,
        };
        return root.ToJsonString();
    }

    private static UpdateRelease MakeRelease(
        string tag = "v1.2.3",
        long size = 9,
        string? downloadUrl = null,
        string? checksumUrl = null,
        string? pageUrl = null)
        => new(
            new Version(1, 2, 3),
            tag,
            "notes",
            new Uri(pageUrl ?? $"https://github.com/{Owner}/{Repo}/releases/tag/{tag}"),
            new Uri(downloadUrl ?? $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{ExeName}"),
            new Uri(checksumUrl ?? $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{ShaName}"),
            size);

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)) };

    private static HttpResponseMessage TextResponse(string text)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };

    private static HttpResponseMessage BytesResponse(byte[] data)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    [Fact]
    public async Task Check_NewerStableRelease_ReturnsValidatedRelease()
    {
        var (service, handler) = Create(_ => JsonResponse(ReleaseJson(size: Payload.Length, body: "hello notes")));

        var release = await service.CheckAsync(new Version(1, 0, 0));

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 3), release!.Version);
        Assert.Equal("v1.2.3", release.TagName);
        Assert.Equal("hello notes", release.Notes);
        Assert.Equal(Payload.Length, release.Size);
        Assert.Equal($"https://github.com/{Owner}/{Repo}/releases/tag/v1.2.3", release.ReleasePage.AbsoluteUri);
        Assert.Equal($"https://github.com/{Owner}/{Repo}/releases/download/v1.2.3/{ExeName}", release.DownloadUrl.AbsoluteUri);
        Assert.Equal($"https://github.com/{Owner}/{Repo}/releases/download/v1.2.3/{ShaName}", release.ChecksumUrl.AbsoluteUri);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", request.RequestUri!.AbsoluteUri);
        Assert.Contains("Updater", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString(), StringComparison.Ordinal);
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(1, 2, 4)]
    [InlineData(2, 0, 0)]
    public async Task Check_NotNewer_ReturnsNull(int major, int minor, int build)
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(tag: "v1.2.3")));
        Assert.Null(await service.CheckAsync(new Version(major, minor, build)));
    }

    [Fact]
    public async Task Check_NormalizesMissingBuildComponent()
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(tag: "v1.2.0")));
        Assert.Null(await service.CheckAsync(new Version(1, 2)));
    }

    [Fact]
    public async Task Check_Prerelease_ReturnsNull()
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(prerelease: true)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_Draft_ReturnsNull()
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(draft: true)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Theory]
    [InlineData("v1.2")]
    [InlineData("1.2.3")]
    [InlineData("v1.2.3-beta")]
    [InlineData("v1.2.3.4")]
    [InlineData("release-v1.2.3")]
    [InlineData("v01.2.3")]
    [InlineData("")]
    public async Task Check_NonStableTag_ReturnsNull(string tag)
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(tag: tag)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_MissingExecutableAsset_ReturnsNull()
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(includeExe: false)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_MissingChecksumAsset_ReturnsNull()
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(includeSha: false)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_UnknownAssetSize_ReturnsNull()
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(size: 0)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_AssetOnForeignHost_ReturnsNull()
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(exeUrl: "https://evil.example/OpenCodeAccountManager-win-x64.exe")));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_AssetOutsideRepository_ReturnsNull()
    {
        var foreign = $"https://github.com/someone-else/other/releases/download/v1.2.3/{ExeName}";
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(exeUrl: foreign)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_ChecksumOnForeignHost_ReturnsNull()
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(shaUrl: "http://evil.example/OpenCodeAccountManager-win-x64.exe.sha256")));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_ReleasePageOutsideRepository_ReturnsNull()
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(pageUrl: "https://github.com/attacker/repo/releases/tag/v1.2.3")));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_NotFound_ReturnsNull()
    {
        var (service, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_ServerError_Throws()
    {
        var (service, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_TransportFailure_Throws()
    {
        var (service, _) = Create(_ => throw new HttpRequestException("no network"));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_OversizedMetadata_Throws()
    {
        var (service, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[(1024 * 1024) + 1]),
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Download_ValidAsset_ReturnsVerifiedAbsolutePathAndKeepsOtherFiles()
    {
        var hash = HashHex(Payload);
        var (service, handler) = Create(request => IsChecksumRequest(request)
            ? TextResponse($"{hash}  {ExeName}")
            : BytesResponse(Payload));

        using var dir = new TempDirectory();
        var unrelated = System.IO.Path.Combine(dir.Path, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep");

        var result = await service.DownloadAsync(MakeRelease(size: Payload.Length), dir.Path);

        Assert.True(System.IO.Path.IsPathRooted(result));
        Assert.Equal(ExeName, System.IO.Path.GetFileName(result));
        Assert.True(File.Exists(result));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result));
        Assert.True(File.Exists(unrelated));
        Assert.StartsWith(System.IO.Path.GetFullPath(dir.Path), System.IO.Path.GetFullPath(result), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Download_ReportsProgress()
    {
        var hash = HashHex(Payload);
        var (service, _) = Create(request => IsChecksumRequest(request)
            ? TextResponse($"{hash}  {ExeName}")
            : BytesResponse(Payload));

        using var dir = new TempDirectory();
        var progress = new ListProgress<UpdateDownloadProgress>();

        await service.DownloadAsync(MakeRelease(size: Payload.Length), dir.Path, progress);

        var last = Assert.Single(progress.Items);
        Assert.Equal(Payload.Length, last.BytesReceived);
        Assert.Equal(Payload.Length, last.TotalBytes);
        Assert.Equal(100d, last.Percent);
    }

    [Fact]
    public async Task Download_ChecksumMissingSeparator_Throws()
    {
        var (service, _) = Create(_ => TextResponse($"{new string('a', 64)}{ShaName}"));
        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_ChecksumWrongFileName_Throws()
    {
        var (service, _) = Create(_ => TextResponse($"{new string('a', 64)}  other.exe"));
        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_ChecksumNotHex_Throws()
    {
        var (service, _) = Create(_ => TextResponse($"{new string('z', 64)}  {ExeName}"));
        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_ChecksumTooShort_Throws()
    {
        var (service, _) = Create(_ => TextResponse($"abc123  {ExeName}"));
        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_ChecksumHttpError_Throws()
    {
        var (service, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadAsync(MakeRelease(), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_OversizedChecksum_Throws()
    {
        var (service, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[(4 * 1024) + 1]),
        });
        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_HashMismatch_DeletesStagingAndThrows()
    {
        var wrongHash = new string('0', 64);
        var (service, _) = Create(request => IsChecksumRequest(request)
            ? TextResponse($"{wrongHash}  {ExeName}")
            : BytesResponse(Payload));

        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(size: Payload.Length), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_TruncatedBody_DeletesStagingAndThrows()
    {
        var hash = HashHex(Payload);
        var (service, _) = Create(request => IsChecksumRequest(request)
            ? TextResponse($"{hash}  {ExeName}")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new NoLengthStreamContent(new MemoryStream(Payload[..4])),
            });

        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(size: Payload.Length), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_ExecutableHttpError_DeletesStagingAndThrows()
    {
        var hash = HashHex(Payload);
        var (service, _) = Create(request => IsChecksumRequest(request)
            ? TextResponse($"{hash}  {ExeName}")
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadAsync(MakeRelease(size: Payload.Length), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_BrokenStream_DeletesStagingAndThrows()
    {
        var hash = HashHex(Payload);
        var (service, _) = Create(request => IsChecksumRequest(request)
            ? TextResponse($"{hash}  {ExeName}")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new NoLengthStreamContent(new FaultingStream(Payload[..3], new IOException("connection reset"))),
            });

        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(MakeRelease(size: Payload.Length), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_CancelledMidStream_DeletesStagingAndThrows()
    {
        var hash = HashHex(Payload);
        using var cts = new CancellationTokenSource();
        var (service, _) = Create(request => IsChecksumRequest(request)
            ? TextResponse($"{hash}  {ExeName}")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new NoLengthStreamContent(new FaultingStream(Payload[..3], new OperationCanceledException(cts.Token))),
            });

        using var dir = new TempDirectory();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadAsync(MakeRelease(size: Payload.Length), dir.Path, progress: null, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_UntrustedDownloadUrl_ThrowsWithoutNetwork()
    {
        var (service, handler) = Create(_ => throw new InvalidOperationException("must not issue a request"));
        using var dir = new TempDirectory();
        var release = MakeRelease(downloadUrl: $"https://evil.example/{ExeName}");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, dir.Path));
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_UntrustedChecksumUrl_ThrowsWithoutNetwork()
    {
        var (service, handler) = Create(_ => throw new InvalidOperationException("must not issue a request"));
        using var dir = new TempDirectory();
        var release = MakeRelease(checksumUrl: $"https://evil.example/{ShaName}");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, dir.Path));
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Download_UnknownSize_ThrowsWithoutNetwork()
    {
        var (service, handler) = Create(_ => throw new InvalidOperationException("must not issue a request"));
        using var dir = new TempDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(size: 0), dir.Path));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_OversizedRelease_ThrowsWithoutNetwork()
    {
        var (service, handler) = Create(_ => throw new InvalidOperationException("must not issue a request"));
        using var dir = new TempDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.DownloadAsync(MakeRelease(size: (200L * 1024 * 1024) + 1), dir.Path));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_NonExecutableContent_DeletesStagingAndThrows()
    {
        var notPe = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        var hash = HashHex(notPe);
        var (service, _) = Create(request => IsChecksumRequest(request)
            ? TextResponse($"{hash}  {ExeName}")
            : BytesResponse(notPe));

        using var dir = new TempDirectory();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(size: notPe.Length), dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Theory]
    [InlineData("https://github.com/{0}/{1}/releases/download/v1.2.3/{2}?spoof=1")]
    [InlineData("https://github.com/{0}/{1}/releases/download/v1.2.3/{2}#fragment")]
    [InlineData("https://user:pass@github.com/{0}/{1}/releases/download/v1.2.3/{2}")]
    public async Task Check_AssetUrlWithQueryFragmentOrUserInfo_ReturnsNull(string template)
    {
        var exeUrl = string.Format(template, Owner, Repo, ExeName);
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(exeUrl: exeUrl)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_ChecksumUrlWithFragment_ReturnsNull()
    {
        var shaUrl = $"https://github.com/{Owner}/{Repo}/releases/download/v1.2.3/{ShaName}#fragment";
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(shaUrl: shaUrl)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_ReleasePageWithQuery_ReturnsNull()
    {
        var pageUrl = $"https://github.com/{Owner}/{Repo}/releases/tag/v1.2.3?redirect=https://evil.example";
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(pageUrl: pageUrl)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_AssetPathWithWrongCase_ReturnsNull()
    {
        var exeUrl = $"https://github.com/{Owner}/{Repo}/releases/download/V1.2.3/{ExeName}";
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(exeUrl: exeUrl)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_ReleaseLargerThanCap_ReturnsNull()
    {
        var (service, _) = Create(_ => JsonResponse(ReleaseJson(size: (200L * 1024 * 1024) + 1)));
        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Download_UrlWithQuery_ThrowsWithoutNetwork()
    {
        var (service, handler) = Create(_ => throw new InvalidOperationException("must not issue a request"));
        using var dir = new TempDirectory();
        var release = MakeRelease(downloadUrl: $"https://github.com/{Owner}/{Repo}/releases/download/v1.2.3/{ExeName}?spoof=1");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, dir.Path));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v1.2.3-beta")]
    [InlineData("v1.2")]
    [InlineData("v01.2.3")]
    [InlineData("")]
    public async Task Download_InvalidTag_ThrowsWithoutNetwork(string tag)
    {
        var (service, handler) = Create(_ => throw new InvalidOperationException("must not issue a request"));
        using var dir = new TempDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(tag: tag), dir.Path));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_TagVersionMismatch_ThrowsWithoutNetwork()
    {
        var (service, handler) = Create(_ => throw new InvalidOperationException("must not issue a request"));
        using var dir = new TempDirectory();
        var release = new UpdateRelease(
            new Version(9, 9, 9),
            "v1.2.3",
            "notes",
            new Uri($"https://github.com/{Owner}/{Repo}/releases/tag/v1.2.3"),
            new Uri($"https://github.com/{Owner}/{Repo}/releases/download/v1.2.3/{ExeName}"),
            new Uri($"https://github.com/{Owner}/{Repo}/releases/download/v1.2.3/{ShaName}"),
            Payload.Length);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, dir.Path));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Check_ApiRateLimited_FallsBackToPublicReleasePage(HttpStatusCode apiStatus)
    {
        var (service, handler) = Create(request => FallbackResponse(request, apiStatus, TagPage("v1.2.3")));

        var release = await service.CheckAsync(new Version(1, 0, 0));

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 3), release!.Version);
        Assert.Equal("v1.2.3", release.TagName);
        Assert.Equal("已通过公开发布页面检查更新。", release.Notes);
        Assert.Equal(TagPage("v1.2.3"), release.ReleasePage.AbsoluteUri);
        Assert.Equal($"https://github.com/{Owner}/{Repo}/releases/download/v1.2.3/{ExeName}", release.DownloadUrl.AbsoluteUri);
        Assert.Equal($"https://github.com/{Owner}/{Repo}/releases/download/v1.2.3/{ShaName}", release.ChecksumUrl.AbsoluteUri);
        Assert.Equal(Payload.Length, release.Size);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", handler.Requests[0].RequestUri!.AbsoluteUri);

        var headRequests = handler.Requests.Skip(1).ToList();
        Assert.All(headRequests, r => Assert.Equal(HttpMethod.Head, r.Method));
        Assert.Equal($"https://github.com/{Owner}/{Repo}/releases/latest", headRequests[0].RequestUri!.AbsoluteUri);
        Assert.Equal(release.DownloadUrl.AbsoluteUri, headRequests[1].RequestUri!.AbsoluteUri);
        Assert.Equal(release.ChecksumUrl.AbsoluteUri, headRequests[2].RequestUri!.AbsoluteUri);
        Assert.All(handler.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task Check_ApiForbidden_SameVersion_ReturnsNullWithoutAssetRequests()
    {
        var (service, handler) = Create(request => FallbackResponse(request, HttpStatusCode.Forbidden, TagPage("v1.0.0")));

        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith(ExeName, StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith(ShaName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://github.com/attacker/opencode-account-manager/releases/tag/v1.2.3")]
    [InlineData("https://github.com/ruoshuixx/other-repo/releases/tag/v1.2.3")]
    [InlineData("https://evil.example/ruoshuixx/opencode-account-manager/releases/tag/v1.2.3")]
    [InlineData("http://github.com/ruoshuixx/opencode-account-manager/releases/tag/v1.2.3")]
    [InlineData("https://github.com/ruoshuixx/opencode-account-manager/releases/tag/v1.2.3?x=1")]
    [InlineData("https://github.com/ruoshuixx/opencode-account-manager/releases/tag/v1.2.3#f")]
    [InlineData("https://user:pass@github.com/ruoshuixx/opencode-account-manager/releases/tag/v1.2.3")]
    [InlineData("https://github.com/ruoshuixx/opencode-account-manager/releases")]
    [InlineData("https://github.com/ruoshuixx/opencode-account-manager/releases/tag/")]
    [InlineData("https://github.com/ruoshuixx/opencode-account-manager/releases/tag/v1.2.3-beta")]
    [InlineData("https://github.com/ruoshuixx/opencode-account-manager/releases/tag/v1.2")]
    [InlineData("https://github.com/ruoshuixx/opencode-account-manager/releases/tag/V1.2.3")]
    [InlineData("https://github.com/ruoshuixx/opencode-account-manager/releases/tag/v01.2.3")]
    public async Task Check_Forbidden_UntrustedRedirectTarget_ReturnsNullWithoutAssetRequests(string finalUri)
    {
        var (service, handler) = Create(request => FallbackResponse(request, HttpStatusCode.Forbidden, finalUri));

        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Check_Forbidden_ExecutableAsset404_ReturnsNull()
    {
        var (service, handler) = Create(request => FallbackResponse(
            request, HttpStatusCode.Forbidden, TagPage("v1.2.3"), exeStatus: HttpStatusCode.NotFound));

        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Check_Forbidden_ChecksumAsset404_ReturnsNull()
    {
        var (service, handler) = Create(request => FallbackResponse(
            request, HttpStatusCode.Forbidden, TagPage("v1.2.3"), shaStatus: HttpStatusCode.NotFound));

        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Check_Forbidden_ExecutableHeadError_Throws()
    {
        var (service, _) = Create(request => FallbackResponse(
            request, HttpStatusCode.Forbidden, TagPage("v1.2.3"), exeStatus: HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_Forbidden_ChecksumHeadError_Throws()
    {
        var (service, _) = Create(request => FallbackResponse(
            request, HttpStatusCode.Forbidden, TagPage("v1.2.3"), shaStatus: HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_Forbidden_FallbackPage404_ReturnsNull()
    {
        var (service, handler) = Create(request => FallbackResponse(
            request, HttpStatusCode.Forbidden, TagPage("v1.2.3"), latestStatus: HttpStatusCode.NotFound));

        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Check_Forbidden_FallbackPageError_Throws()
    {
        var (service, _) = Create(request => FallbackResponse(
            request, HttpStatusCode.Forbidden, TagPage("v1.2.3"), latestStatus: HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_Forbidden_CancelledDuringFallback_Throws()
    {
        using var cts = new CancellationTokenSource();
        var (service, _) = Create(request =>
        {
            if (request.RequestUri!.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                cts.Cancel();
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            return FallbackResponse(request, HttpStatusCode.Forbidden, TagPage("v1.2.3"));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(new Version(1, 0, 0), cts.Token));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(200L * 1024 * 1024 + 1)]
    public async Task Check_Forbidden_ExecutableInvalidLength_ReturnsNull(long length)
    {
        var (service, _) = Create(request => FallbackResponse(
            request, HttpStatusCode.Forbidden, TagPage("v1.2.3"), exeLength: length));

        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task Check_Forbidden_ExecutableWithoutLength_ReturnsNull()
    {
        var (service, _) = Create(request => FallbackResponse(
            request, HttpStatusCode.Forbidden, TagPage("v1.2.3"), exeHasLength: false));

        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(4L * 1024 + 1)]
    public async Task Check_Forbidden_ChecksumInvalidLength_ReturnsNull(long length)
    {
        var (service, _) = Create(request => FallbackResponse(
            request, HttpStatusCode.Forbidden, TagPage("v1.2.3"), shaLength: length));

        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    private static string TagPage(string tag) => $"https://github.com/{Owner}/{Repo}/releases/tag/{tag}";

    private static HttpResponseMessage FallbackResponse(
        HttpRequestMessage request,
        HttpStatusCode apiStatus,
        string finalUri,
        HttpStatusCode latestStatus = HttpStatusCode.OK,
        HttpStatusCode exeStatus = HttpStatusCode.OK,
        long exeLength = 9,
        bool exeHasLength = true,
        HttpStatusCode shaStatus = HttpStatusCode.OK,
        long shaLength = 80,
        bool shaHasLength = true)
    {
        var uri = request.RequestUri!;
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(apiStatus);
        }

        if (uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
        {
            // Simulate the public /releases/latest -> /releases/tag/{tag} redirect.
            return new HttpResponseMessage(latestStatus)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Head, new Uri(finalUri)),
            };
        }

        if (uri.AbsolutePath.EndsWith(ExeName, StringComparison.Ordinal))
        {
            var response = new HttpResponseMessage(exeStatus);
            if (exeStatus == HttpStatusCode.OK)
            {
                response.Content = exeHasLength
                    ? new DeclaredLengthContent(exeLength)
                    : new NoLengthStreamContent(new MemoryStream());
            }
            return response;
        }

        if (uri.AbsolutePath.EndsWith(ShaName, StringComparison.Ordinal))
        {
            var response = new HttpResponseMessage(shaStatus);
            if (shaStatus == HttpStatusCode.OK)
            {
                response.Content = shaHasLength
                    ? new DeclaredLengthContent(shaLength)
                    : new NoLengthStreamContent(new MemoryStream());
            }
            return response;
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            try
            {
                return Task.FromResult(responder(request));
            }
            catch (Exception exception)
            {
                return Task.FromException<HttpResponseMessage>(exception);
            }
        }
    }

    private sealed class ListProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = new();

        public void Report(T value) => Items.Add(value);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "am-update-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>HTTP content whose length is unknown, so the service must rely on the stream itself.</summary>
    private sealed class NoLengthStreamContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context)
            => stream.CopyToAsync(target);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
    }

    /// <summary>HTTP content that advertises a fixed Content-Length (as a HEAD probe would).</summary>
    private sealed class DeclaredLengthContent(long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long computed)
        {
            computed = length;
            return true;
        }
    }

    /// <summary>Emits one prefix chunk, then throws the configured fault on the next read.</summary>
    private sealed class FaultingStream(byte[] prefix, Exception fault) : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (_reads++ == 0)
            {
                var count = Math.Min(buffer.Length, prefix.Length);
                prefix.AsSpan(0, count).CopyTo(buffer.Span);
                return count;
            }
            throw fault;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
