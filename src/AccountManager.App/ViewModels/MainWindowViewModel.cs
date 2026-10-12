using System.Collections.ObjectModel;
using System.Windows.Input;
using AccountManager.App.Services;
using AccountManager.Core.Import;
using AccountManager.Core.Models;
using AccountManager.Core.OpenCode;
using AccountManager.Core.Quota;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

/// <summary>
/// Main window view model. Owns the account list, toolbar state, auto-refresh and the
/// import/export/quota workflows. All heavy core calls are dispatched to the thread pool.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IRowActions
{
    private readonly AppServices _services;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _autoRefreshTimer;
    private readonly Dictionary<string, (string IdentityKey, AccountQuotaResult Result)> _quotaCache = new(StringComparer.Ordinal);
    private ImportPreview? _pendingImport;
    private bool _quotaRunning;

    // Stateless opener shared across invocations (keeps the instance API used by tests).
    private static readonly ShellLauncher SharedLauncher = new();

    public MainWindowViewModel(AppServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        _services.Log.EntryWritten += (_, entry) => OnLog(entry);
        _autoRefreshMinutes = Math.Max(1, services.Settings.AutoRefreshMinutes);
        _logExpanded = services.Settings.LogExpanded;

        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(AutoRefreshMinutes) };
        _autoRefreshTimer.Tick += async (_, _) => await AutoRefreshTickAsync();
        _autoRefreshTimer.Start();

        _ = StartupAsync();
    }

    /// <summary>Read accounts right away, then run one quota pass so the list is usable immediately.</summary>
    private async Task StartupAsync()
    {
        var failedUpdate = DebInstallUpdater.TakeFailureNote(PortablePaths.Data);
        if (failedUpdate is not null) { _services.Log.Warn("上次升级未完成：" + failedUpdate); }

        await ReadOpenCodeAsync();
        if (Accounts.Any(a => a.IsCodex))
        {
            await AutoRefreshTickAsync();
        }

        if (failedUpdate is not null) { await _dialogs.ShowWarningAsync("上次升级未完成", failedUpdate); }
    }

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = new();

    public void StopBackgroundMonitoring()
    {
        _autoRefreshTimer.Stop();
    }

    public ObservableCollection<string> LogLines { get; } = new();

    public ObservableCollection<AccountRowViewModel> ImportRows { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// True only while a quota pass is running. Kept separate from <see cref="IsBusy"/> so an
    /// automatic (or manual) quota refresh never disables the rest of the UI.
    /// </summary>
    [ObservableProperty]
    private bool _isQuotaBusy;

    /// <summary>Any busy state that should show the toolbar progress bar.</summary>
    public bool IsWorking => IsBusy || IsQuotaBusy;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _currentText = "当前账号：—";

    [ObservableProperty]
    private int _autoRefreshMinutes;

    [ObservableProperty]
    private bool _logExpanded;

    [ObservableProperty]
    private bool _importPanelVisible;

    [ObservableProperty]
    private string _importSummary = string.Empty;

    [ObservableProperty]
    private string _providerWarning = string.Empty;

    [ObservableProperty]
    private string _cliStatus = string.Empty;

    partial void OnAutoRefreshMinutesChanged(int value)
    {
        _autoRefreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, value));
        _services.Settings.AutoRefreshMinutes = value;
        PersistSettings();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWorking));
    }

    partial void OnIsQuotaBusyChanged(bool value) => OnPropertyChanged(nameof(IsWorking));

    partial void OnLogExpandedChanged(bool value)
    {
        _services.Settings.LogExpanded = value;
        PersistSettings();
    }

    private void PersistSettings() => new SettingsStore().Save(_services.Settings);

    private void OnLog(Core.Logging.LogEntry entry)
    {
        var line = entry.Time.ToString("HH:mm:ss") + "  " + entry.Message;
        void Append()
        {
            LogLines.Add(line);
            while (LogLines.Count > 500) { LogLines.RemoveAt(0); }
        }

        if (Dispatcher.UIThread.CheckAccess()) { Append(); }
        else { Dispatcher.UIThread.Post(Append); }
    }

    // ---- snapshot ----------------------------------------------------------

    [RelayCommand]
    private async Task ReadOpenCodeAsync()
    {
        if (IsBusy) { return; }
        IsBusy = true;
        StatusText = "正在读取 OpenCode …";
        try
        {
            var snapshot = await Task.Run(() => _services.LoadSnapshotAsync()).ConfigureAwait(true);
            ApplySnapshot(snapshot);
            StatusText = "已读取 " + snapshot.Accounts.Count + " 个账号。";
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("读取失败", StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySnapshot(AccountSnapshot snapshot)
    {
        var previous = Accounts.ToDictionary(a => a.Key, a => (a.IsQuotaSelected, a.IsImportSelected), StringComparer.Ordinal);

        // Stable pin: the saved current Codex account goes first; everything else keeps order.
        var ordered = snapshot.Accounts
            .OrderByDescending(a => a.Kind == AccountKind.Codex && a.Active)
            .ToList();

        Accounts.Clear();
        foreach (var row in ordered)
        {
            var vm = new AccountRowViewModel(row, this);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AccountRowViewModel.IsQuotaSelected))
                {
                    OnPropertyChanged(nameof(SelectAllVisibleChecked));
                }
            };
            if (previous.TryGetValue(vm.Key, out var state))
            {
                vm.IsQuotaSelected = state.IsQuotaSelected;
                vm.IsImportSelected = state.IsImportSelected;
            }

            if (_quotaCache.TryGetValue(vm.Key, out var cached) && cached.IdentityKey == vm.IdentityKey)
            {
                ApplyQuota(vm, cached.Result, fromCache: true);
            }

            Accounts.Add(vm);
        }

        var codex = Accounts.Count(a => a.IsCodex);
        var api = Accounts.Count(a => a.IsApi);
        var current = Accounts.FirstOrDefault(a => a.IsCurrent);
        SummaryText = "共 " + Accounts.Count + " 项（Codex " + codex + " · API " + api + "） · 供应商 " + snapshot.ProviderCount;
        CurrentText = current is null ? "当前账号：—" : "当前账号：" + current.Title;
        ProviderWarning = snapshot.Warning;
        UpdateCliStatus();
        OnPropertyChanged(nameof(SelectAllVisibleChecked));
    }

    private void UpdateCliStatus()
    {
        try
        {
            CliStatus = "CLI：" + (_services.Cli.ResolveCli() ?? "未找到");
        }
        catch (Exception ex)
        {
            CliStatus = _services.Secrets.Redact(ex.Message);
        }
    }

    // ---- quota -------------------------------------------------------------

    public bool CanQuerySelected => Accounts.Any(a => a.IsQuotaSelected && a.IsCodex);

    [RelayCommand]
    private async Task QuerySelectedAsync()
    {
        var rows = Accounts.Where(a => a.IsQuotaSelected && a.IsCodex).ToList();
        if (rows.Count == 0)
        {
            StatusText = "请先勾选要查询额度的 Codex 账号（左侧“选择”列）。";
            return;
        }

        await RunQuotaAsync(rows, forceSubscription: true);
    }

    private async Task RunQuotaAsync(IReadOnlyList<AccountRowViewModel> rows, bool forceSubscription = false)
    {
        // Quota is a read-only background pass. It intentionally does NOT take the global
        // IsBusy gate, otherwise every other toolbar/row action silently no-ops while the
        // accounts are queried. A dedicated flag still prevents overlapping passes.
        if (_quotaRunning) { return; }
        _quotaRunning = true;
        IsQuotaBusy = true;
        var done = 0;
        StatusText = "正在查询额度 0/" + rows.Count + " …";
        var progress = new Progress<AccountQuotaResult>(result =>
        {
            done++;
            StatusText = "正在查询额度 " + done + "/" + rows.Count + " …";
            var target = Accounts.FirstOrDefault(a => a.Key == result.Key);
            if (target is not null)
            {
                ApplyQuota(target, result, fromCache: false);
                _quotaCache[target.Key] = (target.IdentityKey, result);
                _burn.Record(target.Key, AutoSwitchPolicy.Remaining(ToCandidate(target, result)), result.CheckedAt);
            }
        });

        try
        {
            var core = rows.Select(r => r.Row).ToList();
            var results = await Task.Run(() => _services.QueryAsync(core, progress, forceSubscription: forceSubscription)).ConfigureAwait(true);
            var ok = results.Count(r => r.State == QuotaResultState.Success);
            StatusText = "额度查询完成：" + ok + "/" + results.Count + " 成功。";
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
        }
        finally
        {
            _quotaRunning = false;
            IsQuotaBusy = false;
        }
    }

    private void ApplyQuota(AccountRowViewModel row, AccountQuotaResult result, bool fromCache)
    {
        row.Quota5h = QuotaCellViewModel.FromResult(result, primary: true);
        row.QuotaWk = QuotaCellViewModel.FromResult(result, primary: false);
        row.SetState(QuotaCellViewModel.StateText(result.State.ToString()));

        // A live subscription refresh rides along with the quota query. A value is moved and persisted
        // into the local mirror so it survives a list rebuild or restart; an authoritative "no expiry"
        // clears the shown value (rendered as "—"), so a stale date is never left on screen.
        if (result.SubscriptionRefresh == SubscriptionRefreshState.Refreshed && result.SubscriptionActiveUntil is { } until)
        {
            if (row.Row.SubscriptionExpires != until)
            {
                row.Row.SubscriptionExpires = until;
                _ = PersistSubscriptionAsync(row.Row.AccountId, row.Row.Email, until);
            }
        }
        else if (result.SubscriptionRefresh == SubscriptionRefreshState.RefreshedEmpty && row.Row.SubscriptionExpires is not null)
        {
            row.Row.SubscriptionExpires = null;
            _ = ClearSubscriptionAsync(row.Row.AccountId, row.Row.Email);
        }

        row.RefreshSubscriptionClock();

        var credits = row.IsCodex ? result.Usage?.ResetCreditsAvailable : null;
        row.HasResetCredits = credits is not null;
        row.ResetCreditsText = !row.IsCodex
            ? string.Empty
            : (credits is null ? "重置" : "重置×" + credits);
        row.ResetCreditsTooltip = !row.IsCodex
            ? string.Empty
            : (credits is null
                ? "点击查询并使用一张 Codex 重置券（会先弹出到期明细并确认）。"
                : "可用重置次数：" + credits + "（来自服务器实时额度响应）；点击查看明细并重置。");

        if (!string.IsNullOrWhiteSpace(result.Message) && result.State is QuotaResultState.Error or QuotaResultState.Expired or QuotaResultState.MissingToken)
        {
            StatusText = row.Title + "：" + result.Message;
        }
    }

    /// <summary>Fire-and-forget mirror write for a freshly refreshed subscription expiry.</summary>
    private async Task PersistSubscriptionAsync(string accountId, string email, DateTimeOffset until)
    {
        try
        {
            await _services.SaveSubscriptionAsync(accountId, email, until).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _services.Log.Warn("写回订阅缓存失败（不影响账号）：" + _services.Secrets.Redact(ex.Message));
        }
    }

    /// <summary>Fire-and-forget mirror write that clears a subscription expiry a live lookup proved absent.</summary>
    private async Task ClearSubscriptionAsync(string accountId, string email)
    {
        try
        {
            await _services.ClearSubscriptionAsync(accountId, email).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _services.Log.Warn("清除订阅缓存失败（不影响账号）：" + _services.Secrets.Redact(ex.Message));
        }
    }

    /// <summary>
    /// Header "全选" checkbox. Reading returns true when every visible row is selected; writing
    /// selects or clears every visible row (Codex and API). Query and auto-refresh still only act
    /// on Codex rows, so selecting an API row never triggers a query — it only makes the account
    /// available to export.
    /// </summary>
    public bool SelectAllVisibleChecked
    {
        get
        {
            var visible = Accounts.Where(a => a.IsVisible).ToList();
            return visible.Count > 0 && visible.All(a => a.IsQuotaSelected);
        }
        set
        {
            foreach (var row in Accounts.Where(a => a.IsVisible)) { row.IsQuotaSelected = value; }
        }
    }

    // ---- row actions -------------------------------------------------------

    public async Task SwitchAsync(AccountRowViewModel row)
    {
        if (IsBusy) { return; }
        if (row.Row.Active)
        {
            StatusText = row.Title + " 已经是当前账号。";
            return;
        }

        IsBusy = true;
        StatusText = "正在切换账号 …";
        try
        {
            var result = await Task.Run(() => _services.SwitchAsync(row.Row.CredentialId)).ConfigureAwait(true);
            StatusText = result.Changed ? "已切换并回读确认：" + result.Label : result.Label + " 已是当前账号。";
            if (result.ReauthRequired)
            {
                await _dialogs.ShowWarningAsync("需要重新登录", "该账号的访问令牌已过期：切号只激活 OpenCode 凭据，不会自动刷新登录。请先在 OpenCode 中重新连接该账号。");
            }
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("切换失败", StatusText);
        }
        finally
        {
            IsBusy = false;
        }

        await ReadOpenCodeAsync();
    }

    public async Task RefreshAsync(AccountRowViewModel row)
    {
        if (!row.IsCodex) { return; }
        // A single-row query is read-only and must leave the row's checkbox as the user set it.
        await RunQuotaAsync(new[] { row }, forceSubscription: true);
    }

    public async Task ResetAsync(AccountRowViewModel row)
    {
        if (IsBusy || !row.IsCodex) { return; }

        IsBusy = true;
        StatusText = "正在查询 " + row.Title + " 的重置次数 …";
        ResetCreditList list;
        try
        {
            list = await Task.Run(() => _services.GetResetCreditsAsync(row.Row)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            IsBusy = false;
            await _dialogs.ShowWarningAsync("查询重置次数失败", StatusText);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var target = list.AvailableByExpiry.FirstOrDefault();
        var confirmed = await _dialogs.ShowResetCreditsAsync(new ResetCreditsInput
        {
            AccountTitle = row.Title,
            List = list,
            Target = target,
        });
        if (!confirmed || target is null) { return; }

        IsBusy = true;
        StatusText = "正在重置 " + row.Title + " 的用量 …";
        try
        {
            var result = await Task.Run(() => _services.ConsumeResetCreditAsync(row.Row, target.Id)).ConfigureAwait(true);
            if (result.Succeeded)
            {
                var reset = result.WindowsReset > 0 ? "，已重置 " + result.WindowsReset + " 个窗口" : string.Empty;
                StatusText = row.Title + "：已使用一张重置券" + reset + "。";
            }
            else
            {
                StatusText = "重置返回了未知结果（code=" + result.Code + "）。";
                await _dialogs.ShowWarningAsync("重置未确认", StatusText);
            }
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("重置失败", StatusText);
        }
        finally
        {
            IsBusy = false;
        }

        // Re-read the usage endpoint so the row reflects the reset immediately.
        await RunQuotaAsync(new[] { row });
    }

    public async Task DeleteAsync(AccountRowViewModel row)
    {
        if (IsBusy) { return; }
        var detail = row.IsCodex
            ? "将永久删除该 Codex 账号在 OpenCode 中的本地凭据（凭据 " + row.Row.CredentialId + "），并先做加密备份。"
            : "将永久删除供应商 “" + row.Row.Provider + "” 在全局配置中的定义，并删除其本地 API 密钥凭据（先做加密备份）。";
        var confirmed = await _dialogs.ConfirmAsync("删除账号", row.Title + "\n\n" + detail + "\n\n此操作不可撤销，是否继续？", dangerous: true);
        if (!confirmed) { return; }

        IsBusy = true;
        StatusText = "正在删除 …";
        try
        {
            var result = await Task.Run(() => _services.DeleteAsync(row.Row)).ConfigureAwait(true);
            if (result.Removed)
            {
                StatusText = "删除成功" + (string.IsNullOrWhiteSpace(result.Backup) ? "" : "；备份：" + result.Backup);
                if (!string.IsNullOrWhiteSpace(result.Warning)) { await _dialogs.ShowWarningAsync("删除完成但有提示", result.Warning); }
            }
            else
            {
                var message = string.IsNullOrWhiteSpace(result.Warning) ? "删除未完成。" : result.Warning;
                StatusText = message;
                await _dialogs.ShowWarningAsync("删除未完成", message);
            }
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("删除失败", StatusText);
        }
        finally
        {
            IsBusy = false;
        }

        await ReadOpenCodeAsync();
    }

    /// <summary>
    /// Toolbar delete: removes every checked (visible) row in one confirmed pass. Reuses the
    /// same core delete as the per-row button, so each Codex credential / API provider is
    /// still backed up before removal. Nothing is deleted unless the user confirms the list.
    /// </summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (IsBusy) { return; }
        var rows = Accounts.Where(a => a.IsQuotaSelected && a.IsVisible).ToList();
        if (rows.Count == 0)
        {
            StatusText = "请先勾选要删除的账号或 API（左侧“选择”列，或顶部“全选”）。";
            return;
        }

        var codex = rows.Count(r => r.IsCodex);
        var api = rows.Count - codex;
        var listed = string.Join("\n", rows.Take(12).Select(r => "· " + r.Title));
        if (rows.Count > 12) { listed += "\n… 其余 " + (rows.Count - 12) + " 项"; }
        var confirmed = await _dialogs.ConfirmAsync("删除勾选账号",
            "将永久删除勾选的 " + rows.Count + " 项（Codex " + codex + " · API " + api + "）：\n\n" + listed +
            "\n\nCodex 删除本地凭据，API 删除供应商定义与密钥；每项删除前均做加密备份。此操作不可撤销，是否继续？",
            dangerous: true);
        if (!confirmed) { return; }

        IsBusy = true;
        var removed = 0;
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                StatusText = "正在删除 " + (i + 1) + "/" + rows.Count + "：" + row.Title;
                try
                {
                    var result = await Task.Run(() => _services.DeleteAsync(row.Row)).ConfigureAwait(true);
                    if (result.Removed) { removed++; }
                    else { failures.Add(row.Title + "：" + (string.IsNullOrWhiteSpace(result.Warning) ? "未删除。" : result.Warning)); }
                }
                catch (Exception ex)
                {
                    failures.Add(row.Title + "：" + _services.Secrets.Redact(ex.Message));
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        if (failures.Count > 0)
        {
            await _dialogs.ShowWarningAsync("部分删除未完成",
                "成功 " + removed + " 项，失败 " + failures.Count + " 项：\n\n" + string.Join("\n", failures));
        }

        await ReadOpenCodeAsync();
        StatusText = "已删除 " + removed + "/" + rows.Count + " 项。" + (failures.Count > 0 ? " " + failures.Count + " 项失败。" : "");
    }

    public async Task ConfigureAsync(AccountRowViewModel row)
    {
        if (IsBusy || !row.IsApi) { return; }
        var input = new ApiConfigInput
        {
            ProviderId = row.Row.Provider,
            Title = row.Title,
            DisplayName = row.Row.ProviderName,
            BaseUrl = row.Row.BaseUrl,
            Models = row.Row.ModelIds.Count > 0 ? row.Row.ModelIds : row.Row.Models,
            UseResponses = OpenAiApiProtocol.UsesResponses(row.Row.Definition?.Package),
            HasCredential = !string.IsNullOrWhiteSpace(row.Row.CredentialId),
        };

        IsBusy = true;
        string? outcome = null;
        try
        {
            var result = await _dialogs.ShowApiConfigAsync(input);
            if (result is null) { return; }
            StatusText = "正在保存供应商配置 …";
            var save = await Task.Run(() => _services.SaveApiAsync(row.Row, result.DisplayName, result.BaseUrl, result.Models, result.NewKey, result.UseResponses)).ConfigureAwait(true);
            StatusText = save.Updated && save.Complete ? "已保存供应商配置。" : "保存结果需要检查。";
            if (!string.IsNullOrWhiteSpace(save.Warning)) { StatusText += " " + save.Warning; }
            outcome = StatusText;
            if (!save.Updated || !save.Complete) { await _dialogs.ShowWarningAsync("保存需要检查", string.IsNullOrWhiteSpace(save.Warning) ? "未做任何修改。" : save.Warning); }
            else if (save.RuntimeVerified == false) { await _dialogs.ShowWarningAsync("已保存，后台生效待确认", save.Warning); }
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("保存失败", StatusText);
        }
        finally
        {
            IsBusy = false;
        }

        await ReadOpenCodeAsync();
        if (outcome is not null) { StatusText = outcome; }
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        if (IsBusy) { return; }
        IsBusy = true; // Pause quota/auto-switch for the entire modal, not only the final write.
        AccountAddResult? result = null;
        try { result = await _dialogs.ShowAddAccountAsync(_services); }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("添加账号失败", StatusText);
        }
        finally { IsBusy = false; }
        if (result is null) { return; }
        await ReadOpenCodeAsync();
        StatusText = result.Complete ? "已保存：" + result.Label + "。" : "添加结果需要检查。";
        if (!string.IsNullOrWhiteSpace(result.Warning)) { StatusText += " " + result.Warning; }
        if (!result.Complete) { await _dialogs.ShowWarningAsync("添加结果需要检查", result.Warning); }
        else if (result.RuntimeVerified == false) { await _dialogs.ShowWarningAsync("已保存，后台生效待确认", result.Warning); }
    }

    // ---- import / export ---------------------------------------------------

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsBusy) { return; }
        var path = await _dialogs.PickOpenFileAsync("选择 Cockpit 导出文件", "opencode.json");
        if (string.IsNullOrWhiteSpace(path)) { return; }

        IsBusy = true;
        StatusText = "正在解析导入文件 …";
        try
        {
            var preview = await Task.Run(() => _services.PreviewAsync(path));
            _pendingImport = preview;
            ImportRows.Clear();
            foreach (var item in preview.Rows)
            {
                ImportRows.Add(PreviewToRow(item));
            }

            ImportPanelVisible = true;
            ImportSummary = "预览 " + preview.Rows.Count + " 条；选择要导入的账号后点击“确认导入”。";
            StatusText = ImportSummary;
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("解析失败", StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static AccountRowViewModel PreviewToRow(ImportPreviewRow item)
    {
        var row = new AccountRow
        {
            Key = item.Key,
            Kind = item.Kind,
            Label = item.Label,
            Provider = item.Provider,
            Identity = item.Identity,
            Workspace = item.Workspace,
            Plan = item.Plan,
            Expires = item.Expires,
            Status = item.Status,
            StatusCode = "Import",
        };
        return new AccountRowViewModel(row, new NoopRowActions()) { IsImportSelected = true };
    }

    [RelayCommand]
    private void CancelImport()
    {
        _pendingImport = null;
        ImportRows.Clear();
        ImportPanelVisible = false;
        StatusText = "已取消导入。";
    }

    [RelayCommand]
    private async Task CommitImportAsync()
    {
        if (IsBusy) { return; }
        if (_pendingImport is null) { return; }
        var preview = _pendingImport;
        var selectedKeys = ImportRows
            .Where(r => r.IsImportSelected)
            .Select(r => r.Row.Key)
            .ToHashSet(StringComparer.Ordinal);
        var selected = selectedKeys.Count;
        if (selected == 0)
        {
            await _dialogs.ShowWarningAsync("未选择账号", "请至少勾选一个要导入的账号。");
            return;
        }

        preview.SelectedKeys = selectedKeys;
        const string modeText = "完整导入（可能覆盖现有令牌）";
        var confirmed = await _dialogs.ConfirmAsync(
            "确认导入",
            "模式：" + modeText + "。写入前会先做加密备份；导入不会切换当前账号。\n\n将只导入已勾选的 " + selected + " / " + ImportRows.Count + " 条。是否继续？");
        if (!confirmed) { return; }

        IsBusy = true;
        StatusText = "正在导入 …";
        try
        {
            var result = await Task.Run(() => _services.ImportAsync(preview)).ConfigureAwait(true);
            StatusText = "导入完成" + (result.Failed > 0 ? "（失败 " + result.Failed + "）" : "") + "；备份：" + result.Backup;
            ImportPanelVisible = false;
            _pendingImport = null;
            ImportRows.Clear();
            if (result.Messages.Count > 0)
            {
                await _dialogs.ShowInfoAsync("导入结果", string.Join(Environment.NewLine, result.Messages));
            }
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("导入失败", StatusText);
        }
        finally
        {
            IsBusy = false;
        }

        await ReadOpenCodeAsync();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsBusy) { return; }
        var ids = Accounts.Where(a => a.IsQuotaSelected && !string.IsNullOrWhiteSpace(a.Row.CredentialId))
            .Select(a => a.Row.CredentialId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            await _dialogs.ShowWarningAsync("无可导出的账号", "请先在「选择」列勾选要导出的已保存账号。");
            return;
        }

        var target = await _dialogs.PickSaveFileAsync("导出账号（明文）", AccountManager.Core.Export.ExportService.GetExportFileName(DateTime.Now));
        if (string.IsNullOrWhiteSpace(target)) { return; }

        var confirmed = await _dialogs.ConfirmAsync("确认导出", "导出文件是明文凭据，请妥善保管。\n\n是否继续？");
        if (!confirmed) { return; }

        IsBusy = true;
        StatusText = "正在导出 …";
        try
        {
            var result = await Task.Run(() => _services.ExportAsync(ids, target)).ConfigureAwait(true);
            StatusText = "已导出 " + result.Count + " 个账号到 " + result.Path;
            await _dialogs.ShowInfoAsync("导出完成", StatusText + (string.IsNullOrWhiteSpace(result.Warning) ? "" : "\n\n" + result.Warning));
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("导出失败", StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- auto refresh ------------------------------------------------------

    private readonly QuotaBurnEstimator _burn = new();
    private DateTime _lastFullQuotaSweep = DateTime.MinValue;
    private DateTime? _lastAutoSwitch;
    private bool _allExhausted;

    /// <summary>Minimum time between two automatic switches, unless the active window is spent.</summary>
    private static readonly TimeSpan AutoSwitchCooldown = TimeSpan.FromMinutes(3);

    private async Task AutoRefreshTickAsync()
    {
        if (IsBusy || _quotaRunning) { return; }
        var autoSwitch = _services.Settings.AutoSwitchAccount;
        var current = Accounts.FirstOrDefault(a => a.IsCurrent);

        List<AccountRowViewModel> rows;
        if (autoSwitch)
        {
            // While a window is burning the active account is re-polled on the short cadence; every
            // Codex account is still swept on the configured cadence so switch targets stay fresh.
            var normal = TimeSpan.FromMinutes(Math.Max(1, _services.Settings.AutoRefreshMinutes));
            var fullSweepDue = DateTime.UtcNow - _lastFullQuotaSweep >= normal;
            if (!fullSweepDue && current is not null)
            {
                rows = new List<AccountRowViewModel> { current };
            }
            else
            {
                rows = Accounts.Where(a => a.IsCodex).ToList();
                _lastFullQuotaSweep = DateTime.UtcNow;
            }
        }
        else
        {
            rows = current is not null
                ? new List<AccountRowViewModel> { current }
                : Accounts.Where(a => a.IsCodex && a.IsQuotaSelected && a.IsVisible).ToList();
        }

        if (rows.Count == 0)
        {
            StatusText = "自动刷新：没有可刷新的 Codex 账号，已跳过本次。";
            return;
        }

        _services.Log.Info("自动刷新：查询 " + rows.Count + " 个账号。");
        await RunQuotaAsync(rows);

        if (autoSwitch && current is not null)
        {
            await EvaluateAutoSwitchAsync();
        }

        UpdateAutoRefreshInterval();
    }

    /// <summary>
    /// Switches away from the active account as either window reaches its reserve (5h 5%, weekly 2%),
    /// so a long task keeps going instead of dying mid-stream. The trigger is predictive: at the
    /// measured burn rate it fires one poll interval plus the switch latency early, so the account
    /// drains down to the buffer exactly as the new one goes live rather than past it. A spent window
    /// bypasses the cooldown; otherwise the switcher waits out <see cref="AutoSwitchCooldown"/>.
    /// </summary>
    private async Task EvaluateAutoSwitchAsync()
    {
        if (IsBusy) { return; }
        _allExhausted = false;
        var current = Accounts.FirstOrDefault(a => a.IsCurrent);
        if (current is null
            || !_quotaCache.TryGetValue(current.Key, out var curCache)
            || curCache.Result.Usage is null)
        {
            return; // No usable data for the current account — do not switch blindly.
        }

        var cur = ToCandidate(current, curCache.Result);
        var burn = _burn.BurnPerMinute(current.Key);
        var lead = AutoPollScheduler.SwitchLead(AdaptiveInterval(cur));
        if (!AutoSwitchPolicy.ShouldSwitch(cur, burn, lead)) { return; }

        if (!AutoSwitchPolicy.IsExhausted(cur)
            && _lastAutoSwitch is not null
            && DateTime.UtcNow - _lastAutoSwitch.Value < AutoSwitchCooldown)
        {
            StatusText = "自动切换：刚切换过，稍后再判断。";
            return;
        }

        var others = Accounts
            .Where(a => a.IsCodex && !a.IsCurrent && _quotaCache.TryGetValue(a.Key, out var c) && c.Result.Usage is not null)
            .Select(a => ToCandidate(a, _quotaCache[a.Key].Result))
            .ToList();

        var target = AutoSwitchSelector.Select(others, cur.Team);
        if (target is null)
        {
            _allExhausted = true;
            _services.Log.Info("自动切换：其它账号也没有可用额度，保持当前账号，恢复按设置间隔查询。");
            StatusText = "自动切换：其它账号也没有可用额度，保持当前账号。";
            return;
        }

        var targetRow = Accounts.FirstOrDefault(a => a.Key == target.Key);
        if (targetRow is null) { return; }

        _lastAutoSwitch = DateTime.UtcNow;
        _services.Log.Info("自动切换：当前账号即将触及预留线（5h " + Fmt(cur.FiveHourRemaining) + "% / 周 " + Fmt(cur.WeeklyRemaining) + "%），切到 " + targetRow.Title + "。");
        await SwitchAsync(targetRow);
    }

    /// <summary>
    /// Poll the active account on an adaptive cadence: an idle account keeps the configured
    /// interval, a fast-burning one is polled more often so the reserve is noticed in time.
    /// </summary>
    private void UpdateAutoRefreshInterval()
    {
        var normal = TimeSpan.FromMinutes(Math.Max(1, _services.Settings.AutoRefreshMinutes));
        var interval = normal;

        var current = Accounts.FirstOrDefault(a => a.IsCurrent);
        if (_services.Settings.AutoSwitchAccount
            && !_allExhausted
            && current is not null
            && _quotaCache.TryGetValue(current.Key, out var cache)
            && cache.Result.Usage is not null)
        {
            var cur = ToCandidate(current, cache.Result);
            interval = AdaptiveInterval(cur);
        }

        if (_autoRefreshTimer.Interval != interval) { _autoRefreshTimer.Interval = interval; }
    }

    /// <summary>Poll interval for the active account at its current burn rate.</summary>
    private TimeSpan AdaptiveInterval(AutoSwitchCandidate current)
    {
        var normal = TimeSpan.FromMinutes(Math.Max(1, _services.Settings.AutoRefreshMinutes));
        return AutoPollScheduler.NextInterval(
            AutoSwitchPolicy.Remaining(current),
            AutoSwitchPolicy.Reserve(current),
            _burn.BurnPerMinute(current.Key),
            normal);
    }

    private static AutoSwitchCandidate ToCandidate(AccountRowViewModel row, AccountQuotaResult result)
        => new(row.Key, row.Title, result.Usage ?? new QuotaUsage(), row.TeamKey);

    private static string Fmt(double? value) => value?.ToString("0") ?? "—";

    // ---- log / settings ----------------------------------------------------

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    [RelayCommand]
    private async Task RevealLogsAsync()
    {
        try
        {
            await SharedLauncher.OpenAsync(_services.LogDirectory);
        }
        catch (Exception)
        {
            StatusText = "无法自动打开日志目录，请手动打开：" + _services.LogDirectory;
        }
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (IsBusy) { return; }

        var result = await _dialogs.ShowSettingsAsync(new SettingsInput
        {
            CliPath = _services.Settings.CliPath ?? string.Empty,
            AutoRefreshMinutes = AutoRefreshMinutes,
            AutoSwitchAccount = _services.Settings.AutoSwitchAccount,
            WebDavUrl = _services.Settings.WebDavUrl ?? string.Empty,
            WebDavUsername = _services.Settings.WebDavUsername ?? string.Empty,
            WebDavPasswordStored = _services.HasWebDavPassword,
        });
        if (result is null) { return; }

        _services.Settings.CliPath = string.IsNullOrWhiteSpace(result.CliPath) ? null : result.CliPath.Trim();
        _services.ApplyCliPath(_services.Settings.CliPath);
        UpdateCliStatus();
        if (result.AutoRefreshMinutes > 0) { AutoRefreshMinutes = result.AutoRefreshMinutes; }
        _services.Settings.AutoSwitchAccount = result.AutoSwitchAccount;

        _services.Settings.WebDavUrl = string.IsNullOrWhiteSpace(result.WebDavUrl) ? null : result.WebDavUrl.Trim();
        _services.Settings.WebDavUsername = string.IsNullOrWhiteSpace(result.WebDavUsername) ? null : result.WebDavUsername.Trim();
        _services.SetWebDavPassword(result.WebDavPassword);

        PersistSettings();
        StatusText = "设置已保存。";

        // Sync buttons in the settings dialog run once the settings are saved.
        if (result.Action == SettingsAction.Upload) { await SyncUploadAsync(); }
        else if (result.Action == SettingsAction.Download) { await SyncDownloadAsync(); }
    }

    private sealed class NoopRowActions : IRowActions
    {
        public Task SwitchAsync(AccountRowViewModel row) => Task.CompletedTask;

        public Task RefreshAsync(AccountRowViewModel row) => Task.CompletedTask;

        public Task ResetAsync(AccountRowViewModel row) => Task.CompletedTask;

        public Task DeleteAsync(AccountRowViewModel row) => Task.CompletedTask;

        public Task ConfigureAsync(AccountRowViewModel row) => Task.CompletedTask;
    }
}
