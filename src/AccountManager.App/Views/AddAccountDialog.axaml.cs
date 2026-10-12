using AccountManager.App.Services;
using AccountManager.App.ViewModels;
using AccountManager.Core.Models;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AccountManager.App.Views;

public partial class AddAccountDialog : Window
{
    public AddAccountDialog()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (DataContext is AddAccountViewModel { IsSaving: true } vm)
            {
                e.Cancel = true;
                vm.ErrorText = "正在保存并回读校验，请等待完成后关闭，避免中断账号写入。";
            }
        };
    }

    public static async Task<AccountAddResult?> ShowAsync(Window owner, AppServices services)
    {
        var dialog = new AddAccountDialog();
        await using var vm = new AddAccountViewModel(services, new DialogService(() => dialog));
        dialog.DataContext = vm;
        vm.CloseRequested += (_, _) => dialog.Close();
        dialog.Opened += async (_, _) => await vm.PrimeAuthorizationAsync();
        await dialog.ShowDialog(owner);
        return vm.Result;
    }

    private async void OnCopyLink(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddAccountViewModel vm || string.IsNullOrWhiteSpace(vm.AuthorizationUrl)) { return; }
        try
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { vm.ErrorText = "无法访问剪贴板，请手动选中授权链接复制。"; return; }
            await clipboard.SetTextAsync(vm.AuthorizationUrl);
            vm.StatusText = "授权链接已复制，可粘贴到浏览器中打开。";
        }
        catch { vm.ErrorText = "复制失败，请手动选中授权链接复制。"; }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
