using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace AccountManager.Core.Sync;

/// <summary>Connection settings for the manual WebDAV sync.</summary>
public sealed class WebDavOptions
{
    /// <summary>Server root/collection URL, e.g. <c>https://dav.jianguoyun.com/dav/</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>App-specific password. Never logged or echoed.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Remote folder (relative to <see cref="BaseUrl"/>) that holds the timestamped JSON files.</summary>
    public string Folder { get; set; } = "/OpenCodeAccountManager/";

    public int TimeoutMs { get; set; } = 30000;

    /// <summary>Refuse to download more than this; matches the importer's own cap.</summary>
    public long MaxDownloadBytes { get; set; } = 20L * 1024 * 1024;
}

/// <summary>One remote JSON file listed from the sync folder.</summary>
public sealed record WebDavFile(string Name, DateTimeOffset? LastModified, long Size);

public sealed class WebDavException : Exception
{
    public WebDavException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Minimal WebDAV client used only for manual upload/download of the account JSON.
/// Implements just what the sync needs: PROPFIND (test + list), MKCOL (create folder),
/// PUT (upload) and GET (download). Basic auth over HTTPS; credentials never appear in
/// messages or logs. Uses a caller-supplied <see cref="HttpClient"/> for testability.
/// </summary>
public sealed class WebDavClient
{
    private const string PropFindTestBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>";

    private const string PropFindListBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>";

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly AuthenticationHeaderValue _auth;
    private readonly string _folder;
    private readonly int _timeoutMs;
    private readonly long _maxDownloadBytes;

    public WebDavClient(WebDavOptions options, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new WebDavException("请先填写 WebDAV 地址。");
        }

        if (!Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new WebDavException("WebDAV 地址必须是 http:// 或 https:// 开头的完整地址。");
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            throw new WebDavException("请先填写 WebDAV 账号。");
        }

        var folder = string.IsNullOrWhiteSpace(options.Folder) ? "/OpenCodeAccountManager/" : options.Folder.Trim();
        if (!folder.StartsWith('/')) { folder = "/" + folder; }
        if (!folder.EndsWith('/')) { folder += "/"; }

