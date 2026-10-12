using System.Net;
using System.Net.Http;
using System.Text;
using AccountManager.Core.Sync;
using Xunit;

namespace AccountManager.Core.Tests;

public class WebDavClientTests
{
    private const string Base = "https://dav.example.com/dav/";
    private const string Folder = "/OpenCodeAccountManager/";
    private const string FileName = "opencode_2026-10-07_14-30-05.json";
    private const string FileUrl = "https://dav.example.com/dav/OpenCodeAccountManager/opencode_2026-10-07_14-30-05.json";
    private const string DirUrl = "https://dav.example.com/dav/OpenCodeAccountManager/";

    private static (WebDavClient Client, StubHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string baseUrl = Base,
        long maxBytes = 20L * 1024 * 1024)
    {
        var handler = new StubHandler(responder);
        var http = new HttpClient(handler);
        var options = new WebDavOptions
        {
            BaseUrl = baseUrl,
            Username = "alice@example.com",
            Password = "app-password-secret",
            Folder = Folder,
            MaxDownloadBytes = maxBytes,
        };
        return (new WebDavClient(options, http), handler);
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Xml(string body) =>
        new((HttpStatusCode)207) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private const string Multistatus = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/dav/OpenCodeAccountManager/</d:href>
            <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
              <d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/dav/OpenCodeAccountManager/opencode_2026-10-07_14-30-05.json</d:href>
            <d:propstat><d:prop><d:resourcetype/>
              <d:getcontentlength>1234</d:getcontentlength>
              <d:getlastmodified>Wed, 07 Oct 2026 14:30:05 GMT</d:getlastmodified></d:prop>
              <d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
          <d:response>
            <d:href>/dav/OpenCodeAccountManager/notes.txt</d:href>
            <d:propstat><d:prop><d:resourcetype/>
              <d:getcontentlength>5</d:getcontentlength></d:prop>
              <d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    [Fact]
    public async Task Test_SendsAuthenticatedPropfind_ToBase()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage((HttpStatusCode)207));

