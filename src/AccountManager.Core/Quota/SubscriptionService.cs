using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Json;
using AccountManager.Core.OpenCode;

namespace AccountManager.Core.Quota;

/// <summary>Live ChatGPT subscription snapshot: the expiry and (best-effort) plan type.</summary>
public sealed class SubscriptionStatus
{
    public string? PlanType { get; init; }

    public DateTimeOffset? ActiveUntil { get; init; }
}

/// <summary>
/// Live subscription lookup for one Codex account. The request is the same
/// <c>accounts/check/v4-2023-04-27</c> call the ChatGPT web app uses, with a missing or empty expiry
/// falling back to <c>subscriptions?account_id=</c>.
/// <para>
/// On some networks Cloudflare challenges the managed .NET TLS fingerprint with
/// <c>cf-mitigated: challenge</c> (HTTP 403) while a normal browser / curl handshake passes. So the
/// request is sent with the Windows built-in <c>curl.exe</c> when it is available (the token travels
/// over stdin, never as a process argument), and the managed <see cref="HttpClient"/> is kept as a
/// fallback. Both paths return a bounded, parsed JSON body; failures never leak the token.
/// </para>
/// </summary>
public class SubscriptionService
{
    public const string AccountsCheckUri = "https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27";
    public const string SubscriptionsUri = "https://chatgpt.com/backend-api/subscriptions";
    public const int TimeoutSeconds = 18;
    public const int MaxBytes = 1048576;

    /// <summary>Sentinel curl appends after the body so the HTTP status survives <c>-o -</c>.</summary>
    public const string CurlStatusMarker = "___HTTP__";

    /// <summary>One parsed curl stdout frame: a valid trailing status suffix and a body within cap.</summary>
    internal readonly record struct CurlStdoutFrame(bool Ok, int Status, byte[] Body);

    /// <summary>Raw stdout cap: the body cap plus room for the marker/three digits/"___" suffix.</summary>
    internal const int MaxCurlStdoutBytes = MaxBytes + 64;

    private static readonly HttpClient Client = CreateClient();
    private static readonly string? CurlPath = FindCurl();

    private readonly CurlProcessFactory? _curlFactory;
    private readonly string? _curlExecutable;

    /// <summary>Production transport: the discovered system curl and the real process host.</summary>
    public SubscriptionService() : this(null, null) { }

    /// <summary>Test/DI seam. Passing <c>null</c> keeps the production factory and discovered curl.</summary>
    internal SubscriptionService(CurlProcessFactory? curlFactory, string? curlExecutable)
    {
        _curlFactory = curlFactory;
        _curlExecutable = curlExecutable;
    }

    /// <summary>True when a usable <c>curl</c> was found on this machine.</summary>
    public static bool CurlAvailable => CurlPath is not null;

