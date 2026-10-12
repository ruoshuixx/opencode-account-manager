using AccountManager.App.Services;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AccountManager.App.Views;

/// <summary>CLI path override, auto-refresh interval, auto-switch, and WebDAV sync.</summary>
public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        ScalingChanged += (_, _) => DialogWindowBounds.Fit(this);
        PositionChanged += (_, _) => DialogWindowBounds.Fit(this);
    }

    public SettingsResult? Result { get; private set; }

    public static async Task<SettingsResult?> ShowAsync(Window owner, SettingsInput input)
    {
        var dialog = new SettingsDialog();
        dialog.CliBox.Text = input.CliPath;
        dialog.IntervalBox.Value = input.AutoRefreshMinutes;
        dialog.AutoSwitchBox.IsChecked = input.AutoSwitchAccount;
        dialog.WebDavUrlBox.Text = input.WebDavUrl;
        dialog.WebDavUserBox.Text = input.WebDavUsername;
        dialog.WebDavPassBox.Watermark = input.WebDavPasswordStored ? "应用密码（已保存，留空保持不变）" : "应用密码";
        DialogWindowBounds.Fit(dialog, owner, center: true);
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    private void OnHelp(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control) { ToolTip.SetIsOpen(control, !ToolTip.GetIsOpen(control)); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnCancel(this, new RoutedEventArgs()); e.Handled = true; }
        base.OnKeyDown(e);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) { return; }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 OpenCode CLI 可执行文件",
            AllowMultiple = false,
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path)) { CliBox.Text = path; }
    }

    private void OnSave(object? sender, RoutedEventArgs e) => CloseWith(SettingsAction.None);

    private void OnUpload(object? sender, RoutedEventArgs e) => CloseWith(SettingsAction.Upload);

    private void OnDownload(object? sender, RoutedEventArgs e) => CloseWith(SettingsAction.Download);

    private void CloseWith(SettingsAction action)
    {
        Result = new SettingsResult
        {
            CliPath = CliBox.Text?.Trim() ?? string.Empty,
            AutoRefreshMinutes = (int)Math.Max(1, IntervalBox.Value ?? 5),
            AutoSwitchAccount = AutoSwitchBox.IsChecked == true,
            WebDavUrl = WebDavUrlBox.Text?.Trim() ?? string.Empty,
            WebDavUsername = WebDavUserBox.Text?.Trim() ?? string.Empty,
            WebDavPassword = string.IsNullOrEmpty(WebDavPassBox.Text) ? null : WebDavPassBox.Text,
            Action = action,
        };
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
