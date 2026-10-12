using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;

namespace AccountManager.Core.Accounts;

/// <summary>
/// Independent authorization-code/PKCE flow, following the public Codex/Cockpit protocol.
/// Does not invoke OpenCode's auto-activating connect flow, refresh tokens, or write Codex files.
/// Protocol reference: jlcodes99/cockpit-tools/src-tauri/src/modules/codex_oauth.rs.
/// </summary>
public sealed class CodexOAuthService : IDisposable
{
    internal const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    internal const string AuthorizeEndpoint = "https://auth.openai.com/oauth/authorize";
    internal const string TokenEndpoint = "https://auth.openai.com/oauth/token";
    private readonly HttpClient _client;
    private readonly SecretRegistry _secrets;
    private readonly bool _ownsClient;

    public CodexOAuthService(SecretRegistry secrets, bool useProxy = false, string? proxyUrl = null)
    {
        _secrets = secrets;
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = useProxy };
        if (useProxy && !string.IsNullOrWhiteSpace(proxyUrl)) { handler.Proxy = new WebProxy(proxyUrl); }
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = true;
    }

    internal CodexOAuthService(SecretRegistry secrets, HttpClient client)
    {
        _secrets = secrets;
        _client = client;
    }

    public CodexOAuthSession Start() => Start(new[] { 1455, 1457 }, TimeSpan.FromMinutes(10));

    internal CodexOAuthSession Start(IReadOnlyList<int> ports, TimeSpan lifetime)
    {
        TcpListener? listener = null;
        foreach (var port in ports)
        {
            var candidate = new TcpListener(IPAddress.Loopback, port);
            if (OperatingSystem.IsWindows()) { candidate.Server.ExclusiveAddressUse = true; }
            else
            {
                // Unix needs SO_REUSEADDR to rebind after a completed connection's TIME_WAIT.
                // SO_REUSEPORT must stay off: another live listener could otherwise share the port.
                SetUnixReuseAddress(candidate.Server);
            }
            try { candidate.Start(8); listener = candidate; break; }
            catch (SocketException) { candidate.Stop(); }
        }
        if (listener is null)
        {
            throw new OpenCodeException("OAuth 回调端口 1455 和 1457 均不可用。请关闭其它正在授权的窗口后重试；本工具不会结束其它进程。");
        }
        return new CodexOAuthSession(this, listener, _secrets, lifetime);
    }

    private const int SolSocket = 1;
    private const int SoReuseAddr = 2;

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int sockfd, int level, int optname, ref int optval, uint optlen);

    /// <summary>
    /// Sets the real SO_REUSEADDR through libc. The managed <see cref="SocketOptionName.ReuseAddress"/>
    /// option is mapped to SO_REUSEPORT on Linux, which lets a second live listener share the callback port.
    /// </summary>
    private static void SetUnixReuseAddress(Socket socket)
    {
        var handle = socket.SafeHandle;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var enabled = 1;
            if (setsockopt((int)handle.DangerousGetHandle(), SolSocket, SoReuseAddr, ref enabled, sizeof(int)) != 0)
            {
                throw new SocketException(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            if (added) { handle.DangerousRelease(); }
        }
    }

    internal async Task<JsonObject> ExchangeAsync(string code, string verifier, string redirectUri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400) { throw new OpenCodeException("令牌接口返回重定向，已安全停止；请重新授权。"); }
            if (!response.IsSuccessStatusCode)
            {
                throw new OpenCodeException("OpenAI 令牌交换失败（HTTP " + (int)response.StatusCode + "），授权码可能已过期或已使用；请重新授权。");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > 1024 * 1024) { throw new OpenCodeException("令牌响应过大，已停止解析。"); }
                memory.Write(buffer, 0, read);
            }
            JsonObject tokens;
            try { tokens = JsonNode.Parse(memory.ToArray()) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { throw new OpenCodeException("令牌响应格式无效，未保存任何账号。"); }
            _secrets.Register(tokens);
            return AccountFromTokens(tokens);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new OpenCodeException("OpenAI 令牌交换超时，请检查网络和系统代理设置后重新授权。");
        }
        catch (HttpRequestException)
        {
            throw new OpenCodeException("无法连接 OpenAI 令牌接口，请检查网络和系统代理设置后重新授权。");
        }
    }

    internal static JsonObject AccountFromTokens(JsonObject tokens)
    {
        var access = JsonMerge.GetString(tokens, "access_token");
        var refresh = JsonMerge.GetString(tokens, "refresh_token");
        var idToken = JsonMerge.GetString(tokens, "id_token") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
        {
            throw new OpenCodeException("授权结果缺少访问令牌或刷新令牌，未保存账号。");
        }
        var accessClaims = Jwt.ReadPayload(access);
        var idClaims = Jwt.ReadPayload(idToken);
        var accessAuth = JsonMerge.Get(accessClaims, "https://api.openai.com/auth");
        var idAuth = JsonMerge.Get(idClaims, "https://api.openai.com/auth");
        var accessId = JsonMerge.GetString(accessAuth, "chatgpt_account_id");
        var id = JsonMerge.GetString(idAuth, "chatgpt_account_id") ?? accessId;
        var email = JsonMerge.GetString(idClaims, "email")
            ?? JsonMerge.GetString(JsonMerge.Get(accessClaims, "https://api.openai.com/profile"), "email")
            ?? JsonMerge.GetString(accessClaims, "email");
        var exp = JsonNum.ToLong(JsonMerge.Get(accessClaims, "exp"));
        if (string.IsNullOrWhiteSpace(id) || !CredentialHelpers.IsEmail(email)
            || exp is null || exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || exp > 253402300799
            || (!string.IsNullOrWhiteSpace(accessId) && id != accessId))
        {
            throw new OpenCodeException("授权结果的邮箱、工作区或令牌到期时间无法安全确认，未保存账号。");
        }
        return new JsonObject
        {
            ["auth_mode"] = "oauth",
            ["email"] = email,
            ["account_id"] = id,
            ["plan_type"] = JsonMerge.GetString(idAuth, "chatgpt_plan_type") ?? JsonMerge.GetString(accessAuth, "chatgpt_plan_type"),
            ["tokens"] = new JsonObject { ["access_token"] = access, ["refresh_token"] = refresh, ["id_token"] = idToken },
        };
    }

    public void Dispose() { if (_ownsClient) { _client.Dispose(); } }
}