    /// <summary>Whether to try the system-curl transport first. Overridable so tests stay offline.</summary>
    protected virtual bool TrySystemCurl => (_curlExecutable ?? CurlPath) is not null;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        return new HttpClient(handler);
    }

    public virtual async Task<SubscriptionStatus> GetSubscriptionAsync(string accessToken, string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) { throw new InvalidOperationException("缺少访问令牌。"); }
        // Reject header-control characters before *either* transport can launch (curl via stdin config
        // or the managed HttpClient), so a CR/LF can never inject a header or a curl directive.
        RequireHeaderSafe(accessToken);
        RequireHeaderSafe(accountId);

        Exception? failure = null;
        SubscriptionStatus? primary = null;
        try
        {
            var json = await FetchAsync(AccountsCheckUrl(), accessToken, accountId, cancellationToken).ConfigureAwait(false);
            primary = ParseAccountsCheck(json, accountId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Missing OR already expired → ask the subscriptions endpoint. The web client does the same
        // (it treats a missing/expired accounts/check value as stale), so a renewed plan is picked up
        // instead of being shadowed by an expired snapshot.
        var now = DateTimeOffset.UtcNow;
        if (primary?.ActiveUntil is DateTimeOffset primaryUntil && primaryUntil > now) { return primary; }

        try
        {
            var json = await FetchAsync(SubscriptionsUrl(accountId), accessToken, accountId, cancellationToken).ConfigureAwait(false);
            var fallback = ParseSubscriptions(json);
            if (fallback.ActiveUntil is not null) { return fallback; }
            return primary ?? fallback;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // We only reach the fallback when accounts/check had no *usable* expiry, so if the
            // fallback also fails there is no authoritative answer: surface the failure so the caller
            // keeps the current value instead of clearing it or overwriting it with a stale date.
            throw failure ?? ex;
        }
    }

    private static string AccountsCheckUrl()
    {
        // West-positive minutes, matching the ChatGPT web client / Cockpit. The value only nudges
        // the timezone the endpoint formats dates in; the expiry itself is absolute.
        var offset = -(int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes);
        return AccountsCheckUri + "?timezone_offset_min=" + offset.ToString(CultureInfo.InvariantCulture);
    }

    private static string SubscriptionsUrl(string accountId)
        => SubscriptionsUri + "?account_id=" + Uri.EscapeDataString(accountId ?? string.Empty);

    /// <summary>
    /// Fetches the JSON body, preferring the system <c>curl.exe</c> whose TLS fingerprint is not
    /// challenged, and falling back to the managed HTTP client.
    /// </summary>
    private async Task<JsonNode> FetchAsync(string url, string accessToken, string accountId, CancellationToken cancellationToken)
    {
        Exception? curlError = null;
        if (TrySystemCurl)
        {
            var (json, error) = await RunCurlAsync(url, accessToken, accountId, cancellationToken).ConfigureAwait(false);
            if (json is not null) { return json; }
            curlError = error;
        }

        try
        {
            return await SendAsync(url, accessToken, accountId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw curlError ?? ex;
        }
    }

    /// <summary>Replaceable HTTP boundary. Returns the decoded JSON body.</summary>
    protected virtual async Task<JsonNode> SendAsync(string url, string accessToken, string accountId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        if (!string.IsNullOrWhiteSpace(accountId)) { request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId); }
        request.Headers.TryAddWithoutValidation("Origin", "https://chatgpt.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://chatgpt.com/");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(ResolveError(0));
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(ResolveError(0));
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                throw new InvalidOperationException("请求被重定向，出于安全考虑已中止。");
            }

            if (status is < 200 or >= 300)
            {
                throw new InvalidOperationException(ResolveError(status));
            }

            var text = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) { throw new InvalidOperationException("响应为空。"); }
            try
            {
                return JsonNode.Parse(text) ?? throw new InvalidOperationException("响应解析失败：接口可能已变更。");
            }
            catch (System.Text.Json.JsonException)
            {
                throw new InvalidOperationException("响应解析失败：接口可能已变更。");
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxBytes) { throw new InvalidOperationException("响应过大，已中止解析。"); }
            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    /// <summary>Runs the request through the system curl, feeding headers (incl. the token) via stdin.</summary>
    private async Task<(JsonNode? Json, Exception? Error)> RunCurlAsync(string url, string accessToken, string accountId, CancellationToken cancellationToken)
    {
        var executable = _curlExecutable ?? CurlPath;
        if (executable is null) { return (null, new InvalidOperationException(ResolveError(0))); }

        string config;
        try
        {
            var path = new Uri(url).AbsolutePath;
            config = BuildCurlStdinConfig(accessToken, accountId, path);
        }
        catch (Exception)
        {
            return (null, new InvalidOperationException(ResolveError(0)));
        }

        if (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }

        var proxy = ResolveProxy(url);
        CurlFetchResult result;
        try
        {
            var transport = new SystemCurlTransport(_curlFactory);
            result = await transport
                .FetchAsync(executable, url, proxy, config, budget: null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return (null, new InvalidOperationException(ResolveError(0)));
        }

        if (!result.TransportOk)
        {
            // A non-zero curl exit, an oversize/truncated body, a missing suffix or a budget kill all
            // land here; the status is only trustworthy when the transport declared the frame valid.
            var message = result.Status > 0 ? ResolveError(result.Status) : ResolveError(0);
            return (null, new InvalidOperationException(message));
        }

        if (result.Status is >= 200 and < 300)
        {
            try
            {
                return (JsonNode.Parse(result.Body), null);
            }
            catch (System.Text.Json.JsonException)
            {
                return (null, new InvalidOperationException("响应解析失败：接口可能已变更。"));
            }
        }

        return (null, new InvalidOperationException(ResolveError(result.Status)));
    }

    /// <summary>Builds the <c>--config -</c> stdin body. Values are escaped; control chars are rejected.</summary>
    internal static string BuildCurlStdinConfig(string accessToken, string accountId, string targetPath)
    {
        var config = new StringBuilder();
        AppendHeader(config, "Authorization", "Bearer " + accessToken);
        AppendHeader(config, "Accept", "application/json");
        AppendHeader(config, "Referer", "https://chatgpt.com/");
        AppendHeader(config, "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        AppendHeader(config, "x-openai-target-path", targetPath);
        AppendHeader(config, "x-openai-target-route", targetPath);
        if (!string.IsNullOrWhiteSpace(accountId)) { AppendHeader(config, "ChatGPT-Account-Id", accountId); }
        return config.ToString();
    }

    /// <summary>Builds the curl process arguments: curlrc disabled, config/token over stdin, url last.</summary>
    internal static ProcessStartInfo BuildCurlStartInfo(string executable, string url, string? proxy)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        // -q first: never read a user curlrc. --config -: token only over stdin, never argv. No -L:
        // a redirect is not followed (the managed transport rejects 3xx, curl must too).
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add("-");
        psi.ArgumentList.Add("-sS");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("-");
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("\n" + CurlStatusMarker + "%{http_code}___");
        psi.ArgumentList.Add("--max-time");
        psi.ArgumentList.Add(TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        if (!OperatingSystem.IsWindows())
        {
            // On Linux, curl's default ClientHello (TLS 1.2 + 1.3, OpenSSL) is answered with a Cloudflare
            // challenge (HTTP 403, cf-mitigated: challenge) on chatgpt.com/backend-api, so the subscription
            // lookup never reaches the API. Pinning a single TLS version gets through. Windows curl.exe
            // (Schannel) is not challenged and keeps its default handshake.
            psi.ArgumentList.Add("--tlsv1.3");
        }

        if (proxy is not null)
        {
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add(proxy);
        }

        psi.ArgumentList.Add(url);
        return psi;
    }

    /// <summary>
    /// Strictly frames raw curl stdout: the last <see cref="CurlStatusMarker"/> must be followed by
    /// exactly three ASCII digits and <c>"___"</c> (optionally one trailing CR/LF), and the parsed
    /// body must be no larger than <see cref="MaxBytes"/>. Body bytes are never decoded here.
    /// </summary>
    internal static CurlStdoutFrame FrameCurlStdout(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0) { return new CurlStdoutFrame(false, 0, Array.Empty<byte>()); }

        var marker = Encoding.ASCII.GetBytes(CurlStatusMarker);
        var index = LastIndexOf(raw, marker);
        if (index < 0) { return new CurlStdoutFrame(false, 0, Array.Empty<byte>()); }

        var tail = raw[(index + marker.Length)..];
        if (tail.Length < 6) { return new CurlStdoutFrame(false, 0, Array.Empty<byte>()); }
        if (tail[0] is < (byte)'0' or > (byte)'9'
            || tail[1] is < (byte)'0' or > (byte)'9'
            || tail[2] is < (byte)'0' or > (byte)'9'
            || tail[3] != (byte)'_' || tail[4] != (byte)'_' || tail[5] != (byte)'_')
        {
            return new CurlStdoutFrame(false, 0, Array.Empty<byte>());
        }

        var trailing = tail[6..];
        var trailingOk = trailing.Length == 0
            || (trailing.Length == 1 && (trailing[0] == (byte)'\n' || trailing[0] == (byte)'\r'))
            || (trailing.Length == 2 && trailing[0] == (byte)'\r' && trailing[1] == (byte)'\n');
        if (!trailingOk) { return new CurlStdoutFrame(false, 0, Array.Empty<byte>()); }

        var status = ((tail[0] - (byte)'0') * 100) + ((tail[1] - (byte)'0') * 10) + (tail[2] - (byte)'0');

        // Strip exactly the single LF separator curl writes before the marker. A CR belonging to the
        // body (e.g. a payload that ends CRLF) is part of the body and must be preserved.
        var bodyEnd = index;
        if (bodyEnd > 0 && raw[bodyEnd - 1] == (byte)'\n') { bodyEnd--; }

        // Enforce the body cap before allocating/copying the bytes.
        if (bodyEnd > MaxBytes) { return new CurlStdoutFrame(false, status, Array.Empty<byte>()); }
        var body = raw[..bodyEnd].ToArray();
        return new CurlStdoutFrame(true, status, body);
    }

    private static int LastIndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) { return i; }
        }

        return -1;
    }

    /// <summary>Rejects a header value carrying CR/LF or any other control character.</summary>
    private static void RequireHeaderSafe(string? value)
    {
        if (string.IsNullOrEmpty(value)) { return; }
        foreach (var ch in value)
        {
            if (char.IsControl(ch)) { throw new InvalidOperationException("请求头包含非法控制字符。"); }
        }
    }

    private static void AppendHeader(StringBuilder builder, string name, string value)
    {
        RequireHeaderSafe(value);
        builder.Append("header = \"").Append(name).Append(": ").Append(Escape(value)).Append("\"\n");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Splits curl's <c>-o - -w</c> output into the HTTP status and the body.</summary>
    public static (int Status, string Body) ParseCurlOutput(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) { return (0, string.Empty); }
        var index = stdout.LastIndexOf(CurlStatusMarker, StringComparison.Ordinal);
        if (index < 0) { return (0, stdout); }

        var tail = stdout[(index + CurlStatusMarker.Length)..];
        var close = tail.IndexOf("___", StringComparison.Ordinal);
        var codeText = close < 0 ? tail : tail[..close];
        var body = stdout[..index];
        if (body.EndsWith('\n')) { body = body[..^1]; }
        if (body.EndsWith('\r')) { body = body[..^1]; }
        return (int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : 0, body);
    }

    private static string? ResolveProxy(string url)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            if (proxy is null) { return null; }
            var request = new Uri(url);
            var resolved = proxy.GetProxy(request);
            if (resolved is null || resolved == request) { return null; }
            return resolved.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// OS-parameterised discovery. Windows prefers <paramref name="systemDirectory"/>\curl.exe then
    /// PATH entries; Unix probes only absolute PATH entries for a regular, executably-accessible file
    /// named exactly <c>curl</c>. <paramref name="isUsableExecutable"/> is the usability predicate
    /// (the Linux default reuses the Task 3 regular-file/effective-access validator).
    /// </summary>
    internal static string? FindCurl(
        bool windows,
        IReadOnlyList<string> pathDirectories,
        string? systemDirectory,
        Func<string, bool>? isUsableExecutable = null)
    {
        var usable = isUsableExecutable ?? DefaultCurlUsable;
        if (windows)
        {
            if (!string.IsNullOrWhiteSpace(systemDirectory))
            {
                var candidate = JoinWindows(systemDirectory!, "curl.exe");
                if (usable(candidate)) { return candidate; }
            }

            foreach (var dir in pathDirectories ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(dir)) { continue; }
                var candidate = JoinWindows(dir, "curl.exe");
                if (usable(candidate)) { return candidate; }
            }

            return null;
        }

        foreach (var dir in pathDirectories ?? Array.Empty<string>())
        {
            // A relative or empty segment must never become a process path.
            if (string.IsNullOrWhiteSpace(dir) || dir[0] != '/') { continue; }
            var candidate = JoinPosix(dir, "curl");
            if (usable(candidate)) { return candidate; }
        }

        return null;
    }

    private static string JoinWindows(string directory, string name)
        => directory.TrimEnd('\\', '/') + "\\" + name;

    private static string JoinPosix(string directory, string name)
        => directory.TrimEnd('/') + "/" + name;

    private static bool DefaultCurlUsable(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) { return File.Exists(path); }
            return LinuxOpenCodeDiscovery.ResolveOrdinaryExecutable(path) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindCurl()
    {
        try
        {
            var windows = OperatingSystem.IsWindows();
            var path = Environment.GetEnvironmentVariable("PATH");
            var directories = string.IsNullOrEmpty(path)
                ? Array.Empty<string>()
                : path.Split(Path.PathSeparator);
            var system = windows ? Environment.GetFolderPath(Environment.SpecialFolder.System) : null;
            return FindCurl(windows, directories, system);
        }
        catch
        {
            // Ignore lookup failures; the managed fallback still runs.
        }

        return null;
    }

    public static string ResolveError(int status) => status switch
    {
        401 => "令牌被拒绝：访问令牌可能已过期，请先在 OpenCode 中刷新后重试。",
        403 => "访问被拒绝：可能无权限、网络被拦截，或订阅接口被风控校验拦截。",
        404 => "接口不存在（HTTP 404），订阅刷新可能暂不可用。",
        429 => "请求过于频繁（HTTP 429），请稍后重试。",
        >= 500 and <= 599 => "服务暂时不可用（HTTP " + status + "），请稍后重试。",
        > 0 => "请求失败（HTTP " + status + "）。",
        _ => "请求失败：网络不可达或超时。",
    };

    // ---- pure parsing -------------------------------------------------------

    /// <summary>
    /// Parses <c>accounts/check</c>. Picks the entry belonging to <paramref name="accountId"/> so a
    /// personal/team workspace is never crossed. When <paramref name="accountId"/> is blank (the
    /// caller does not know the workspace) it falls back to a default non-free entry, then any
    /// non-free entry, then any entry that carries an expiry. When an id <em>was</em> requested but
    /// does not appear in the payload it returns nothing (displayed as "—") rather than another
    /// workspace's date. Accepts <c>accounts</c> as an object (keyed by account id) or an array.
    /// </summary>
    public static SubscriptionStatus ParseAccountsCheck(JsonNode? json, string accountId)
    {
        var accounts = JsonMerge.Get(json, "accounts");
        if (accounts is null) { return new SubscriptionStatus(); }

        var entries = new List<AccountEntry>();
        if (accounts is JsonObject obj)
        {
            foreach (var (key, node) in obj) { entries.Add(ReadAccountEntry(node, key)); }
        }
        else if (accounts is JsonArray array)
        {
            foreach (var node in array) { entries.Add(ReadAccountEntry(node, null)); }
        }

        if (entries.Count == 0) { return new SubscriptionStatus(); }

        AccountEntry? chosen = null;
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            // A specific workspace was requested: only its own entry is acceptable. A missing match
            // means "unknown", never another workspace's expiry.
            chosen = entries.FirstOrDefault(e => e.Id is not null && string.Equals(e.Id, accountId, StringComparison.Ordinal));
        }
        else
        {
            chosen ??= entries.FirstOrDefault(e => e.Expires is not null && e.Default && !e.Free);
            chosen ??= entries.FirstOrDefault(e => e.Expires is not null && !e.Free);
            chosen ??= entries.FirstOrDefault(e => e.Expires is not null);
        }

        if (chosen is null) { return new SubscriptionStatus(); }

        return new SubscriptionStatus { ActiveUntil = chosen.Expires, PlanType = chosen.Plan };
    }

    /// <summary>Parses the <c>subscriptions?account_id=</c> fallback payload.</summary>
    public static SubscriptionStatus ParseSubscriptions(JsonNode? json)
    {
        var node = JsonMerge.Get(json, "data") ?? json;
        if (node is JsonArray array)
        {
            node = array.Count > 0 ? array[0] : null;
        }

        var expires = FirstTime(
            JsonMerge.Get(node, "active_until"),
            JsonMerge.Get(node, "expires_at"),
            JsonMerge.Get(node, "subscription_active_until"),
            JsonMerge.Get(node, "subscriptionActiveUntil"));
        var plan = JsonMerge.GetString(node, "plan_type") ?? JsonMerge.GetString(node, "plan");
        return new SubscriptionStatus { ActiveUntil = expires, PlanType = plan };
    }

    private sealed record AccountEntry(string? Id, bool Default, bool Free, DateTimeOffset? Expires, string? Plan);

    private static AccountEntry ReadAccountEntry(JsonNode? node, string? fallbackId)
    {
        var account = JsonMerge.Get(node, "account");
        var entitlement = JsonMerge.Get(node, "entitlement");
        var id = JsonMerge.GetString(account, "account_id")
            ?? JsonMerge.GetString(entitlement, "account_id")
            ?? JsonMerge.GetString(node, "account_id")
            ?? fallbackId;
        var expires = FirstTime(
            JsonMerge.Get(entitlement, "expires_at"),
            JsonMerge.Get(account, "expires_at"),
            JsonMerge.Get(node, "expires_at"),
            JsonMerge.Get(entitlement, "active_until"),
            JsonMerge.Get(node, "active_until"));
        var plan = JsonMerge.GetString(entitlement, "subscription_plan")
            ?? JsonMerge.GetString(account, "plan_type")
            ?? JsonMerge.GetString(entitlement, "plan_type")
            ?? JsonMerge.GetString(node, "plan_type");
        var isDefault = JsonNum.ToBool(JsonMerge.Get(account, "is_default")) || JsonNum.ToBool(JsonMerge.Get(node, "is_default"));
        var isFree = JsonNum.ToBool(JsonMerge.Get(account, "is_free")) || JsonNum.ToBool(JsonMerge.Get(node, "is_free"));
        return new AccountEntry(id, isDefault, isFree, expires, plan);
    }

    private static DateTimeOffset? FirstTime(params JsonNode?[] nodes)
    {
        foreach (var node in nodes)
        {
            var parsed = QuotaService.ConvertResetTime(node);
            if (parsed is not null) { return parsed; }
        }

        return null;
    }
}

