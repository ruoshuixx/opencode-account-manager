using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Quota;
using Xunit;

namespace AccountManager.Core.Tests;

/// <summary>
/// Task 6 contract tests (tests-only RED) for the curl discovery/framing/builder boundary and the
/// production-used transport wiring of <see cref="SubscriptionService"/>. The public surface
/// (<c>GetSubscriptionAsync</c>, <c>SendAsync</c>, <c>ParseCurlOutput</c>, account selection) is
/// unchanged; only internal seams are added.
///
/// Proposed production seam (see task-6-report.md; missing until implemented, so this file is an
/// accepted contract red exactly like <c>GsettingsCompletionTests</c>):
///
///   // discovery: Windows prefers systemDirectory\curl.exe then PATH; Unix uses the exact name
///   // "curl", absolute path segments only, a regular file with effective execute access.
///   internal static string? SubscriptionService.FindCurl(
///       bool windows, IReadOnlyList&lt;string&gt; pathDirectories, string? systemDirectory,
///       Func&lt;string, bool&gt;? isUsableExecutable = null);
///
///   // arguments: -q literal first (no curlrc), --config - (token only via stdin), no -L,
///   // bounded --max-time, url last.
///   internal static ProcessStartInfo SubscriptionService.BuildCurlStartInfo(
///       string executable, string url, string? proxy);
///
///   // stdin config body: escaped values; control characters rejected before any process starts.
///   internal static string SubscriptionService.BuildCurlStdinConfig(
///       string accessToken, string accountId, string targetPath);
///
///   // strict transport framing over the raw stdout bytes. Ok requires a well-formed trailing
///   // suffix (marker + exactly three digits + "___") AND parsed body bytes &lt;= MaxBytes.
///   internal readonly record struct CurlStdoutFrame(bool Ok, int Status, byte[] Body);
///   internal static CurlStdoutFrame SubscriptionService.FrameCurlStdout(ReadOnlySpan&lt;byte&gt; raw);
///
///   // injected curl process boundary + factory; the default RunCurlAsync uses the same transport.
///   internal interface ICurlProcessHost : IAsyncDisposable { ... }        // see SystemCurlProcessTests.cs
///   internal delegate ICurlProcessHost CurlProcessFactory(ProcessStartInfo startInfo);
///   internal readonly record struct CurlFetchResult(bool TransportOk, int Status, byte[] Body, string? Error);
///   internal sealed class SystemCurlTransport { internal Task&lt;CurlFetchResult&gt; FetchAsync(...); }
///
///   // test/DI ctor; production keeps public SubscriptionService().
///   internal SubscriptionService(CurlProcessFactory? curlFactory, string? curlExecutable);
///
/// No real curl, account request, HOME, network or child process is used.
/// </summary>
public class SystemCurlTests
{
    // ── discovery: OS-independent literal candidates through the injected usability predicate ──

    [Fact]
    public void WindowsPrefersTheSystemDirectoryCurlExe()
    {
        var offered = new List<string>();
        bool Usable(string path) { offered.Add(path); return true; }

        var found = SubscriptionService.FindCurl(true, new[] { @"C:\Tools" }, @"C:\Windows\System32", Usable);

        Assert.Equal(@"C:\Windows\System32\curl.exe", found);
        Assert.Equal(@"C:\Windows\System32\curl.exe", offered[0]);
    }

    [Fact]
    public void WindowsFallsBackToPathDirectoriesInOrder()
    {
        var found = SubscriptionService.FindCurl(
            true,
            new[] { @"C:\First", @"C:\Tools" },
            @"C:\Windows\System32",
            path => path == @"C:\Tools\curl.exe");

        Assert.Equal(@"C:\Tools\curl.exe", found);
    }

