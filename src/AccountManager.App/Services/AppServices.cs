using System.Text.Json.Nodes;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Export;
using AccountManager.Core.Import;
using AccountManager.Core.Json;
using AccountManager.Core.Logging;
using AccountManager.Core.Models;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Quota;
using AccountManager.Core.Security;
using AccountManager.Core.Sync;

namespace AccountManager.App.Services;

/// <summary>
/// Composition root: builds every core service once and exposes the small set of
/// operations the UI needs. Mirrors the PowerShell entry point wiring.
/// </summary>
public sealed class AppServices : IAddAccountServices
{
    public AppServices(AppSettings settings)
    {
        Settings = settings;
        Secrets = new SecretRegistry();
        LogDirectory = PortablePaths.Logs;
        Log = new AppLog(Secrets, LogDirectory);

        Cli = new OpenCodeCli(new OpenCodeCliOptions { CliPath = settings.CliPath }, Secrets, Log);
        Api = new OpenCodeApi(Cli, Secrets);
        RuntimeVerifier = new ProviderRuntimeVerifier(Api);
        ConfigFile = new ConfigFile(Cli);
        ProviderConfig = new ProviderConfigReader(Api, ConfigFile, Secrets);
        Backups = new BackupService();
        Subscriptions = new SubscriptionStore(Cli, Log);
        Mapper = new AccountMapper(Secrets);
        Snapshot = new AccountSnapshotService(Api, Mapper, Secrets, Subscriptions, ProviderConfig);
        Switch = new SwitchService(Api, Mapper);
        Quota = new QuotaService();
        Subscription = new SubscriptionService();
        SubscriptionRefresher = new SubscriptionRefresher(Subscription, Log);
        QuotaSelector = new AccountQuotaSelector(Api, Quota, Secrets, Log, SubscriptionRefresher);
        ResetCredits = new ResetCreditService();
        Reset = new AccountResetService(Api, ResetCredits, QuotaSelector, Secrets, Log);

        Actions = new AccountActions(Api, ConfigFile, Backups, Secrets, Log, Mapper);
        Export = new ExportService(Api, Secrets, Log, Subscriptions, ProviderConfig);
        Import = new ImportService(Api, Backups, Secrets, Log, Subscriptions);
    }

    public AppSettings Settings { get; }

    public string LogDirectory { get; }

    public SecretRegistry Secrets { get; }

    public AppLog Log { get; }

    public OpenCodeCli Cli { get; }

    public OpenCodeApi Api { get; }

    public ProviderRuntimeVerifier RuntimeVerifier { get; }

    public ConfigFile ConfigFile { get; }

    public ProviderConfigReader ProviderConfig { get; }

    public BackupService Backups { get; }

    public SubscriptionStore Subscriptions { get; }

    public AccountMapper Mapper { get; }

    public AccountSnapshotService Snapshot { get; }

    public SwitchService Switch { get; }

    public QuotaService Quota { get; }

    public SubscriptionService Subscription { get; }

    public SubscriptionRefresher SubscriptionRefresher { get; }

    public AccountQuotaSelector QuotaSelector { get; }

    public ResetCreditService ResetCredits { get; }

    public AccountResetService Reset { get; }

    public AccountActions Actions { get; }

    public ExportService Export { get; }

    public ImportService Import { get; }

    public void ApplyCliPath(string? path) => Cli.SetCliPath(path);

    // ---- WebDAV sync (manual upload / download) ----------------------------

    public const string WebDavFolder = "/OpenCodeAccountManager/";

    /// <summary>True when a (protected) WebDAV app password is stored.</summary>
    public bool HasWebDavPassword => !string.IsNullOrEmpty(Settings.WebDavPassword);

    public WebDavOptions BuildWebDavOptions() => new()
    {
        BaseUrl = Settings.WebDavUrl ?? string.Empty,
        Username = Settings.WebDavUsername ?? string.Empty,
        Password = SecretProtector.UnprotectText(Settings.WebDavPassword) ?? string.Empty,
        Folder = WebDavFolder,
    };

    /// <summary>Stores a new app password (protected); null/empty keeps the existing one.</summary>
    public void SetWebDavPassword(string? plain)
    {
        if (!string.IsNullOrEmpty(plain)) { Settings.WebDavPassword = SecretProtector.ProtectText(plain); }
    }

    public Task<AccountSnapshot> LoadSnapshotAsync(CancellationToken ct = default)
        => Snapshot.GetSnapshotAsync(ct);

    public Task<SwitchResult> SwitchAsync(string credentialId, CancellationToken ct = default)
        => Switch.SwitchAsync(credentialId, ct);

    public Task<DeleteResult> DeleteAsync(AccountRow row, CancellationToken ct = default)
        => Actions.RemoveManagedAccountAsync(row.CredentialId, row.Provider, ct);

    public async Task<ApiSaveResult> SaveApiAsync(AccountRow row, string displayName, string baseUrl, IReadOnlyList<string> models, string? newKey, CancellationToken ct = default)
    {
        var save = await Actions.UpdateManagedApiProviderAsync(row.CredentialId, row.Provider, displayName, baseUrl, models, newKey, ct).ConfigureAwait(false);
        return await VerifyApiSaveAsync(save, OpenAiApiProtocol.PackageFor(OpenAiApiProtocol.UsesResponses(row.Definition?.Package))).ConfigureAwait(false);
    }

