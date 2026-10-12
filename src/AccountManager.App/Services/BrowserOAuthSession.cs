using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;

namespace AccountManager.App.Services;

/// <summary>
/// Small App-side seam over one browser OAuth session. Keeps the Core OAuth protocol out of the
/// view model and lets UI tests supply a synthetic pending session without real ports/endpoints.
/// </summary>
public interface IBrowserOAuthSession : IAsyncDisposable
{
    string AuthorizationUrl { get; }

    /// <summary>Contains tokens. Only consume to create a preview, never display/log this result.</summary>
    Task<JsonObject> Completion { get; }

    Task SubmitCallbackAsync(string callbackUrl);
}

/// <summary>
/// Thin adapter over Core's OAuth service + session: the adapter owns both, so disposing the
/// session also disposes the service that created it.
/// </summary>
internal sealed class BrowserOAuthSession : IBrowserOAuthSession
{
    private readonly CodexOAuthService _service;
    private readonly CodexOAuthSession _session;

    internal BrowserOAuthSession(CodexOAuthService service)
    {
        _service = service;
        try { _session = service.Start(); }
        catch
        {
            service.Dispose();
            throw;
        }
    }

    public string AuthorizationUrl => _session.AuthorizationUrl;

    public Task<JsonObject> Completion => _session.Completion;

    public Task SubmitCallbackAsync(string callbackUrl) => _session.SubmitCallbackAsync(callbackUrl);

    public async ValueTask DisposeAsync()
    {
        try { await _session.DisposeAsync().ConfigureAwait(false); }
        finally { _service.Dispose(); }
    }
}
