using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountManager.Core.Updates;
using Xunit;

namespace AccountManager.Core.Tests;

public sealed class LinuxUpdateAssetTests
{
    private const string Owner = "ruoshuixx";
    private const string Repo = "opencode-account-manager";
    private const string WindowsExecutable = "OpenCodeAccountManager-win-x64.exe";
    private const string WindowsChecksum = "OpenCodeAccountManager-win-x64.exe.sha256";
    private const string LinuxExecutable = "OpenCodeAccountManager-linux-x64";
    private const string LinuxChecksum = "OpenCodeAccountManager-linux-x64.sha256";

    private static readonly byte[] ElfPayload = BuildSyntheticElf();

    [Fact]
    public async Task DefaultServiceStillSelectsWindowsAssetsAndPlatform()
    {
        var handler = new StubHandler(_ => JsonResponse(ReleaseJson(includeWindows: true, includeLinux: false)));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo);

        var release = await service.CheckAsync(new Version(1, 0, 0));

        Assert.NotNull(release);
        Assert.Equal(UpdatePlatform.WindowsX64, release!.Platform);
        Assert.EndsWith(WindowsExecutable, release.DownloadUrl.AbsoluteUri, StringComparison.Ordinal);
        Assert.EndsWith(WindowsChecksum, release.ChecksumUrl.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxProfileSelectsOnlyFixedLinuxX64AssetsAndCarriesCanonicalMetadata()
    {
        var handler = new StubHandler(_ => JsonResponse(ReleaseJson(includeWindows: true, includeLinux: true, includeTarball: true)));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);

        var release = await service.CheckAsync(new Version(1, 0, 0));

        Assert.NotNull(release);
        Assert.Equal(UpdatePlatform.LinuxX64, release!.Platform);
        Assert.Equal(new Version(1, 2, 3), release.Version);
        Assert.Equal("v1.2.3", release.TagName);
        Assert.Equal("synthetic release notes", release.Notes);
        Assert.Equal(ElfPayload.Length, release.Size);
        Assert.Equal(ReleasePage("v1.2.3"), release.ReleasePage.AbsoluteUri);
        Assert.Equal(DownloadUrl("v1.2.3", LinuxExecutable), release.DownloadUrl.AbsoluteUri);
        Assert.Equal(DownloadUrl("v1.2.3", LinuxChecksum), release.ChecksumUrl.AbsoluteUri);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LinuxProfileDoesNotOfferUpdateWhenOnlyWindowsOrTarballAssetsExist()
    {
        var handler = new StubHandler(_ => JsonResponse(ReleaseJson(includeWindows: true, includeLinux: false, includeTarball: true)));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);

        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void UndefinedPlatformIsRejectedAtConstruction()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("must not send")));

        Assert.Throws<ArgumentOutOfRangeException>(() => new GitHubUpdateService(http, Owner, Repo, (UpdatePlatform)42));
    }

    [Fact]
    public async Task DownloadRejectsReleaseForDifferentPlatformBeforeEffects()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not send"));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);
        using var directory = new TempDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(UpdatePlatform.WindowsX64), directory.Path));

        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("repository")]
    [InlineData("executable-name")]
    [InlineData("checksum-name")]
    public async Task DownloadRejectsMismatchedVersionRepositoryOrAssetNamesWithoutNetwork(string mismatch)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not send"));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);
        using var directory = new TempDirectory();
        var valid = MakeRelease();
        var altered = mismatch switch
        {
            "version" => valid with { Version = new Version(9, 9, 9) },
            "repository" => valid with { DownloadUrl = new Uri($"https://github.com/other/repo/releases/download/v1.2.3/{LinuxExecutable}") },
            "executable-name" => valid with { DownloadUrl = new Uri(DownloadUrl("v1.2.3", "another-linux-x64")) },
            _ => valid with { ChecksumUrl = new Uri(DownloadUrl("v1.2.3", "another-linux-x64.sha256")) },
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(altered, directory.Path));

        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadVerifiedLinuxElfUsesLinuxChecksumNameAndStagingFilename()
    {
        var hash = HashHex(ElfPayload);
        var handler = new StubHandler(request => IsChecksum(request)
            ? TextResponse($"{hash}  {LinuxExecutable}")
            : BytesResponse(ElfPayload));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);
        using var directory = new TempDirectory();

        var result = await service.DownloadAsync(MakeRelease(), directory.Path);

        Assert.Equal(LinuxExecutable, Path.GetFileName(result));
        Assert.Equal(ElfPayload, await File.ReadAllBytesAsync(result));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(DownloadUrl("v1.2.3", LinuxChecksum), handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal(DownloadUrl("v1.2.3", LinuxExecutable), handler.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Equal(UpdatePlatform.LinuxX64, MakeRelease().Platform);
        AssertPrivateLinuxStagingModesIfUnix(result);
    }

    [Fact]
    public async Task DownloadDoesNotReturnDifferentValidElfReplacedWhenSourceDisposes()
    {
        var replacement = ElfPayload.ToArray();
        replacement[40] = 0x5a;
        Assert.NotEqual(HashHex(ElfPayload), HashHex(replacement));

        using var directory = new TempDirectory();
        string? targetPath = null;
        var handler = new StubHandler(request =>
        {
            if (IsChecksum(request)) { return TextResponse($"{HashHex(ElfPayload)}  {LinuxExecutable}"); }

            var stage = Assert.Single(Directory.GetDirectories(directory.Path));
            targetPath = Path.Combine(stage, LinuxExecutable);
            return ReplacingStreamResponse(ElfPayload, () => ReplaceClosedTarget(targetPath, replacement));
        });
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), directory.Path));

        Assert.NotNull(targetPath);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(targetPath));
    }

    [Fact]
    public async Task DownloadCleanupPreservesDifferentElfReplacedWhenSourceDisposesAfterFailure()
    {
        var replacement = ElfPayload.ToArray();
        replacement[40] = 0x6b;
        Assert.NotEqual(HashHex(ElfPayload[..20]), HashHex(replacement));

        using var directory = new TempDirectory();
        string? targetPath = null;
        var handler = new StubHandler(request =>
        {
            if (IsChecksum(request)) { return TextResponse($"{HashHex(ElfPayload)}  {LinuxExecutable}"); }

            var stage = Assert.Single(Directory.GetDirectories(directory.Path));
            targetPath = Path.Combine(stage, LinuxExecutable);
            return ReplacingStreamResponse(ElfPayload[..20], () => ReplaceClosedTarget(targetPath, replacement));
        });
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), directory.Path));

        Assert.NotNull(targetPath);
        Assert.True(File.Exists(targetPath));
        Assert.Equal(replacement, await File.ReadAllBytesAsync(targetPath));
    }

    [Fact]
    public async Task DownloadRejectsStagingDirectoryReplacementWhileResponseIsAwaited()
    {
        using var directory = new TempDirectory();
        string? replacementDirectory = null;
        string? displacedDirectory = null;
        var handler = new StubHandler(request =>
        {
            if (IsChecksum(request)) { return TextResponse($"{HashHex(ElfPayload)}  {LinuxExecutable}"); }

            var stage = Assert.Single(Directory.GetDirectories(directory.Path));
            displacedDirectory = Path.Combine(directory.Path, "displaced-stage");
            Directory.Move(stage, displacedDirectory);
            Directory.CreateDirectory(stage);
            replacementDirectory = stage;
            return BytesResponse(ElfPayload);
        });
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), directory.Path));

        Assert.NotNull(replacementDirectory);
        Assert.True(Directory.Exists(replacementDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(replacementDirectory));
        Assert.NotNull(displacedDirectory);
        Assert.True(Directory.Exists(displacedDirectory));
    }

    [LinuxOnlyFact]
    public async Task DownloadDoesNotFollowStagingDirectoryLinkReplacedWhileResponseIsAwaited()
    {
        using var directory = new TempDirectory();
        using var linkTarget = new TempDirectory();
        string? stageLink = null;
        var handler = new StubHandler(request =>
        {
            if (IsChecksum(request)) { return TextResponse($"{HashHex(ElfPayload)}  {LinuxExecutable}"); }

            var stage = Assert.Single(Directory.GetDirectories(directory.Path));
            var displaced = Path.Combine(directory.Path, "displaced-stage");
            Directory.Move(stage, displaced);
            Directory.CreateSymbolicLink(stage, linkTarget.Path);
            stageLink = stage;
            return BytesResponse(ElfPayload);
        });
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), directory.Path));

        Assert.NotNull(stageLink);
        Assert.Equal(linkTarget.Path, new DirectoryInfo(stageLink!).LinkTarget);
        Assert.Empty(Directory.GetFileSystemEntries(linkTarget.Path));
    }

    [Fact]
    public async Task DownloadRejectsChecksumForWindowsFilename()
    {
        var hash = HashHex(ElfPayload);
        var handler = new StubHandler(_ => TextResponse($"{hash}  {WindowsExecutable}"));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);
        using var directory = new TempDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), directory.Path));

        Assert.Single(handler.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadLinuxRejectsPortableExecutableAndCleansStaging()
    {
        var pePayload = new byte[64];
        pePayload[0] = (byte)'M';
        pePayload[1] = (byte)'Z';
        var hash = HashHex(pePayload);
        var handler = new StubHandler(request => IsChecksum(request)
            ? TextResponse($"{hash}  {LinuxExecutable}")
            : BytesResponse(pePayload));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);
        using var directory = new TempDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), directory.Path));

        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadLinuxHashMismatchCleansOnlyItsKnownArtifact()
    {
        var wrongHash = new string('0', 64);
        var handler = new StubHandler(request => IsChecksum(request)
            ? TextResponse($"{wrongHash}  {LinuxExecutable}")
            : BytesResponse(ElfPayload));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);
        using var directory = new TempDirectory();
        string? unownedPath = null;
        var progress = new CallbackProgress<UpdateDownloadProgress>(() =>
        {
            var stage = Assert.Single(Directory.GetDirectories(directory.Path));
            unownedPath = Path.Combine(stage, "concurrent-unowned.txt");
            File.WriteAllText(unownedPath, "leave this file alone");
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), directory.Path, progress));

        Assert.NotNull(unownedPath);
        Assert.True(File.Exists(unownedPath));
        Assert.Equal("leave this file alone", await File.ReadAllTextAsync(unownedPath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(unownedPath)!, LinuxExecutable)));
    }

    [Fact]
    public async Task DownloadDoesNotDeleteConcurrentFileAtItsStagingNameWhenCreateNewFails()
    {
        using var directory = new TempDirectory();
        string? unownedPath = null;
        var handler = new StubHandler(request =>
        {
            if (IsChecksum(request)) { return TextResponse($"{HashHex(ElfPayload)}  {LinuxExecutable}"); }

            var stage = Assert.Single(Directory.GetDirectories(directory.Path));
            unownedPath = Path.Combine(stage, LinuxExecutable);
            File.WriteAllText(unownedPath, "concurrent file");
            return BytesResponse(ElfPayload);
        });
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);

        await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(MakeRelease(), directory.Path));

        Assert.NotNull(unownedPath);
        Assert.True(File.Exists(unownedPath));
        Assert.Equal("concurrent file", await File.ReadAllTextAsync(unownedPath));
    }

    [Fact]
    public async Task DownloadLinuxLengthMismatchCleansStagingAndThrows()
    {
        var hash = HashHex(ElfPayload);
        var handler = new StubHandler(request => IsChecksum(request)
            ? TextResponse($"{hash}  {LinuxExecutable}")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DeclaredLengthContent(ElfPayload.Length + 1),
            });
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);
        using var directory = new TempDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), directory.Path));

        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadLinuxTruncatedBodyWithoutContentLengthCleansStagingAndThrows()
    {
        var hash = HashHex(ElfPayload);
        var handler = new StubHandler(request => IsChecksum(request)
            ? TextResponse($"{hash}  {LinuxExecutable}")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new NoLengthStreamContent(new MemoryStream(ElfPayload[..20])),
            });
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);
        using var directory = new TempDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(MakeRelease(), directory.Path));

        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadLinuxCancellationCleansStagingAndPropagates()
    {
        var hash = HashHex(ElfPayload);
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(request => IsChecksum(request)
            ? TextResponse($"{hash}  {LinuxExecutable}")
            : BytesResponse(ElfPayload));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);
        using var directory = new TempDirectory();
        var progress = new CallbackProgress<UpdateDownloadProgress>(() => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.DownloadAsync(MakeRelease(), directory.Path, progress, cancellation.Token));

        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task LinuxRateLimitFallbackUsesLinuxHeadUrlsAndReturnsLinuxProfile(HttpStatusCode apiStatus)
    {
        var handler = new StubHandler(request => FallbackResponse(request, apiStatus));
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxX64);

        var release = await service.CheckAsync(new Version(1, 0, 0));

        Assert.NotNull(release);
        Assert.Equal(UpdatePlatform.LinuxX64, release!.Platform);
        Assert.Equal(ElfPayload.Length, release.Size);
        Assert.Equal(ReleasePage("v1.2.3"), release.ReleasePage.AbsoluteUri);
        Assert.Equal(DownloadUrl("v1.2.3", LinuxExecutable), release.DownloadUrl.AbsoluteUri);
        Assert.Equal(DownloadUrl("v1.2.3", LinuxChecksum), release.ChecksumUrl.AbsoluteUri);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Head, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Head, handler.Requests[2].Method);
        Assert.Equal(DownloadUrl("v1.2.3", LinuxExecutable), handler.Requests[2].RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Head, handler.Requests[3].Method);
        Assert.Equal(DownloadUrl("v1.2.3", LinuxChecksum), handler.Requests[3].RequestUri!.AbsoluteUri);
        Assert.All(handler.Requests, request => Assert.Null(request.Headers.Authorization));
    }

    private static UpdateRelease MakeRelease(UpdatePlatform platform = UpdatePlatform.LinuxX64)
        => new(
            new Version(1, 2, 3),
            "v1.2.3",
            "synthetic notes",
            new Uri(ReleasePage("v1.2.3")),
            new Uri(DownloadUrl("v1.2.3", LinuxExecutable)),
            new Uri(DownloadUrl("v1.2.3", LinuxChecksum)),
            ElfPayload.Length,
            platform);

    private static string ReleaseJson(
        bool includeWindows,
        bool includeLinux,
        bool includeTarball = false,
        string tag = "v1.2.3")
    {
        var assets = new List<object>();
        if (includeWindows)
        {
            assets.Add(new { name = WindowsExecutable, size = ElfPayload.Length, browser_download_url = DownloadUrl(tag, WindowsExecutable) });
            assets.Add(new { name = WindowsChecksum, size = 80, browser_download_url = DownloadUrl(tag, WindowsChecksum) });
        }
        if (includeLinux)
        {
            assets.Add(new { name = LinuxExecutable, size = ElfPayload.Length, browser_download_url = DownloadUrl(tag, LinuxExecutable) });
            assets.Add(new { name = LinuxChecksum, size = 80, browser_download_url = DownloadUrl(tag, LinuxChecksum) });
        }
        if (includeTarball)
        {
            assets.Add(new { name = "OpenCodeAccountManager-linux-x64.tar.gz", size = ElfPayload.Length, browser_download_url = DownloadUrl(tag, "OpenCodeAccountManager-linux-x64.tar.gz") });
        }

        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            prerelease = false,
            html_url = ReleasePage(tag),
            body = "synthetic release notes",
            assets,
        });
    }

    private static HttpResponseMessage FallbackResponse(HttpRequestMessage request, HttpStatusCode apiStatus)
    {
        var uri = request.RequestUri!;
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(apiStatus);
        }
        if (uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Head, new Uri(ReleasePage("v1.2.3"))),
            };
        }
        if (uri.AbsolutePath.EndsWith(LinuxExecutable, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new DeclaredLengthContent(ElfPayload.Length) };
        }
        if (uri.AbsolutePath.EndsWith(LinuxChecksum, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new DeclaredLengthContent(80) };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage TextResponse(string text)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };

    private static HttpResponseMessage BytesResponse(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage ReplacingStreamResponse(byte[] bytes, Action onDispose)
        => new(HttpStatusCode.OK) { Content = new ReplacingStreamContent(new DisposeBoundaryStream(bytes, onDispose)) };

    private static void ReplaceClosedTarget(string? path, byte[] replacement)
    {
        Assert.NotNull(path);
        using var target = new FileStream(path!, FileMode.Open, FileAccess.Write, FileShare.None);
        target.SetLength(0);
        target.Write(replacement);
    }

    private static bool IsChecksum(HttpRequestMessage request)
        => request.RequestUri!.AbsolutePath.EndsWith(LinuxChecksum, StringComparison.Ordinal);

    private static string HashHex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReleasePage(string tag)
        => $"https://github.com/{Owner}/{Repo}/releases/tag/{tag}";

    private static string DownloadUrl(string tag, string filename)
        => $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{filename}";

    private static byte[] BuildSyntheticElf()
    {
        var bytes = new byte[64];
        bytes[0] = 0x7f;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        bytes[16] = 2;
        bytes[18] = 0x3e;
        bytes[20] = 1;
        return bytes;
    }

    private static void AssertPrivateLinuxStagingModesIfUnix(string executablePath)
    {
        if (OperatingSystem.IsWindows()) { return; }

        var privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var privateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var modeMask = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        Assert.Equal(privateDirectory, File.GetUnixFileMode(Path.GetDirectoryName(executablePath)!) & modeMask);
        Assert.Equal(privateFile, File.GetUnixFileMode(executablePath) & modeMask);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            try { return Task.FromResult(responder(request)); }
            catch (Exception exception) { return Task.FromException<HttpResponseMessage>(exception); }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "am-linux-update-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) { Directory.Delete(Path, recursive: true); } }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class CallbackProgress<T>(Action callback) : IProgress<T>
    {
        public void Report(T value) => callback();
    }

    private sealed class LinuxOnlyFactAttribute : FactAttribute
    {
        public LinuxOnlyFactAttribute()
        {
            if (!OperatingSystem.IsLinux()) { Skip = "Requires native Linux symlink behavior."; }
        }
    }

    private sealed class DeclaredLengthContent(long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long computed)
        {
            computed = length;
            return true;
        }
    }

    private sealed class NoLengthStreamContent(Stream source) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => source.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(source);
    }

    private sealed class ReplacingStreamContent(Stream source) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => source.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(source);
    }

    private sealed class DisposeBoundaryStream(byte[] bytes, Action onDispose) : MemoryStream(bytes, writable: false)
    {
        private bool _boundaryCrossed;

        public override ValueTask DisposeAsync()
        {
            CrossBoundaryOnce();
            return base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { CrossBoundaryOnce(); }
            base.Dispose(disposing);
        }

        private void CrossBoundaryOnce()
        {
            if (_boundaryCrossed) { return; }
            _boundaryCrossed = true;
            onDispose();
        }
    }
}