/// <summary>
/// Result of one curl request. <see cref="TransportOk"/> is true only when curl exited 0, the stdin
/// config was written and closed, stdout framed cleanly and the parsed body fit <see cref="SubscriptionService.MaxBytes"/>.
/// </summary>
internal readonly record struct CurlFetchResult(bool TransportOk, int Status, byte[] Body, string? Error);

/// <summary>
/// The small process boundary the production curl path talks to. Production wraps a real
/// <see cref="Process"/>; tests inject a fake so no child process is ever launched.
/// </summary>
internal interface ICurlProcessHost : IAsyncDisposable
{
    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Kills only the child this host created, never a process tree or a process by name.</summary>
    void KillOwned();
}

internal delegate ICurlProcessHost CurlProcessFactory(ProcessStartInfo startInfo);

/// <summary>
/// Deterministic curl runner: writes the stdin config while draining stdout and stderr concurrently,
/// all under one monotonic budget. Oversize output, a write failure, cancellation or budget expiry
/// stop at once (killing only the owned child) and report a fixed, non-leaking error.
/// </summary>
internal sealed class SystemCurlTransport
{
    private const int StderrCap = 8192;

    private static readonly string GenericFailure = SubscriptionService.ResolveError(0);

    private readonly CurlProcessFactory _factory;