        _baseUri = baseUri;
        _folder = folder;
        _timeoutMs = options.TimeoutMs > 0 ? options.TimeoutMs : 30000;
        _maxDownloadBytes = options.MaxDownloadBytes > 0 ? options.MaxDownloadBytes : 20L * 1024 * 1024;
        _auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(options.Username + ":" + options.Password)));
        _http = http ?? new HttpClient();
    }

    /// <summary>True when the configured base URL uses HTTPS.</summary>
    public bool IsSecure => _baseUri.Scheme == Uri.UriSchemeHttps;

    public string Folder => _folder;

    /// <summary>Absolute URL of the sync folder (never contains credentials).</summary>
    public Uri FolderUri => BuildUri(_folder);

    /// <summary>
    /// Verifies the endpoint is a reachable WebDAV collection. Throws
    /// <see cref="WebDavException"/> with a friendly message on failure.
    /// </summary>
    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Parse("PROPFIND"), _baseUri, PropFindTestBody, "0", cancellationToken).ConfigureAwait(false);
        var code = (int)response.StatusCode;
        if (code is 200 or 207) { return; }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new WebDavException("WebDAV 认证失败，请检查账号和应用密码（不是登录密码）。");
        }

        throw new WebDavException("WebDAV 返回 HTTP " + code + "，请检查地址是否正确。");
    }

    /// <summary>Uploads (creates or replaces) <paramref name="fileName"/> inside the sync folder.</summary>
    public async Task UploadAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateFileName(fileName);
        await EnsureFolderAsync(cancellationToken).ConfigureAwait(false);

        using var response = await SendAsync(HttpMethod.Put, BuildUri(_folder + fileName), content, depth: null, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) { return; }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new WebDavException("WebDAV 认证失败，请检查账号和应用密码。");
        }

        throw new WebDavException("上传被拒绝：HTTP " + (int)response.StatusCode + "。");
    }

    /// <summary>Lists the JSON files in the sync folder (folders excluded).</summary>
    public async Task<IReadOnlyList<WebDavFile>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Parse("PROPFIND"), FolderUri, PropFindListBody, "1", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new WebDavException("WebDAV 认证失败，请检查账号和应用密码。");
        }

        if ((int)response.StatusCode == 404) { return Array.Empty<WebDavFile>(); }
        if ((int)response.StatusCode is not (200 or 207))
        {
            throw new WebDavException("读取云端目录失败：HTTP " + (int)response.StatusCode + "。");
        }

        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseList(xml);
    }

    /// <summary>Downloads <paramref name="fileName"/> from the sync folder, or null when missing.</summary>
    public async Task<byte[]?> DownloadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        ValidateFileName(fileName);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeoutMs);
        using var request = CreateRequest(HttpMethod.Get, BuildUri(_folder + fileName), depth: null);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebDavException("下载超时，请检查网络。");
        }
        catch (HttpRequestException ex)
        {
            throw new WebDavException("下载失败：" + ex.Message, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) { return null; }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new WebDavException("WebDAV 认证失败，请检查账号和应用密码。");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new WebDavException("下载被拒绝：HTTP " + (int)response.StatusCode + "。");
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared is > 0 && declared > _maxDownloadBytes)
            {
                throw new WebDavException("云端文件过大，已停止下载。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > _maxDownloadBytes)
                {
                    throw new WebDavException("云端文件过大，已停止下载。");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private async Task EnsureFolderAsync(CancellationToken cancellationToken)
    {
        var cumulative = new StringBuilder();
        foreach (var segment in _folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            cumulative.Append('/').Append(segment);
            using var response = await SendAsync(HttpMethod.Parse("MKCOL"), BuildUri(cumulative.ToString() + "/"), xmlBody: null, depth: null, cancellationToken).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            // 201 created; 405/301/302 = already exists (servers differ).
            if (response.IsSuccessStatusCode || code is 405 or 301 or 302 or 409) { continue; }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new WebDavException("WebDAV 认证失败，请检查账号和应用密码。");
            }

            throw new WebDavException("创建云端目录失败：HTTP " + code + "。");
        }
    }

    private static IReadOnlyList<WebDavFile> ParseList(string xml)
    {
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch { return Array.Empty<WebDavFile>(); }

        var files = new List<WebDavFile>();
        foreach (var response in document.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            if (response.Descendants().Any(e => e.Name.LocalName == "collection")) { continue; }

            var href = response.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) { continue; }

            var decoded = Uri.UnescapeDataString(href.Trim()).TrimEnd('/');
            var name = decoded[(decoded.LastIndexOf('/') + 1)..];
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.Length == 0) { continue; }

            var modifiedText = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "getlastmodified")?.Value;
            DateTimeOffset? modified = DateTimeOffset.TryParse(modifiedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
            var lengthText = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "getcontentlength")?.Value;
            var size = long.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize) ? parsedSize : 0;

            files.Add(new WebDavFile(name, modified, size));
        }

        return files;
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains('/') || fileName.Contains('\\') ||
            fileName is "." or ".." ||
            !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new WebDavException("云端文件名无效。");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string? xmlBody, string? depth, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeoutMs);
        using var request = CreateRequest(method, uri, depth);
        if (xmlBody is not null)
        {
            request.Content = new StringContent(xmlBody, new UTF8Encoding(false), "application/xml");
        }

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebDavException("连接 WebDAV 超时，请检查地址和网络。");
        }
        catch (HttpRequestException ex)
        {
            throw new WebDavException("无法连接 WebDAV：" + ex.Message, ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, byte[] content, string? depth, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeoutMs);
        using var request = CreateRequest(method, uri, depth);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebDavException("上传超时，请检查网络。");
        }
        catch (HttpRequestException ex)
        {
            throw new WebDavException("上传失败：" + ex.Message, ex);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string? depth)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = _auth;
        request.Headers.UserAgent.ParseAdd("OpenCodeAccountManager-Sync");
        if (depth is not null) { request.Headers.Add("Depth", depth); }
        return request;
    }

    private Uri BuildUri(string pathFromRoot)
    {
        var basePath = _baseUri.AbsolutePath.TrimEnd('/');
        var relative = pathFromRoot.StartsWith('/') ? pathFromRoot : "/" + pathFromRoot;
        return new Uri(_baseUri.GetLeftPart(UriPartial.Authority) + basePath + relative);
    }
}