    public async Task<ApiSaveResult> SaveApiAsync(AccountRow row, string displayName, string baseUrl, IReadOnlyList<string> models, string? newKey, bool useResponses, CancellationToken ct = default)
    {
        var save = await Actions.UpdateManagedApiProviderAsync(row.CredentialId, row.Provider, displayName, baseUrl, models, newKey, useResponses, ct).ConfigureAwait(false);
        return await VerifyApiSaveAsync(save, OpenAiApiProtocol.PackageFor(useResponses)).ConfigureAwait(false);
    }

    private async Task<ApiSaveResult> VerifyApiSaveAsync(ApiSaveResult save, string expectedPackage)
    {
        if (!save.Updated || !save.Complete) { return save; }
        // After a completed write, a read timeout/cancel is not a failed save. Never redo a write.
        var runtime = await RuntimeVerifier.VerifyAsync(save.ProviderID, expectedPackage).ConfigureAwait(false);
        save.RuntimeVerified = runtime.State == ProviderRuntimeState.Applied;
        save.Warning = runtime.Message;
        return save;
    }

    /// <summary>The OAuth client follows the platform's default proxy settings, like the quota queries do.</summary>
    public CodexOAuthService CreateOAuthService() => new(Secrets, useProxy: true);

    public async Task EnsureBrowserOAuthAsync()
    {
        var integration = await Api.GetIntegrationAsync("openai").ConfigureAwait(false);
        var methods = JsonMerge.Get(JsonMerge.Get(integration, "data"), "methods") as JsonArray;
        if (methods is null || !methods.Any(m => JsonMerge.GetString(m, "id") == "chatgpt-browser" && JsonMerge.GetString(m, "type") == "oauth"))
        {
            throw new OpenCodeException("当前 OpenCode 不支持 chatgpt-browser OAuth，请使用兼容的 V2 版本。");
        }
    }

    public Task<CodexAddPreview> PreviewCodexAuthorizationAsync(JsonObject account)
        => Actions.PreviewCodexAuthorizationAsync(account);

    public Task<AccountAddResult> SaveCodexAuthorizationAsync(CodexAddPreview preview, bool replaceExisting)
        => Actions.SaveCodexAuthorizationAsync(preview, replaceExisting);

    public async Task<AccountAddResult> CreateApiProviderAsync(string id, string name, string url, IReadOnlyList<string> models, string key, bool useResponses)
    {
        var added = await Actions.CreateManagedApiProviderAsync(id, name, url, models, key, useResponses).ConfigureAwait(false);
        if (!added.Saved || !added.Complete) { return added; }
        var runtime = await RuntimeVerifier.VerifyAsync(id, OpenAiApiProtocol.PackageFor(useResponses)).ConfigureAwait(false);
        return new AccountAddResult
        {
            Saved = added.Saved, Complete = added.Complete, Kind = added.Kind,
            CredentialID = added.CredentialID, ProviderID = added.ProviderID, Label = added.Label,
            Backup = added.Backup, Warning = runtime.Message, RuntimeVerified = runtime.State == ProviderRuntimeState.Applied,
        };
    }

    public Task<ExportResult> ExportAsync(IReadOnlyList<string> credentialIds, string path, CancellationToken ct = default)
        => Export.ExportAsync(credentialIds, path, ct);

    public Task<ImportPreview> PreviewAsync(string path, CancellationToken ct = default)
        => Import.PreviewAsync(path, ct);

    public Task<ImportResult> ImportAsync(ImportPreview preview, CancellationToken ct = default)
        => Import.ImportAsync(preview, ct);

    public Task<List<AccountQuotaResult>> QueryAsync(IReadOnlyList<AccountRow> rows, IProgress<AccountQuotaResult>? progress, CancellationToken ct = default, bool forceSubscription = false)
        => QuotaSelector.RunBatchAsync(rows, progress, ct, forceSubscription);

    /// <summary>Persists a live subscription expiry into the local mirror (best effort).</summary>
    public Task SaveSubscriptionAsync(string accountId, string email, DateTimeOffset activeUntil, CancellationToken ct = default)
        => Subscriptions.UpdateActiveUntilAsync(accountId, email, activeUntil, ct);

    public Task ClearSubscriptionAsync(string accountId, string email, CancellationToken ct = default)
        => Subscriptions.ClearActiveUntilAsync(accountId, email, ct);

    public Task<ResetCreditList> GetResetCreditsAsync(AccountRow row, CancellationToken ct = default)
        => Reset.GetListAsync(row, ct);

    public Task<ResetConsumeResult> ConsumeResetCreditAsync(AccountRow row, string creditId, CancellationToken ct = default)
        => Reset.ConsumeAsync(row, creditId, ct);

    /// <summary>All credential ids currently saved (for reference only).</summary>
    public async Task<List<JsonNode>> GetCredentialsAsync(CancellationToken ct = default)
    {
        var array = await Api.GetCredentialsAsync(ct).ConfigureAwait(false);
        return array.Where(n => n is not null).Select(n => n!).ToList();
    }
}
