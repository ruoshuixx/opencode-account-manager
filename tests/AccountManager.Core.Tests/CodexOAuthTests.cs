using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Security;
using Xunit;

namespace AccountManager.Core.Tests;

public class CodexOAuthTests
{
    [Fact]
    public async Task ManualCallbackUsesPkceAndNeverSendsVerifierInAuthorizationUrl()
    {
        var handler = new TokenHandler();
        using var client = new HttpClient(handler);
        var secrets = new SecretRegistry(false);
        using var service = new CodexOAuthService(secrets, client);
        await using var session = service.Start(new[] { 0 }, TimeSpan.FromMinutes(1));
        var query = CodexOAuthSession.ParseQuery(new Uri(session.AuthorizationUrl).Query);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(query.ContainsKey("code_verifier"));
        Assert.Equal(session.RedirectUri, query["redirect_uri"]);
        await session.SubmitCallbackAsync(Callback(session, "code+one"));
        var account = await session.Completion;
        Assert.Equal("new@sample.test", account["email"]!.GetValue<string>());
        Assert.Equal(CodexOAuthService.TokenEndpoint, handler.RequestUri);
        Assert.Equal("code+one", handler.Form["code"]);
        Assert.Equal(session.RedirectUri, handler.Form["redirect_uri"]);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(handler.Form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["code_challenge"], challenge);
        Assert.Equal("authorization_code", handler.Form["grant_type"]);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("code+one", secrets.Redact("code+one"));
        Assert.DoesNotContain(query["state"], secrets.Redact(query["state"]));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("path")]
    [InlineData("state")]
    [InlineData("duplicate")]
    [InlineData("missing-code")]
    [InlineData("https")]
    public async Task InvalidManualCallbackDoesNotExchangeAndValidCallbackStillWorks(string kind)
    {
        var handler = new TokenHandler();
        using var client = new HttpClient(handler);
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        await using var session = service.Start(new[] { 0 }, TimeSpan.FromMinutes(1));
        var valid = Callback(session);
        var invalid = kind switch
        {
            "host" => valid.Replace("localhost", "example.test"),
            "port" => valid.Replace(new Uri(session.RedirectUri).Port.ToString(), "1"),
            "path" => valid.Replace("/auth/callback", "/wrong"),
            "state" => session.RedirectUri + "?code=fake&state=other-window",
            "duplicate" => valid + "&code=second",
            "missing-code" => session.RedirectUri + "?state=" + State(session),
            "https" => valid.Replace("http:", "https:"),
            _ => throw new InvalidOperationException(),
        };
        await Assert.ThrowsAsync<OpenCodeException>(() => session.SubmitCallbackAsync(invalid));
        Assert.Equal(0, handler.Calls);
        Assert.False(session.Completion.IsCompleted);
        await session.SubmitCallbackAsync(valid);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AutomaticLoopbackCallbackCompletesAndDoesNotEchoSecrets()
    {
        var handler = new TokenHandler();
        using var client = new HttpClient(handler);
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        await using var session = service.Start(new[] { 0 }, TimeSpan.FromMinutes(1));
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        var response = await browser.GetAsync(Callback(session, "private-code").Replace("localhost", "127.0.0.1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-code", html);
        Assert.DoesNotContain(State(session), html);
        Assert.NotNull(await session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task InvalidAutomaticCallbackDoesNotBreakManualFallback()
    {
        var handler = new TokenHandler();
        using var client = new HttpClient(handler);
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        await using var session = service.Start(new[] { 0 }, TimeSpan.FromMinutes(1));
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        var response = await browser.GetAsync(session.RedirectUri.Replace("localhost", "127.0.0.1") + "?state=wrong&code=private");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, handler.Calls);
        await session.SubmitCallbackAsync(Callback(session));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CompletedAutomaticCallbackReleasesPortForNextAuthorization()
    {
        using var client = new HttpClient(new TokenHandler());
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        var port = 0;
        for (var i = 0; i < 3; i++)
        {
            await using var session = service.Start(new[] { port }, TimeSpan.FromMinutes(1));
            port = new Uri(session.RedirectUri).Port;
            using var response = await browser.GetAsync(Callback(session).Replace("localhost", "127.0.0.1"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(await session.Completion);
        }
    }

    [Fact]
    public async Task ConcurrentManualSubmissionsOnlyExchangeOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TokenHandler { WaitBeforeResponse = release.Task };
        using var client = new HttpClient(handler);
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        await using var session = service.Start(new[] { 0 }, TimeSpan.FromMinutes(1));
        var first = session.SubmitCallbackAsync(Callback(session));
        var second = session.SubmitCallbackAsync(Callback(session));
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ClosingDuringManualTokenExchangeCancelsSafelyAndReleasesPort()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TokenHandler { WaitBeforeResponse = release.Task };
        using var client = new HttpClient(handler);
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        var session = service.Start(new[] { 0 }, TimeSpan.FromMinutes(1));
        var port = new Uri(session.RedirectUri).Port;
        var submission = session.SubmitCallbackAsync(Callback(session));
        Assert.Equal(1, handler.Calls);
        await session.DisposeAsync();
        var error = await Assert.ThrowsAsync<OpenCodeException>(() => submission);
        Assert.Contains("取消", error.Message);
        await Assert.ThrowsAsync<OpenCodeException>(() => session.SubmitCallbackAsync(Callback(session)));
        await using var next = service.Start(new[] { port }, TimeSpan.FromMinutes(1));
        Assert.Equal(port, new Uri(next.RedirectUri).Port);
    }

    [Fact]
    public async Task ConcurrentDenialCannotReplaceAnAlreadyAcceptedCallback()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TokenHandler { WaitBeforeResponse = release.Task };
        using var client = new HttpClient(handler);
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        await using var session = service.Start(new[] { 0 }, TimeSpan.FromMinutes(1));
        var first = session.SubmitCallbackAsync(Callback(session));
        var second = session.SubmitCallbackAsync(session.RedirectUri + "?error=access_denied&state=" + State(session));
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, handler.Calls);
        Assert.True(session.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExpiryAndCancelReleasePortAndInvalidateOldCallback()
    {
        using var client = new HttpClient(new TokenHandler());
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        var expired = service.Start(new[] { 0 }, TimeSpan.FromMilliseconds(80));
        var port = new Uri(expired.RedirectUri).Port;
        await Assert.ThrowsAsync<OpenCodeException>(() => expired.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<OpenCodeException>(() => expired.SubmitCallbackAsync(Callback(expired)));
        await expired.DisposeAsync();
        await expired.DisposeAsync();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
        var cancelled = service.Start(new[] { port }, TimeSpan.FromMinutes(1));
        await cancelled.DisposeAsync();
        var error = await Assert.ThrowsAsync<OpenCodeException>(() => cancelled.Completion);
        Assert.Contains("取消", error.Message);
    }

    [Fact]
    public async Task OccupiedPortFallsBackWithoutDisturbingOtherListener()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Server.ExclusiveAddressUse = true;
        occupied.Start();
        try
        {
            using var client = new HttpClient(new TokenHandler());
            using var service = new CodexOAuthService(new SecretRegistry(), client);
            var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            await using var session = service.Start(new[] { port, 0 }, TimeSpan.FromMinutes(1));
            Assert.NotEqual(port, new Uri(session.RedirectUri).Port);
            Assert.True(occupied.Server.IsBound);
        }
        finally { occupied.Stop(); }
    }

    [Fact]
    public async Task TwoLiveAuthorizationListenersCannotShareTheSamePort()
    {
        using var client = new HttpClient(new TokenHandler());
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        await using var session = service.Start(new[] { 0 }, TimeSpan.FromMinutes(1));
        var port = new Uri(session.RedirectUri).Port;
        Assert.Throws<OpenCodeException>(() => service.Start(new[] { port }, TimeSpan.FromMinutes(1)));
        await session.SubmitCallbackAsync(Callback(session));
    }

    [Fact]
    public async Task DenialStopsWithoutTokenExchange()
    {
        var handler = new TokenHandler();
        using var client = new HttpClient(handler);
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        await using var session = service.Start(new[] { 0 }, TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<OpenCodeException>(() => session.SubmitCallbackAsync(session.RedirectUri + "?error=access_denied&state=" + State(session)));
        Assert.True(session.Completion.IsFaulted);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(400)]
    [InlineData(500)]
    public async Task TokenErrorsNeverEchoServerBodyOrCode(int status)
    {
        var handler = new TokenHandler { Status = (HttpStatusCode)status, Body = "private-server-body" };
        using var client = new HttpClient(handler);
        using var service = new CodexOAuthService(new SecretRegistry(), client);
        await using var session = service.Start(new[] { 0 }, TimeSpan.FromMinutes(1));
        var error = await Assert.ThrowsAsync<OpenCodeException>(() => session.SubmitCallbackAsync(Callback(session, "private-code")));
        Assert.DoesNotContain("private-server-body", error.Message);
        Assert.DoesNotContain("private-code", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void TokenMappingRefusesIncompleteAndExpiredIdentity()
    {
        Assert.Throws<OpenCodeException>(() => CodexOAuthService.AccountFromTokens(new JsonObject { ["access_token"] = "invalid", ["refresh_token"] = "secret" }));
        Assert.Throws<OpenCodeException>(() => CodexOAuthService.AccountFromTokens(new JsonObject { ["access_token"] = AccountAdditionTests.AccessToken("new@sample.test") }));
    }

    [Fact]
    public void ApiInputValidationNormalizesModelsAndRejectsUnsafeInputs()
    {
        Assert.Equal(new[] { "a", "b", "c" }, AccountManager.Core.Actions.ApiProviderValidation.ParseModels(" a\r\nb,a，c "));
        Assert.Equal("Name", AccountManager.Core.Actions.ApiProviderValidation.Name(" Name "));
        Assert.Throws<OpenCodeException>(() => AccountManager.Core.Actions.ApiProviderValidation.Name(" "));
        Assert.Throws<OpenCodeException>(() => AccountManager.Core.Actions.ApiProviderValidation.Endpoint("https://user:pass@example.test/v1"));
        Assert.Throws<OpenCodeException>(() => AccountManager.Core.Actions.ApiProviderValidation.Endpoint("https://example.test/v1?key=private"));
        Assert.Throws<OpenCodeException>(() => AccountManager.Core.Actions.ApiProviderValidation.Endpoint("http://example.test"));
        Assert.Throws<OpenCodeException>(() => AccountManager.Core.Actions.ApiProviderValidation.Key(" ", true));
    }

    private static string State(CodexOAuthSession session) => CodexOAuthSession.ParseQuery(new Uri(session.AuthorizationUrl).Query)["state"];
    private static string Callback(CodexOAuthSession session, string code = "fake-code")
        => session.RedirectUri + "?code=" + Uri.EscapeDataString(code) + "&state=" + State(session);

    private sealed class TokenHandler : HttpMessageHandler
    {
        public int Calls;
        public string? RequestUri;
        public Dictionary<string, string> Form = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? Body;
        public Task? WaitBeforeResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            RequestUri = request.RequestUri!.AbsoluteUri;
            Form = CodexOAuthSession.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (WaitBeforeResponse is not null) { await WaitBeforeResponse.WaitAsync(cancellationToken); }
            var tokens = new JsonObject { ["access_token"] = AccountAdditionTests.AccessToken("new@sample.test"), ["refresh_token"] = "fake-refresh", ["id_token"] = "" };
            return new HttpResponseMessage(Status) { Content = new StringContent(Body ?? tokens.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
