using AccountManager.App.Services;
using AccountManager.Core.Actions;
using AccountManager.Core.Models;
using AccountManager.Core.OpenCode;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

public sealed partial class AddAccountViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAddAccountServices _services;
    private readonly IDialogService _dialogs;
    private readonly Func<string, CancellationToken, Task> _openAsync;
    private readonly string _providerId = AccountActions.NewApiProviderId();

    // Stateless opener shared across default constructors (keeps the instance API used by tests).
    private static readonly ShellLauncher SharedLauncher = new();
    private IBrowserOAuthSession? _session;
    private CodexAddPreview? _preview;
    private Task _cleanup = Task.CompletedTask;
    private int _generation;
    private bool _disposed;

    public AddAccountViewModel(
        IAddAccountServices services,
        IDialogService dialogs,
        Action<string>? openBrowser = null,
        Func<string, CancellationToken, Task>? openAsync = null)
    {
        _services = services;
        _dialogs = dialogs;
        // An explicit async opener wins; then the legacy Action is kept working by wrapping it;
        // otherwise the real OS launcher is used. There is no global switch.
        if (openAsync is not null) { _openAsync = openAsync; }
        else if (openBrowser is not null) { var action = openBrowser; _openAsync = (url, _) => { action(url); return Task.CompletedTask; }; }
        else { _openAsync = static (url, ct) => SharedLauncher.OpenAsync(url, ct); }
    }

    public event EventHandler? CloseRequested;
    public AccountAddResult? Result { get; private set; }

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _waitingForAuthorization;
    [ObservableProperty] private bool _authorizationReady;
    [ObservableProperty] private bool _authorizationVerifying;
    [ObservableProperty] private string _authorizationUrl = string.Empty;
    [ObservableProperty] private string _callbackUrl = string.Empty;
    [ObservableProperty] private string _statusText = "正在准备 OpenAI OAuth 授权链接 …";
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _baseUrl = string.Empty;
    [ObservableProperty] private string _modelsText = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private bool _useResponses;

    public bool CanOperate => !IsBusy && !IsSaving && !AuthorizationVerifying && !_disposed;
    public bool IsWorking => IsBusy || IsSaving || AuthorizationVerifying;
    public bool CanOpenBrowser => CanOperate && IsOAuthTab && !AuthorizationReady;
    public bool CanSubmitCallback => CanOperate && IsOAuthTab && WaitingForAuthorization && !string.IsNullOrWhiteSpace(CallbackUrl);
    public bool CanSaveOAuth => CanOperate && IsOAuthTab && AuthorizationReady;
    public bool CanSaveApi => CanOperate && IsApiTab;
    public bool HasAuthorizationUrl => !string.IsNullOrEmpty(AuthorizationUrl);
    public bool IsOAuthTab => SelectedTabIndex == 0;
    public bool IsApiTab => SelectedTabIndex == 1;

    /// <summary>Radio helper: Chat Completions is the inverse of the Responses toggle.</summary>
    public bool UseChat
    {
        get => !UseResponses;
        set { if (value) { UseResponses = false; } }
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnIsSavingChanged(bool value) => NotifyCommands();
    partial void OnWaitingForAuthorizationChanged(bool value) => NotifyCommands();
    partial void OnAuthorizationReadyChanged(bool value) => NotifyCommands();
    partial void OnAuthorizationVerifyingChanged(bool value) => NotifyCommands();
    partial void OnCallbackUrlChanged(string value) => NotifyCommands();
    partial void OnAuthorizationUrlChanged(string value) => OnPropertyChanged(nameof(HasAuthorizationUrl));
    partial void OnUseResponsesChanged(bool value) => OnPropertyChanged(nameof(UseChat));
    partial void OnApiKeyChanged(string value) => _services.Secrets.RegisterValue(value);

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsOAuthTab));
        OnPropertyChanged(nameof(IsApiTab));
        NotifyCommands();
        if (value != 0)
        {
            _ = CancelAuthorizationAsync();
            ErrorText = string.Empty;
            StatusText = "添加会先生成加密备份，不覆盖现有供应商；密钥只保存到 OpenCode 本地凭据。";
        }
        else
        {
            StatusText = "正在准备授权链接 …";
            _ = PrimeAuthorizationAsync();
        }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(CanOpenBrowser));
        OnPropertyChanged(nameof(CanSubmitCallback));
        OnPropertyChanged(nameof(CanSaveOAuth));
        OnPropertyChanged(nameof(CanSaveApi));
        OpenBrowserCommand.NotifyCanExecuteChanged();
        RestartAuthorizationCommand.NotifyCanExecuteChanged();
        SubmitCallbackCommand.NotifyCanExecuteChanged();
        SaveOAuthCommand.NotifyCanExecuteChanged();
        SaveApiCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenBrowser))]
    private async Task OpenBrowserAsync()
    {
        if (!CanOpenBrowser) { return; }
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            if (!await EnsureAuthorizationSessionAsync()) { return; }
            await _openAsync(AuthorizationUrl, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled opening the browser; keep the pending authorization untouched.
        }
        catch (Exception)
        {
            // Never surface the opener's message: it can embed the full authorization URL.
            ErrorText = _session is not null && !_session.Completion.IsCompleted
                ? "未能自动打开浏览器，请复制授权链接到浏览器中打开。"
                : "未能自动打开浏览器，请手动打开授权链接。";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Prepares the OAuth link as soon as the dialog opens so the user sees it without clicking.</summary>
    public async Task PrimeAuthorizationAsync()
    {
        if (_disposed || !IsOAuthTab) { return; }
        if (_session is not null && !_session.Completion.IsCompleted) { return; }
        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            if (!await EnsureAuthorizationSessionAsync() && IsOAuthTab)
            {
                StatusText = "未能准备授权链接，请点击“重新授权”。";
            }
        }
        catch (Exception ex)
        {
            if (IsOAuthTab)
            {
                ErrorText = _services.Secrets.Redact(ex.Message);
                StatusText = "未能准备授权链接，请点击“重新授权”。";
            }
        }
        finally { IsBusy = false; }
    }

    /// <summary>Starts (or reuses) an OAuth session, publishing its authorization URL. Does not open a browser.</summary>
    private async Task<bool> EnsureAuthorizationSessionAsync()
    {
        if (_session is not null && !_session.Completion.IsCompleted) { return !string.IsNullOrEmpty(AuthorizationUrl); }
        await CancelAuthorizationAsync();
        await _cleanup;
        if (_disposed || SelectedTabIndex != 0) { return false; }
        StatusText = "正在准备 OpenAI OAuth 授权链接 …";
        await _services.EnsureBrowserOAuthAsync();
        if (_disposed || SelectedTabIndex != 0) { return false; }
        _session = _services.StartBrowserOAuthSession();
        AuthorizationUrl = _session.AuthorizationUrl;
        WaitingForAuthorization = true;
        StatusText = "等待浏览器授权（10 分钟内有效）。无法自动回调时，请粘贴完整回调地址。";
        _ = ObserveAuthorizationAsync(_session, _generation);
        return !string.IsNullOrEmpty(AuthorizationUrl);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task RestartAuthorizationAsync()
    {
        if (!CanOperate) { return; }
        await CancelAuthorizationAsync();
        await OpenBrowserAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSubmitCallback))]
    private async Task SubmitCallbackAsync()
    {
        if (!CanSubmitCallback || _session is null) { return; }
        var generation = _generation;
        IsBusy = true;
        ErrorText = string.Empty;
        StatusText = "正在校验回调并交换令牌 …";
        try { await _session.SubmitCallbackAsync(CallbackUrl); }
        catch (Exception ex) { if (!_disposed && generation == _generation) { ErrorText = _services.Secrets.Redact(ex.Message); } }
        finally { IsBusy = false; }
    }

    private async Task ObserveAuthorizationAsync(IBrowserOAuthSession session, int generation)
    {
        try
        {
            var account = await session.Completion;
            if (_disposed || generation != _generation) { return; }
            WaitingForAuthorization = false;
            AuthorizationVerifying = true;
            StatusText = "授权完成，正在核对账号身份 …";
            var preview = await _services.PreviewCodexAuthorizationAsync(account);
            if (_disposed || generation != _generation) { return; }
            _preview = preview;
            WaitingForAuthorization = false;
            AuthorizationReady = true;
            CallbackUrl = string.Empty;
            StatusText = "已授权：" + preview.Label + " · 工作区 " + preview.Workspace
                + (preview.HasExistingAccount ? "。该账号已存在，保存时会询问是否更新。" : "。点击“保存账号”完成添加。");
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _generation) { return; }
            WaitingForAuthorization = false;
            AuthorizationReady = false;
            ErrorText = _services.Secrets.Redact(ex.Message);
            StatusText = "授权未完成，可点击“重新授权”生成新链接。";
        }
        finally
        {
            if (!_disposed && generation == _generation) { AuthorizationVerifying = false; }
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveOAuth))]
    private async Task SaveOAuthAsync()
    {
        if (!CanSaveOAuth) { return; }
        var preview = _preview;
        if (preview is null) { return; }
        IsSaving = true;
        ErrorText = string.Empty;
        try
        {
            var replace = preview.HasExistingAccount && await _dialogs.ConfirmAsync("更新已有 Codex 账号",
                preview.Label + " · 工作区 " + preview.Workspace
                + "\n\n该账号已存在。是否使用本次授权更新其凭据？会先加密备份，再创建、验证新凭据，最后移除旧凭据；不会切到其它账号。", dangerous: true);
            if (preview.HasExistingAccount && !replace) { return; }
            StatusText = "正在备份并保存 Codex 账号 …";
            Result = await _services.SaveCodexAuthorizationAsync(preview, replace);
        }
        catch (Exception ex) { ErrorText = _services.Secrets.Redact(ex.Message); }
        finally { IsSaving = false; }
        if (Result is not null) { CloseRequested?.Invoke(this, EventArgs.Empty); }
    }

    [RelayCommand(CanExecute = nameof(CanSaveApi))]
    private async Task SaveApiAsync()
    {
        if (!CanSaveApi) { return; }
        IsSaving = true;
        ErrorText = string.Empty;
        try
        {
            var name = ApiProviderValidation.Name(DisplayName);
            var url = ApiProviderValidation.Endpoint(BaseUrl);
            var models = ApiProviderValidation.ParseModels(ModelsText);
            var key = ApiProviderValidation.Key(ApiKey, required: true);
            StatusText = "正在备份并添加 API 供应商 …";
            Result = await _services.CreateApiProviderAsync(_providerId, name, url, models, key, UseResponses);
        }
        catch (Exception ex) { ErrorText = _services.Secrets.Redact(ex.Message); }
        finally { IsSaving = false; }
        if (Result is not null) { CloseRequested?.Invoke(this, EventArgs.Empty); }
    }

    private Task CancelAuthorizationAsync()
    {
        _generation++;
        var session = _session;
        _session = null;
        _preview = null;
        AuthorizationReady = false;
        AuthorizationVerifying = false;
        WaitingForAuthorization = false;
        AuthorizationUrl = string.Empty;
        CallbackUrl = string.Empty;
        _cleanup = CleanupAsync(_cleanup, session);
        return _cleanup;
    }

    private static async Task CleanupAsync(Task previous, IBrowserOAuthSession? session)
    {
        await previous;
        try { if (session is not null) { await session.DisposeAsync(); } }
        catch { /* a cancelled/expired session must not fault the dialog */ }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        ApiKey = string.Empty;
        await CancelAuthorizationAsync();
    }
}
