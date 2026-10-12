using System.Net;
using System.Text;
using AccountManager.Core.Updates;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>The .deb installation updates from a version-named package asset next to its checksum.</summary>
public sealed class DebUpdateAssetTests
{
    private const string Owner = "ruoshuixx";
    private const string Repo = "opencode-account-manager";

    [Fact]
    public void DebProfileDerivesTheAssetNamesFromTheTag()
    {
        var profile = UpdateAssetProfile.For(UpdatePlatform.LinuxDeb);

        Assert.True(profile.IsDeb);
        Assert.True(profile.IsLinux);
        Assert.Equal("opencode-account-manager_1.2.3_amd64.deb", profile.ExecutableAssetNameFor("v1.2.3"));
        Assert.Equal("opencode-account-manager_1.2.3_amd64.deb.sha256", profile.ChecksumAssetNameFor("v1.2.3"));
    }

    [Fact]
    public void OtherProfilesKeepTheirFixedAssetNames()
    {
        var windows = UpdateAssetProfile.For(UpdatePlatform.WindowsX64);
        var linux = UpdateAssetProfile.For(UpdatePlatform.LinuxX64);

        Assert.Equal("OpenCodeAccountManager-win-x64.exe", windows.ExecutableAssetNameFor("v9.9.9"));
        Assert.Equal("OpenCodeAccountManager-linux-x64", linux.ExecutableAssetNameFor("v9.9.9"));
        Assert.False(windows.IsDeb);
        Assert.False(linux.IsDeb);
    }

    [Fact]
    public void DebPackageHeaderIsTheArMagicWithADebianBinaryMember()
    {
        var header = Encoding.ASCII.GetBytes("!<arch>\ndebian-binary   1700000000  0     0     100644  4         `\n2.0\n");

        Assert.True(ExecutableFormat.IsDebPackage(header));
        Assert.False(ExecutableFormat.IsDebPackage(Encoding.ASCII.GetBytes("!<arch>\nsomething-else    ")));
        Assert.False(ExecutableFormat.IsDebPackage(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }));
        Assert.False(ExecutableFormat.IsDebPackage(Array.Empty<byte>()));
    }

    [Fact]
    public async Task DebProfileOffersAnUpdateOnlyWhenTheVersionNamedPackageAndChecksumExist()
    {
        var withDeb = new StubHandler(_ => Json(Release(
            "opencode-account-manager_1.2.3_amd64.deb", "opencode-account-manager_1.2.3_amd64.deb.sha256")));
        var service = new GitHubUpdateService(new HttpClient(withDeb), Owner, Repo, UpdatePlatform.LinuxDeb);

        var release = await service.CheckAsync(new Version(1, 0, 0));

        Assert.NotNull(release);
        Assert.Equal(UpdatePlatform.LinuxDeb, release!.Platform);
        Assert.Equal(
            "https://github.com/ruoshuixx/opencode-account-manager/releases/download/v1.2.3/opencode-account-manager_1.2.3_amd64.deb",
            release.DownloadUrl.AbsoluteUri);

        var onlyPortable = new StubHandler(_ => Json(Release("OpenCodeAccountManager-linux-x64", "OpenCodeAccountManager-linux-x64.sha256")));
        var other = new GitHubUpdateService(new HttpClient(onlyPortable), Owner, Repo, UpdatePlatform.LinuxDeb);
        Assert.Null(await other.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task DebProfileRejectsADownloadThatIsNotADebianPackage()
    {
        var payload = new byte[1024];
        new Random(7).NextBytes(payload);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".sha256", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(hash + "  opencode-account-manager_1.2.3_amd64.deb\n"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });
        var service = new GitHubUpdateService(new HttpClient(handler), Owner, Repo, UpdatePlatform.LinuxDeb);
        var release = new UpdateRelease(
            new Version(1, 2, 3), "v1.2.3", "notes",
            new Uri("https://github.com/ruoshuixx/opencode-account-manager/releases/tag/v1.2.3"),
            new Uri("https://github.com/ruoshuixx/opencode-account-manager/releases/download/v1.2.3/opencode-account-manager_1.2.3_amd64.deb"),
            new Uri("https://github.com/ruoshuixx/opencode-account-manager/releases/download/v1.2.3/opencode-account-manager_1.2.3_amd64.deb.sha256"),
            payload.Length, UpdatePlatform.LinuxDeb);
        var directory = Path.Combine(Path.GetTempPath(), "ocam-deb-asset-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, directory));
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    }

    private static string Release(string packageName, string checksumName)
    {
        string Asset(string name, long size) => "{\"name\":\"" + name + "\",\"size\":" + size
            + ",\"browser_download_url\":\"https://github.com/ruoshuixx/opencode-account-manager/releases/download/v1.2.3/" + name + "\"}";
        return "{\"tag_name\":\"v1.2.3\",\"draft\":false,\"prerelease\":false,\"body\":\"notes\","
            + "\"html_url\":\"https://github.com/ruoshuixx/opencode-account-manager/releases/tag/v1.2.3\","
            + "\"assets\":[" + Asset(packageName, 4096) + "," + Asset(checksumName, 100) + "]}";
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