/// <summary>One short-lived loopback listener. Automatic and manual callbacks use the same validator.</summary>
public sealed class CodexOAuthSession : IAsyncDisposable
{
    private readonly CodexOAuthService _service;
    private readonly TcpListener _listener;
    private readonly SecretRegistry _secrets;
    private readonly string _state;
    private readonly string _verifier;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<JsonObject> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _registration;
    private readonly Task _listenerTask;
    private readonly object _submissionLock = new();
    private Task? _exchangeTask;
    private int _cancelled;
    private int _disposed;

    internal CodexOAuthSession(CodexOAuthService service, TcpListener listener, SecretRegistry secrets, TimeSpan lifetime)
    {
        _service = service;
        _listener = listener;
        _secrets = secrets;
        _state = Base64Url(RandomNumberGenerator.GetBytes(32));
        _verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        RedirectUri = "http://localhost:" + port + "/auth/callback";
        ExpiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = CodexOAuthService.ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier))),
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = _state,
            ["originator"] = "opencode",
        };
        AuthorizationUrl = CodexOAuthService.AuthorizeEndpoint + "?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        _secrets.RegisterValue(_state);
        _secrets.RegisterValue(_verifier);
        _secrets.RegisterValue(AuthorizationUrl);
        _registration = _lifetime.Token.Register(() =>
        {
            _listener.Stop();
            _completion.TrySetException(new OpenCodeException(Volatile.Read(ref _cancelled) == 1
                ? "已取消授权，未保存账号。" : "授权已超时，请重新生成授权链接。"));
        });
        _lifetime.CancelAfter(lifetime);
        _listenerTask = ListenAsync();
    }

    public string AuthorizationUrl { get; }
    public string RedirectUri { get; }
    public DateTimeOffset ExpiresAt { get; }
    /// <summary>Contains tokens. Only consume to create a preview, never display/log this result.</summary>
    public Task<JsonObject> Completion => _completion.Task;

    public async Task SubmitCallbackAsync(string callbackUrl)
    {
        _secrets.RegisterValue(callbackUrl);
        if (Volatile.Read(ref _disposed) != 0 || _lifetime.IsCancellationRequested) { throw new OpenCodeException("此授权已取消或超时，请重新授权。"); }
        var query = ValidateCallback(callbackUrl);
        lock (_submissionLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || _lifetime.IsCancellationRequested) { throw new OpenCodeException("此授权已取消或超时，请重新授权。"); }
            // The first accepted callback owns the outcome. A concurrent denial or second
            // code cannot cancel/replace an exchange that is already in flight.
            if (_exchangeTask is null)
            {
                if (query.TryGetValue("error", out _))
                {
                    var denied = new OpenCodeException("OpenAI 授权被拒绝或取消，未保存账号。");
                    _completion.TrySetException(denied);
                    _lifetime.Cancel();
                    throw denied;
                }
                if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 8192)
                {
                    throw new OpenCodeException("回调地址缺少有效授权码，请粘贴浏览器中的完整回调地址。");
                }
                _secrets.RegisterValue(code);
                _exchangeTask = ExchangeAndCompleteAsync(code);
            }
        }
        await Completion.ConfigureAwait(false);
    }

    private async Task ExchangeAndCompleteAsync(string code)
    {
        try
        {
            var account = await _service.ExchangeAsync(code, _verifier, RedirectUri, _lifetime.Token).ConfigureAwait(false);
            _completion.TrySetResult(account);
        }
        catch (OperationCanceledException)
        {
            _completion.TrySetException(new OpenCodeException("授权已取消或超时，请重新授权。"));
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex is OpenCodeException ? ex : new OpenCodeException("令牌交换失败，请重新授权。"));
        }
        finally { _lifetime.Cancel(); }
    }

    internal Dictionary<string, string> ValidateCallback(string text)
    {
        if (text.Length > 16384 || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "http" || (uri.Host != "localhost" && uri.Host != "127.0.0.1")
            || uri.Port != new Uri(RedirectUri).Port || uri.AbsolutePath != "/auth/callback"
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new OpenCodeException("回调地址必须是本次授权使用的本机 /auth/callback 完整地址。");
        }
        var values = ParseQuery(uri.Query);
        if (!values.TryGetValue("state", out var state)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(_state)))
        {
            throw new OpenCodeException("回调 state 与本次授权不匹配；旧链接或其它窗口的回调不能使用。");
        }
        return values;
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace("+", " "));
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : string.Empty;
            if (!values.TryAdd(key, value)) { throw new OpenCodeException("回调含有重复参数，已拒绝处理。"); }
        }
        return values;
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                await HandleRequestAsync(client).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                _completion.TrySetException(new OpenCodeException("本机回调监听中断；请使用手动回调地址，或重新授权。"));
                _lifetime.Cancel();
            }
        }
    }

    private async Task HandleRequestAsync(TcpClient client)
    {
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        var stream = client.GetStream();
        var bytes = new byte[16384];
        var count = 0;
        try
        {
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), readTimeout.Token).ConfigureAwait(false);
                if (read == 0) { return; }
                count += read;
                if (Encoding.ASCII.GetString(bytes, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) { break; }
            }
            var headers = Encoding.ASCII.GetString(bytes, 0, count).Split("\r\n");
            var first = headers[0].Split(' ');
            var host = headers.FirstOrDefault(h => h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
            var port = new Uri(RedirectUri).Port;
            if (count == bytes.Length || first.Length != 3 || first[0] != "GET" || !first[1].StartsWith("/auth/callback?", StringComparison.Ordinal)
                || (host != "localhost:" + port && host != "127.0.0.1:" + port))
            {
                await RespondAsync(stream, false, "无效回调请求。请返回账号管理工具。", readTimeout.Token).ConfigureAwait(false);
                return;
            }
            try
            {
                await SubmitCallbackAsync(new Uri(new Uri(RedirectUri), first[1]).AbsoluteUri).ConfigureAwait(false);
                // Do not echo query/code/state into HTML or logs.
                await RespondAsync(stream, true, "授权已完成，请返回 OpenCode 账号管理工具确认保存。", CancellationToken.None).ConfigureAwait(false);
            }
            catch (OpenCodeException)
            {
                await RespondAsync(stream, false, "回调未完成，请返回账号管理工具查看提示。", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
            // A disconnected/slow browser must not fail a still-valid manual authorization.
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException)
        {
            // Malformed HTTP input is not a reason to terminate the loopback listener.
        }
    }

    private static async Task RespondAsync(NetworkStream stream, bool success, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>OpenCode 账号授权</title><body><p>" + message + "</p></body></html>");
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (success ? "200 OK" : "400 Bad Request")
            + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length
            + "\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\n\r\n");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
        await stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        Interlocked.Exchange(ref _cancelled, 1);
        _lifetime.Cancel();
        Task? exchange;
        lock (_submissionLock) { exchange = _exchangeTask; }
        await _listenerTask.ConfigureAwait(false);
        // Manual submission can be independent of the listener. Let its cancellation finish
        // before disposing the token source/client, including the exchange's finally block.
        if (exchange is not null) { await exchange.ConfigureAwait(false); }
        // Observe cancellation/expiry when the caller closed before awaiting Completion.
        if (Completion.IsFaulted) { _ = Completion.Exception; }
        _registration.Dispose();
        _lifetime.Dispose();
    }
}