    [Fact]
    public void WindowsWithoutASystemDirectoryUsesThePath()
    {
        var found = SubscriptionService.FindCurl(
            true,
            new[] { @"C:\Tools\" },
            systemDirectory: null,
            path => path == @"C:\Tools\curl.exe");

        // A trailing separator must not double up; the discovered path stays the curl.exe file.
        Assert.Equal(@"C:\Tools\curl.exe", found);
    }

    [Fact]
    public void WindowsReturnsNullWhenNoCandidateIsUsable()
    {
        Assert.Null(SubscriptionService.FindCurl(true, new[] { @"C:\Tools" }, @"C:\Windows\System32", _ => false));
    }

    [Fact]
    public void UnixUsesTheExactNameCurlAndSkipsNonAbsoluteDirectories()
    {
        var offered = new List<string>();
        bool Usable(string path) { offered.Add(path); return path == "/usr/bin/curl"; }

        var found = SubscriptionService.FindCurl(false, new[] { "relative/dir", "", "/usr/bin" }, null, Usable);

        Assert.Equal("/usr/bin/curl", found);
        // A Windows-style name must never be probed on Unix …
        Assert.DoesNotContain(offered, path => path.EndsWith(".exe", StringComparison.Ordinal));
        // … and a relative PATH segment must never turn into a process path.
        Assert.DoesNotContain(offered, path => !path.StartsWith('/'));
    }

    [Fact]
    public void UnixReturnsNullWhenNoCandidateIsUsable()
    {
        Assert.Null(SubscriptionService.FindCurl(false, new[] { "/usr/bin", "/snap/bin" }, null, _ => false));
    }

    [Fact]
    public void FindCurlNeverThrowsOnEmptyOrMalformedEntries()
    {
        var exception = Record.Exception(() =>
        {
            Assert.Null(SubscriptionService.FindCurl(true, new[] { "", "  " }, "", _ => true));
            Assert.Null(SubscriptionService.FindCurl(false, new[] { "", "  " }, null, _ => true));
        });

        Assert.Null(exception);
    }

    // Native: the *default* usability predicate must require a real regular executable named curl.
    [LinuxFact]
    [UnsupportedOSPlatform("windows")]
    public void UnixDefaultUsabilityRequiresARegularExecutableNamedCurl()
    {
        using var dir = new TempDirectory();

        var plain = Path.Combine(dir.Path, "curl");
        File.WriteAllText(plain, "#!/bin/sh\n");
        // No execute bit: the kernel would refuse execve, so curl is not usable.
        Assert.Null(SubscriptionService.FindCurl(false, new[] { dir.Path }, null));

        File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(plain, SubscriptionService.FindCurl(false, new[] { dir.Path }, null));

        using var directoryCalledCurl = new TempDirectory();
        var asDirectory = Path.Combine(directoryCalledCurl.Path, "curl");
        Directory.CreateDirectory(asDirectory);
        Assert.Null(SubscriptionService.FindCurl(false, new[] { directoryCalledCurl.Path }, null));
    }

    // ── arguments: no curlrc, no redirects, no token in argv ───────────────────────────────

    [Fact]
    public void CurlTransportDoesNotExposeTokenInArguments()
    {
        var info = SubscriptionService.BuildCurlStartInfo("curl", SubscriptionService.AccountsCheckUri, proxy: null);

        Assert.Equal("-q", info.ArgumentList[0]);
        Assert.DoesNotContain("-L", info.ArgumentList);
        Assert.DoesNotContain(info.ArgumentList, argument => argument.Contains("Bearer", StringComparison.Ordinal));
    }

    [Fact]
    public void CurlArgumentsDisableCurlrcFirstAndReadConfigFromStdin()
    {
        var info = SubscriptionService.BuildCurlStartInfo("curl", SubscriptionService.AccountsCheckUri, proxy: null);

        Assert.Equal("-q", info.ArgumentList[0]);
        var configIndex = info.ArgumentList.IndexOf("--config");
        Assert.True(configIndex >= 0, "--config is required to keep the token off the command line");
        Assert.Equal("-", info.ArgumentList[configIndex + 1]);
    }

    [Fact]
    public void CurlArgumentsBoundTheTimeAndDoNotFollowRedirects()
    {
        var url = SubscriptionService.AccountsCheckUri;
        var info = SubscriptionService.BuildCurlStartInfo("curl", url, proxy: null);

        Assert.Contains("-sS", info.ArgumentList);
        Assert.Contains("-o", info.ArgumentList);
        Assert.Contains("-", info.ArgumentList);
        Assert.Contains("--max-time", info.ArgumentList);
        Assert.Contains(SubscriptionService.TimeoutSeconds.ToString(), info.ArgumentList);
        Assert.DoesNotContain("-L", info.ArgumentList);
        Assert.Equal(url, info.ArgumentList[^1]);
        Assert.Equal("curl", info.FileName);
    }

    [Fact]
    public void CurlPinsTheTlsVersionOnlyOutsideWindows()
    {
        // Linux curl's default handshake is challenged by Cloudflare (403, cf-mitigated: challenge) on
        // chatgpt.com/backend-api; one pinned TLS version is not. Windows curl.exe keeps its default.
        var info = SubscriptionService.BuildCurlStartInfo("curl", SubscriptionService.SubscriptionsUri, proxy: null);

        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("--tlsv1.3", info.ArgumentList);
        }
        else
        {
            Assert.Contains("--tlsv1.3", info.ArgumentList);
        }

        Assert.Equal(SubscriptionService.SubscriptionsUri, info.ArgumentList[^1]);
    }