    internal SystemCurlTransport(CurlProcessFactory? factory = null)
        => _factory = factory ?? DefaultFactory;

    private static ICurlProcessHost DefaultFactory(ProcessStartInfo startInfo) => new ProcessCurlHost(startInfo);

    internal async Task<CurlFetchResult> FetchAsync(
        string executable,
        string url,
        string? proxy,
        string stdinConfig,
        TimeSpan? budget = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }

        var total = budget ?? TimeSpan.FromSeconds(SubscriptionService.TimeoutSeconds);
        var clock = Stopwatch.StartNew();

        ProcessStartInfo startInfo;
        try { startInfo = SubscriptionService.BuildCurlStartInfo(executable, url, proxy); }
        catch { return Failure(); }

        ICurlProcessHost host;
        try { host = _factory(startInfo); }
        catch (OperationCanceledException) { throw; }
        catch { return Failure(); }

        var killed = 0;
        void KillOnce()
        {
            if (Interlocked.Exchange(ref killed, 1) == 0)
            {
                try { host.KillOwned(); } catch { }
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<PipeRead>? stdoutTask = null;
        Task<bool>? stderrTask = null;
        Task? stdinTask = null;
        Task? exitTask = null;
        Task? pipesTask = null;

        void ObserveStarted() => Observe(exitTask, stdoutTask, stderrTask, stdinTask, pipesTask);

        CurlFetchResult Abort()
        {
            KillOnce();
            try { deadline.Cancel(); } catch { }
            ObserveStarted();
            // A caller cancellation is safe to surface; anything else is a fixed failure.
            if (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
            return Failure();
        }

        try
        {
            // The single budget covers factory/startup too: subtract whatever it already spent.
            var remaining = total - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) { KillOnce(); return Failure(); }
            deadline.CancelAfter(remaining);

            var readToken = deadline.Token;
            stdoutTask = ReadCappedAsync(host.StandardOutput, SubscriptionService.MaxCurlStdoutBytes, readToken);
            stderrTask = DrainCappedAsync(host.StandardError, StderrCap, readToken);
            stdinTask = WriteAllAsync(host.StandardInput, stdinConfig, readToken);
            exitTask = host.WaitForExitAsync(readToken);

            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Fail() => failure.TrySetResult(true);
            WatchFailure(stdoutTask, () => stdoutTask!.Result.Exceeded, Fail);
            WatchFailure(stderrTask, () => stderrTask!.Result, Fail);
            WatchFailure(stdinTask, () => false, Fail);
            WatchFailure(exitTask, () => false, Fail);

            var deadlineTask = Task.Delay(Timeout.Infinite, readToken);

            var first = await Task.WhenAny(exitTask, failure.Task, deadlineTask).ConfigureAwait(false);
            if (ReferenceEquals(first, failure.Task) || ReferenceEquals(first, deadlineTask)
                || exitTask.IsFaulted || exitTask.IsCanceled)
            {
                return Abort();
            }

            // curl has exited: the three pipes must still finish cleanly within the same budget.
            pipesTask = Task.WhenAll(stdoutTask!, stderrTask!, stdinTask!);
            var second = await Task.WhenAny(pipesTask, failure.Task, deadlineTask).ConfigureAwait(false);
            if (ReferenceEquals(second, failure.Task) || ReferenceEquals(second, deadlineTask)
                || pipesTask.IsFaulted || pipesTask.IsCanceled)
            {
                return Abort();
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderrExceeded = await stderrTask.ConfigureAwait(false);
            await stdinTask.ConfigureAwait(false); // success requires the config fully written and closed

            if (stdout.Exceeded || stderrExceeded) { KillOnce(); return Failure(); }
            if (host.ExitCode != 0) { KillOnce(); return Failure(); }

            var frame = SubscriptionService.FrameCurlStdout(stdout.Bytes);
            if (!frame.Ok) { KillOnce(); return Failure(); }

            return new CurlFetchResult(true, frame.Status, frame.Body, null);
        }
        catch (Exception)
        {
            // Any unexpected post-factory fault — a throwing stream/exit host member, an internal
            // cancellation — is contained here: kill only the owned child at most once, stop the
            // remaining reads, observe every started task and report a fixed error. The caller's
            // cancellation is instead propagated as an inner-free OCE.
            KillOnce();
            try { deadline.Cancel(); } catch { }
            ObserveStarted();
            if (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
            return Failure();
        }
        finally
        {
            try { deadline.Cancel(); } catch { }
            ObserveStarted();
            try { await host.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static CurlFetchResult Failure() => new(false, 0, Array.Empty<byte>(), GenericFailure);

    private static void WatchFailure(Task task, Func<bool> isBad, Action fail)
    {
        if (task.IsFaulted || task.IsCanceled) { fail(); return; }
        if (task.IsCompleted)
        {
            if (isBad()) { fail(); }
            return;
        }

        _ = task.ContinueWith(
            _ =>
            {
                if (task.IsFaulted || task.IsCanceled) { fail(); return; }
                if (isBad()) { fail(); }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void Observe(params Task?[] tasks)
    {
        foreach (var task in tasks)
        {
            if (task is null) { continue; }
            if (task.IsFaulted) { _ = task.Exception; continue; }
            if (task.IsCompleted) { continue; }
            _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
        }
    }

    private static async Task<PipeRead> ReadCappedAsync(Stream stream, int cap, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0) { return new PipeRead(memory.ToArray(), false); }
            if (memory.Length + read > cap) { return new PipeRead(Array.Empty<byte>(), true); }
            memory.Write(buffer, 0, read);
        }
    }

    private static async Task<bool> DrainCappedAsync(Stream stream, int cap, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0) { return false; }
            total += read;
            if (total > cap) { return true; }
        }
    }

    private static async Task WriteAllAsync(Stream stream, string config, CancellationToken cancellationToken)
    {
        var bytes = new UTF8Encoding(false).GetBytes(config);
        await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        // A failed close means the config was never confirmed sealed; let it fault stdinTask so the
        // monitor reports a fixed failure instead of a false success.
        stream.Close();
    }

    private readonly record struct PipeRead(byte[] Bytes, bool Exceeded);
}

/// <summary>The production host: a real child process, killed only as the single created child.</summary>
internal sealed class ProcessCurlHost : ICurlProcessHost
{
    private readonly Process _process;

    internal ProcessCurlHost(ProcessStartInfo startInfo)
    {
        _process = new Process { StartInfo = startInfo };
        bool started;
        try
        {
            started = _process.Start();
        }
        catch
        {
            // No child was started: release the unowned handle and never try to kill.
            _process.Dispose();
            throw;
        }

        if (!started)
        {
            _process.Dispose();
            throw new InvalidOperationException("无法启动 curl。");
        }
    }

    public Stream StandardInput => _process.StandardInput.BaseStream;

    public Stream StandardOutput => _process.StandardOutput.BaseStream;

    public Stream StandardError => _process.StandardError.BaseStream;

    public int ExitCode => _process.ExitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

    public void KillOwned()
    {
        try
        {
            if (!_process.HasExited) { _process.Kill(entireProcessTree: false); }
        }
        catch
        {
            // The child may have exited between the check and the kill.
        }
    }

    public ValueTask DisposeAsync()
    {
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
