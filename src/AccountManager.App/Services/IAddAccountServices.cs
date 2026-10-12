using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Models;
using AccountManager.Core.Security;

namespace AccountManager.App.Services;

/// <summary>Small dialog boundary so UI tests never construct real account/CLI services.</summary>
public interface IAddAccountServices
{
    SecretRegistry Secrets { get; }
    Task EnsureBrowserOAuthAsync();
    CodexOAuthService CreateOAuthService();

    /// <summary>
    /// Starts one browser OAuth session. The production default wraps <see cref="CreateOAuthService"/>
    /// in the App session adapter; UI tests override it with a synthetic session so no real port or
    /// token endpoint is touched.
    /// </summary>
    IBrowserOAuthSession StartBrowserOAuthSession() => new BrowserOAuthSession(CreateOAuthService());

    Task<CodexAddPreview> PreviewCodexAuthorizationAsync(JsonObject account);
    Task<AccountAddResult> SaveCodexAuthorizationAsync(CodexAddPreview preview, bool replaceExisting);
    Task<AccountAddResult> CreateApiProviderAsync(string id, string name, string url, IReadOnlyList<string> models, string key, bool useResponses);
}