        await client.TestAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PROPFIND", request.Method.Method);
        Assert.Equal(Base, request.RequestUri!.AbsoluteUri);
        Assert.Equal("0", Assert.Single(request.Headers.GetValues("Depth")));
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("alice@example.com:app-password-secret")),
            request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Test_Unauthorized_ThrowsWithoutLeakingPassword()
    {
        var (client, _) = Create(_ => Status(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<WebDavException>(() => client.TestAsync());
        Assert.Contains("认证失败", ex.Message);
        Assert.DoesNotContain("app-password-secret", ex.Message);
    }

    [Fact]
    public async Task Upload_CreatesParentThenPutsBody()
    {
        byte[]? putBody = null;
        var (client, handler) = Create(request =>
        {
            if (request.Method.Method == "PUT")
            {
                putBody = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }

            return Status(HttpStatusCode.Created);
        });

        var payload = Encoding.UTF8.GetBytes("[{\"id\":\"a\"}]");
        await client.UploadAsync(FileName, payload);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("MKCOL", handler.Requests[0].Method.Method);
        Assert.Equal(DirUrl, handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal("PUT", handler.Requests[1].Method.Method);
        Assert.Equal(FileUrl, handler.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Equal(payload, putBody);
        Assert.All(handler.Requests, r => Assert.Equal("Basic", r.Headers.Authorization!.Scheme));
    }

    [Fact]
    public async Task Upload_ToleratesExistingDirectory_405()
    {
        var (client, handler) = Create(request =>
            request.Method.Method == "MKCOL" ? Status(HttpStatusCode.MethodNotAllowed) : new HttpResponseMessage(HttpStatusCode.NoContent));

        await client.UploadAsync(FileName, new byte[] { 1, 2, 3 });

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("PUT", handler.Requests[1].Method.Method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape.json")]
    [InlineData("sub/dir.json")]
    [InlineData("..\\escape.json")]
    [InlineData("notes.txt")]
    public async Task Upload_InvalidFileName_Throws(string fileName)
    {
        var (client, handler) = Create(_ => Status(HttpStatusCode.Created));

        await Assert.ThrowsAsync<WebDavException>(() => client.UploadAsync(fileName, new byte[] { 1 }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task List_ParsesJsonFilesOnly()
    {
        var (client, handler) = Create(_ => Xml(Multistatus));

        var files = await client.ListAsync();

        var file = Assert.Single(files);
        Assert.Equal(FileName, file.Name);
        Assert.Equal(1234, file.Size);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 14, 30, 5, TimeSpan.Zero), file.LastModified);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PROPFIND", request.Method.Method);
        Assert.Equal(DirUrl, request.RequestUri!.AbsoluteUri);
        Assert.Equal("1", Assert.Single(request.Headers.GetValues("Depth")));
    }

    [Fact]
    public async Task List_NotFound_ReturnsEmpty()
    {
        var (client, _) = Create(_ => Status(HttpStatusCode.NotFound));

        Assert.Empty(await client.ListAsync());
    }

    [Fact]
    public async Task Download_NotFound_ReturnsNull()
    {
        var (client, _) = Create(_ => Status(HttpStatusCode.NotFound));

        Assert.Null(await client.DownloadAsync(FileName));
    }

    [Fact]
    public async Task Download_ReturnsBytes()
    {
        var (client, handler) = Create(_ => Json("[{\"id\":\"x\"}]"));

        var bytes = await client.DownloadAsync(FileName);

        Assert.NotNull(bytes);
        Assert.Equal("[{\"id\":\"x\"}]", Encoding.UTF8.GetString(bytes!));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(FileUrl, request.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Download_DeclaredTooLarge_Throws()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[4096]),
        }, maxBytes: 1024);

        var ex = await Assert.ThrowsAsync<WebDavException>(() => client.DownloadAsync(FileName));
        Assert.Contains("过大", ex.Message);
    }

    [Fact]
    public async Task Download_StreamedTooLarge_Throws()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[4096]),
        }, maxBytes: 1024);

        var ex = await Assert.ThrowsAsync<WebDavException>(() => client.DownloadAsync(FileName));
        Assert.Contains("过大", ex.Message);
    }

    [Fact]
    public void IsSecure_ReflectsScheme()
    {
        var (secure, _) = Create(_ => Status(HttpStatusCode.OK));
        Assert.True(secure.IsSecure);

        var (insecure, _) = Create(_ => Status(HttpStatusCode.OK), baseUrl: "http://dav.example.com/dav/");
        Assert.False(insecure.IsSecure);
    }

    [Fact]
    public void FolderUri_KeepsBasePath()
    {
        var (client, _) = Create(_ => Status(HttpStatusCode.OK));
        Assert.Equal(DirUrl, client.FolderUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("sync", "/sync/")]
    [InlineData("/sync", "/sync/")]
    [InlineData("/sync/", "/sync/")]
    public void Folder_NormalizesSlashes(string input, string expected)
    {
        var options = new WebDavOptions { BaseUrl = Base, Username = "u", Password = "p", Folder = input };
        var client = new WebDavClient(options, new HttpClient(new StubHandler(_ => Status(HttpStatusCode.OK))));

        Assert.Equal(expected, client.Folder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://dav.example.com/")]
    public void Constructor_InvalidUrl_Throws(string url)
    {
        var options = new WebDavOptions { BaseUrl = url, Username = "u", Password = "p" };
        Assert.Throws<WebDavException>(() => new WebDavClient(options, new HttpClient(new StubHandler(_ => Status(HttpStatusCode.OK)))));
    }

    [Fact]
    public void Constructor_MissingUsername_Throws()
    {
        var options = new WebDavOptions { BaseUrl = Base, Username = "", Password = "p" };
        Assert.Throws<WebDavException>(() => new WebDavClient(options, new HttpClient(new StubHandler(_ => Status(HttpStatusCode.OK)))));
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

    /// <summary>Content with an unknown length so the size guard must rely on the streamed bytes.</summary>
    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(payload, 0, payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