    [Fact]
    public void CurlProxyIsPassedOnlyWhenResolved()
    {
        var without = SubscriptionService.BuildCurlStartInfo("curl", SubscriptionService.AccountsCheckUri, proxy: null);
        Assert.DoesNotContain("--proxy", without.ArgumentList);

        var with = SubscriptionService.BuildCurlStartInfo("curl", SubscriptionService.AccountsCheckUri, "http://127.0.0.1:7890");
        var proxyIndex = with.ArgumentList.IndexOf("--proxy");
        Assert.True(proxyIndex >= 0);
        Assert.Equal("http://127.0.0.1:7890", with.ArgumentList[proxyIndex + 1]);
    }

    [Fact]
    public void CurlStartInfoRedirectsAllThreeStreamsAndHidesTheWindow()
    {
        var info = SubscriptionService.BuildCurlStartInfo("curl", SubscriptionService.AccountsCheckUri, proxy: null);

        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.True(info.CreateNoWindow);
    }

    [Fact]
    public void CurlEnvironmentCarriesNoBearerOrAccountHeader()
    {
        var info = SubscriptionService.BuildCurlStartInfo("curl", SubscriptionService.AccountsCheckUri, proxy: null);

        Assert.DoesNotContain(info.Environment, entry =>
            entry.Key.Contains("Bearer", StringComparison.Ordinal)
            || (entry.Value?.Contains("Bearer", StringComparison.Ordinal) ?? false));
    }

    // ── stdin config: escaping and control-character rejection ─────────────────────────────

