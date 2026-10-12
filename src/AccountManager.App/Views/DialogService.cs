using AccountManager.App.Services;
using AccountManager.Core.Models;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AccountManager.App.Views;

/// <summary>Avalonia implementation of <see cref="IDialogService"/>.</summary>
public sealed class DialogService : IDialogService
{
    private readonly Func<Window?> _owner;

    public DialogService(Func<Window?> owner) => _owner = owner;

    public async Task<bool> ConfirmAsync(string title, string message, bool dangerous = false)
    {
        var owner = _owner();
        if (owner is null) { return false; }
        return await MessageDialog.ShowAsync(owner, title, message, confirm: true, dangerous: dangerous);
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        var owner = _owner();
        if (owner is null) { return; }
        await MessageDialog.ShowAsync(owner, title, message, confirm: false, dangerous: false);
    }

    public async Task ShowWarningAsync(string title, string message)
    {
        var owner = _owner();
        if (owner is null) { return; }
        await MessageDialog.ShowAsync(owner, title, message, confirm: false, dangerous: false);
    }

    public async Task<string?> PickOpenFileAsync(string title, string suggestedName)
    {
        var storage = GetStorage();
        if (storage is null) { return null; }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedFileName = suggestedName,
            FileTypeFilter = new[] { JsonFilter },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        var storage = GetStorage();
        if (storage is null) { return null; }
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = new[] { JsonFilter },
        });
        return file?.TryGetLocalPath();
    }

    public async Task<ApiConfigResult?> ShowApiConfigAsync(ApiConfigInput input)
    {
        var owner = _owner();
        if (owner is null) { return null; }
        return await ApiConfigDialog.ShowAsync(owner, input);
    }

    public async Task<AccountAddResult?> ShowAddAccountAsync(AppServices services)
    {
        var owner = _owner();
        if (owner is null) { return null; }
        return await AddAccountDialog.ShowAsync(owner, services);
    }

    public async Task<bool> ShowResetCreditsAsync(ResetCreditsInput input)
    {
        var owner = _owner();
        if (owner is null) { return false; }
        return await ResetCreditsDialog.ShowAsync(owner, input);
    }

    public async Task<SettingsResult?> ShowSettingsAsync(SettingsInput input)
    {
        var owner = _owner();
        if (owner is null) { return null; }
        return await SettingsDialog.ShowAsync(owner, input);
    }

    private static readonly FilePickerFileType JsonFilter = new("Cockpit 导出 (JSON)")
    {
        Patterns = new[] { "*.json" },
        MimeTypes = new[] { "application/json" },
    };

    private IStorageProvider? GetStorage()
    {
        var owner = _owner();
        return owner is null ? null : TopLevel.GetTopLevel(owner)?.StorageProvider;
    }
}