    [Fact]
    public void CurlStdinConfigCarriesTheAuthorizationAndTargetHeaders()
    {
        var config = SubscriptionService.BuildCurlStdinConfig("tok", "acct", "/backend-api/accounts/check");

        Assert.Contains("header = \"Authorization: Bearer tok\"", config, StringComparison.Ordinal);
        Assert.Contains("header = \"ChatGPT-Account-Id: acct\"", config, StringComparison.Ordinal);
        Assert.Contains("header = \"x-openai-target-path: /backend-api/accounts/check\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public void CurlStdinConfigEscapesQuotesAndBackslashes()
    {
        // Raw token contains a double quote and a backslash; both must be escaped so header data can
        // never terminate the quoted curl config value or start a new directive.
        var config = SubscriptionService.BuildCurlStdinConfig("to\"ken\\x", "acct", "/p");

        Assert.Contains("header = \"Authorization: Bearer to\\\"ken\\\\x\"", config, StringComparison.Ordinal);
    }

    [Fact]
    public void CurlStdinConfigRejectsControlCharactersInHeaderValues()
    {
        var exception = Assert.ThrowsAny<Exception>(() =>
            SubscriptionService.BuildCurlStdinConfig("token", "acct\r\nX-Injected: 1", "/p"));

        Assert.DoesNotContain("X-Injected", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", exception.Message, StringComparison.Ordinal);
    }

    // ── framing: body cap is byte-exact and independent of the status-suffix allowance ─────

    [Fact]
    public void FrameAcceptsExactlyTheBodyCapWithAValidStatusSuffix()
    {
        var body = Filled(SubscriptionService.MaxBytes);

        var frame = SubscriptionService.FrameCurlStdout(WithSuffix(body, "200"));

        Assert.True(frame.Ok);
        Assert.Equal(200, frame.Status);
        Assert.Equal(SubscriptionService.MaxBytes, frame.Body.Length);
    }

    [Fact]
    public void FrameRejectsOneBodyByteOverTheCapEvenWithAValidSuffix()
    {
        var raw = WithSuffix(Filled(SubscriptionService.MaxBytes + 1), "200");

        // Sanity: this raw length passes the raw cap, so the *parsed body* check must be the
        // discriminator; the suffix allowance must never enlarge the accepted body.
        Assert.True(raw.Length < SubscriptionService.MaxCurlStdoutBytes);
        Assert.False(SubscriptionService.FrameCurlStdout(raw).Ok);
    }

    [Fact]
    public void FrameCountsMultibyteUtf8ByBytes()
    {
        var text = new string('é', SubscriptionService.MaxBytes / 2); // 2 bytes each → exact cap
        var body = Encoding.UTF8.GetBytes(text);
        Assert.Equal(SubscriptionService.MaxBytes, body.Length);

        var frame = SubscriptionService.FrameCurlStdout(WithSuffix(body, "200"));

        Assert.True(frame.Ok);
        Assert.Equal(text, Encoding.UTF8.GetString(frame.Body));
    }

    [Fact]
    public void FrameRejectsAMissingStatusSuffix()
    {
        var frame = SubscriptionService.FrameCurlStdout(Encoding.UTF8.GetBytes("{\"a\":1}"));

        Assert.False(frame.Ok);
        Assert.Equal(0, frame.Status);
    }

    [Fact]
    public void FrameRejectsAMarkerForgedInsideTheBody()
    {
        var raw = Encoding.UTF8.GetBytes("{\"x\":\"" + SubscriptionService.CurlStatusMarker + "200___\"}");

        Assert.False(SubscriptionService.FrameCurlStdout(raw).Ok);
    }

    [Fact]
    public void FrameRejectsAStatusThatIsNotExactlyThreeDigits()
    {
        foreach (var bad in new[] { "", "20", "2000", "abc", "20a" })
        {
            Assert.False(SubscriptionService.FrameCurlStdout(WithSuffix(Encoding.UTF8.GetBytes("{}"), bad)).Ok);
        }
    }

    [Fact]
    public void FrameUsesTheLastWellFormedSuffix()
    {
        var bodyText = "{\"x\":\"" + SubscriptionService.CurlStatusMarker + "999___\"}";

        var frame = SubscriptionService.FrameCurlStdout(WithSuffix(Encoding.UTF8.GetBytes(bodyText), "200"));

        Assert.True(frame.Ok);
        Assert.Equal(200, frame.Status);
        Assert.Equal(bodyText, Encoding.UTF8.GetString(frame.Body));
    }

    [Fact]
    public void FrameOfEmptyInputIsNotOk()
        => Assert.False(SubscriptionService.FrameCurlStdout(ReadOnlySpan<byte>.Empty).Ok);

    [Fact]
    public void FrameRejectsOneBodyByteOverTheCapEndingInCr()
    {
        // The parsed body is MaxBytes+1 bytes and its last byte is CR. curl adds exactly one LF
        // separator; a CR that belongs to the body must never be stripped, so this stays over cap.
        var body = Filled(SubscriptionService.MaxBytes + 1);
        body[^1] = (byte)'\r';

        var raw = WithSuffix(body, "200");

        Assert.True(raw.Length < SubscriptionService.MaxCurlStdoutBytes);
        Assert.False(SubscriptionService.FrameCurlStdout(raw).Ok);
    }

    [Fact]
    public void FramePreservesTheBodyTrailingCrAtExactlyTheCap()
    {
        var body = Filled(SubscriptionService.MaxBytes);
        body[^1] = (byte)'\r';

        var frame = SubscriptionService.FrameCurlStdout(WithSuffix(body, "200"));

        Assert.True(frame.Ok);
        Assert.Equal(SubscriptionService.MaxBytes, frame.Body.Length);
        // The body's own trailing CR is retained byte-for-byte; only the LF separator is removed.
        Assert.Equal(body, frame.Body);
    }

    // ── service wiring: the actual GetSubscriptionAsync path uses the injected curl boundary ──

    [Fact]
    public async Task GetSubscriptionUsesTheInjectedCurlBoundary()
    {
        ProcessStartInfo? captured = null;
        var starts = 0;
        var factory = new CurlProcessFactory(info =>
        {
            starts++;
            captured = info;
            return new ImmediateCurlHost(
                CurlStdout("""{"accounts":{"t":{"account":{"account_id":"t"},"entitlement":{"expires_at":"2099-01-02T03:04:05Z"}}}}"""),
                Array.Empty<byte>(),
                exitCode: 0);
        });
        var service = new CurlBoundaryService(factory);

        var status = await service.GetSubscriptionAsync("token", "t");

        Assert.Equal(DateTimeOffset.Parse("2099-01-02T03:04:05Z"), status.ActiveUntil);
        Assert.Equal(1, starts);
        Assert.NotNull(captured);
        Assert.Equal("-q", captured!.ArgumentList[0]);
        Assert.Equal(0, service.ManagedCalls);
    }

    [Fact]
    public async Task GetSubscriptionUsesTheCurlBoundaryForTheSubscriptionsFallback()
    {
        var startedUrls = new List<string>();
        var factory = new CurlProcessFactory(info =>
        {
            var url = info.ArgumentList[^1];
            startedUrls.Add(url);
            var body = url.Contains("accounts/check", StringComparison.Ordinal)
                ? """{"accounts":{"t":{"account":{"account_id":"t"},"entitlement":{"expires_at":"2000-01-01T00:00:00Z"}}}}"""
                : """{"active_until":"2099-01-02T03:04:05Z"}""";
            return new ImmediateCurlHost(CurlStdout(body), Array.Empty<byte>(), exitCode: 0);
        });
        var service = new CurlBoundaryService(factory);

        var status = await service.GetSubscriptionAsync("token", "t");

        Assert.Equal(DateTimeOffset.Parse("2099-01-02T03:04:05Z"), status.ActiveUntil);
        Assert.Equal(2, startedUrls.Count);
        Assert.Contains("subscriptions", startedUrls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurlParseFailureSurfacesAFixedErrorWithoutInnerOrRawBody()
    {
        var factory = new CurlProcessFactory(_ =>
            new ImmediateCurlHost(CurlStdout("not json at all SENTINEL"), Array.Empty<byte>(), exitCode: 0));
        var service = new CurlBoundaryService(factory, managed: _ => throw new InvalidOperationException("managed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSubscriptionAsync("token", "t"));

        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("SENTINEL", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurlStartupFailureSurfacesAFixedErrorWithoutInnerOrRawMessage()
    {
        var factory = new CurlProcessFactory(_ => throw new InvalidOperationException("curl.exe startup SENTINEL"));
        var service = new CurlBoundaryService(factory, managed: _ => throw new InvalidOperationException("managed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSubscriptionAsync("token", "t"));

        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("SENTINEL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurlRejectsControlCharactersInHeaderValuesBeforeStarting()
    {
        var starts = 0;
        var factory = new CurlProcessFactory(_ =>
        {
            starts++;
            return new ImmediateCurlHost(Array.Empty<byte>(), Array.Empty<byte>(), exitCode: 0);
        });
        var service = new CurlBoundaryService(factory, managed: _ => throw new InvalidOperationException("managed"));

        var ex = await Record.ExceptionAsync(() => service.GetSubscriptionAsync("token", "acct\r\nX-Injected: 1"));

        Assert.NotNull(ex);
        Assert.IsNotType<OperationCanceledException>(ex);
        Assert.Equal(0, starts);
        Assert.DoesNotContain("X-Injected", ex!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task ManagedFallbackStillRunsWhenCurlReturnsNoBody()
    {
        var factory = new CurlProcessFactory(_ =>
            new ImmediateCurlHost(Encoding.UTF8.GetBytes("network broke"), Array.Empty<byte>(), exitCode: 0));
        var service = new CurlBoundaryService(factory, managed: _ =>
            Node("""{"accounts":{"t":{"account":{"account_id":"t"},"entitlement":{"expires_at":"2099-01-02T03:04:05Z"}}}}"""));

        var status = await service.GetSubscriptionAsync("token", "t");

        Assert.Equal(DateTimeOffset.Parse("2099-01-02T03:04:05Z"), status.ActiveUntil);
        Assert.Equal(1, service.ManagedCalls);
    }

    [Fact]
    public async Task PreCancelledGetSubscriptionStartsNoCurlAndMakesNoManagedRequest()
    {
        var starts = 0;
        var factory = new CurlProcessFactory(_ =>
        {
            starts++;
            return new ImmediateCurlHost(Array.Empty<byte>(), Array.Empty<byte>(), exitCode: 0);
        });
        var service = new CurlBoundaryService(factory, managed: _ => Node("{}"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetSubscriptionAsync("token", "t", cts.Token));

        Assert.Equal(0, starts);
        Assert.Equal(0, service.ManagedCalls);
    }

    [Fact]
    public async Task ManagedCancellationIsNotReplacedByAnEarlierCurlError()
    {
        // curl fails (no status suffix) *and* the managed fallback is cancelled. Cancellation must
        // win; the earlier curl error must never shadow it.
        var factory = new CurlProcessFactory(_ =>
            new ImmediateCurlHost(Encoding.UTF8.GetBytes("network broke"), Array.Empty<byte>(), exitCode: 0));
        var service = new CurlBoundaryService(factory, managedCancels: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetSubscriptionAsync("token", "t"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    private static byte[] Filled(int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)'a');
        return bytes;
    }

    private static byte[] WithSuffix(byte[] body, string status)
    {
        var suffix = Encoding.ASCII.GetBytes("\n" + SubscriptionService.CurlStatusMarker + status + "___");
        var raw = new byte[body.Length + suffix.Length];
        body.CopyTo(raw, 0);
        suffix.CopyTo(raw, body.Length);
        return raw;
    }

    private static byte[] CurlStdout(string body, int status = 200)
        => WithSuffix(Encoding.UTF8.GetBytes(body), status.ToString());

    /// <summary>Boundary service that forces the curl path and records managed-fallback use.</summary>
    private sealed class CurlBoundaryService : SubscriptionService
    {
        private readonly Func<string, JsonNode?>? _managed;
        private readonly bool _managedCancels;

        public CurlBoundaryService(
            CurlProcessFactory factory,
            Func<string, JsonNode?>? managed = null,
            bool managedCancels = false)
            : base(factory, "/fake/curl")
        {
            _managed = managed;
            _managedCancels = managedCancels;
        }

        public int ManagedCalls { get; private set; }

        protected override bool TrySystemCurl => true;

        protected override Task<JsonNode> SendAsync(string url, string accessToken, string accountId, CancellationToken cancellationToken)
        {
            ManagedCalls++;
            if (_managedCancels) { return Task.FromException<JsonNode>(new OperationCanceledException(cancellationToken)); }
            var node = _managed?.Invoke(url);
            return node is null
                ? Task.FromException<JsonNode>(new InvalidOperationException("managed request failed"))
                : Task.FromResult(node);
        }
    }

    /// <summary>Host whose reported streams and exit are available immediately; no child process.</summary>
    private sealed class ImmediateCurlHost : ICurlProcessHost
    {
        public ImmediateCurlHost(byte[] stdout, byte[] stderr, int exitCode)
        {
            StandardOutput = new MemoryStream(stdout);
            StandardError = new MemoryStream(stderr);
            ExitCode = exitCode;
        }

        public Stream StandardInput { get; } = new MemoryStream();

        public Stream StandardOutput { get; }

        public Stream StandardError { get; }

        public int ExitCode { get; }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;

        public void KillOwned() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
