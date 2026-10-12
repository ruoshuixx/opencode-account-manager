using System.Net.Http;
using AccountManager.App.Services;
using AccountManager.Core.Export;
using AccountManager.Core.Sync;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

/// <summary>
/// Manual WebDAV sync: upload a freshly exported, timestamped JSON, or download the newest
/// file from the cloud folder and feed the existing import preview. No automatic/background
/// sync and no merge; the user drives it and confirms each direction. The synced file is the
/// plaintext account JSON, so HTTPS is expected.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private static readonly HttpClient SyncHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    [RelayCommand]
    private async Task SyncUploadAsync()
    {
        if (IsBusy || IsUpdating) { return; }

        if (!TryCreateSyncClient(out var client, out var problem))
        {
            await _dialogs.ShowWarningAsync("同步未配置", problem);
            return;
        }

        var ids = Accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.Row.CredentialId))
            .Select(a => a.Row.CredentialId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            await _dialogs.ShowWarningAsync("无可上传的账号", "请先点击「读取」加载本机 OpenCode 账号。");
            return;
        }

        // New export every upload, with a date-time suffix so earlier uploads are kept.
        var fileName = ExportService.GetExportFileName(DateTime.Now);
        var message = "将重新导出本机 " + ids.Count + " 个账号（含明文令牌/密钥）并上传到：\n"
            + _services.Settings.WebDavUrl + "\n\n云端文件：" + client.Folder + fileName;
        if (!client.IsSecure) { message += "\n\n⚠ 当前地址不是 HTTPS，凭据可能在传输中被窃听，建议改用 https://。"; }
        message += "\n\n是否继续？";
        if (!await _dialogs.ConfirmAsync("上传到 WebDAV", message, dangerous: !client.IsSecure)) { return; }

        IsBusy = true;
        StatusText = "正在上传到 WebDAV …";
        string? directory = null;
        try
        {
            directory = Path.Combine(Path.GetTempPath(), "OpenCodeAccountManager-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, fileName);

            var result = await Task.Run(() => _services.ExportAsync(ids, file)).ConfigureAwait(true);
            var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(true);
            if (bytes.Length == 0) { throw new WebDavException("导出结果为空，已取消上传。"); }

            await client.UploadAsync(fileName, bytes).ConfigureAwait(true);
            StatusText = "已上传 " + result.Count + " 个账号到 " + fileName + "。";
            await _dialogs.ShowInfoAsync("上传完成", StatusText
                + (string.IsNullOrWhiteSpace(result.Warning) ? "" : "\n\n" + result.Warning));
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("上传失败", StatusText);
        }
        finally
        {
            CleanUpSyncDirectory(directory);
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncDownloadAsync()
    {
        if (IsBusy || IsUpdating) { return; }

        if (!TryCreateSyncClient(out var client, out var problem))
        {
            await _dialogs.ShowWarningAsync("同步未配置", problem);
            return;
        }

        IsBusy = true;
        StatusText = "正在读取云端文件列表 …";
        string? directory = null;
        try
        {
            var files = await client.ListAsync().ConfigureAwait(true);
            if (files.Count == 0)
            {
                StatusText = "云端暂无同步文件。";
                await _dialogs.ShowInfoAsync("没有可下载的内容", "云端目录还没有账号 JSON。请先在另一台设备上传。");
                return;
            }

            // Newest first; unknown timestamps fall back to the file name.
            var newest = files
                .OrderByDescending(f => f.LastModified ?? DateTimeOffset.MinValue)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .First();

            var message = "将从云端下载最新一份账号 JSON 并进入导入预览：\n\n"
                + client.Folder + newest.Name
                + (newest.LastModified is { } t ? "\n时间：" + t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "")
                + "\n\n下载后仍需在预览里勾选并「确认导入」才会写入本机。是否继续？";
            if (!await _dialogs.ConfirmAsync("从 WebDAV 下载", message)) { return; }

            StatusText = "正在从 WebDAV 下载 " + newest.Name + " …";
            var bytes = await client.DownloadAsync(newest.Name).ConfigureAwait(true);
            if (bytes is null || bytes.Length == 0)
            {
                StatusText = "云端文件已不存在，请重试。";
                await _dialogs.ShowWarningAsync("下载失败", StatusText);
                return;
            }

            directory = Path.Combine(Path.GetTempPath(), "OpenCodeAccountManager-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "opencode-download.json");
            await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(true);

            var preview = await Task.Run(() => _services.PreviewAsync(file)).ConfigureAwait(true);
            _pendingImport = preview;
            ImportRows.Clear();
            foreach (var item in preview.Rows) { ImportRows.Add(PreviewToRow(item)); }
            ImportPanelVisible = true;
            ImportSummary = "云端 " + newest.Name + " 预览 " + preview.Rows.Count + " 条；选择后点击「确认导入」。";
            StatusText = ImportSummary;
        }
        catch (Exception ex)
        {
            StatusText = _services.Secrets.Redact(ex.Message);
            await _dialogs.ShowWarningAsync("下载失败", StatusText);
        }
        finally
        {
            IsBusy = false;
            CleanUpSyncDirectory(directory);
        }
    }

    private bool TryCreateSyncClient(out WebDavClient client, out string problem)
    {
        client = null!;
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(_services.Settings.WebDavUrl) ||
            string.IsNullOrWhiteSpace(_services.Settings.WebDavUsername))
        {
            problem = "请先在「设置」里填写 WebDAV 地址、账号和应用密码。";
            return false;
        }

        if (!_services.HasWebDavPassword)
        {
            problem = "请先在「设置」里填写 WebDAV 应用密码（坚果云等需生成应用密码，不是登录密码）。";
            return false;
        }

        try
        {
            client = new WebDavClient(_services.BuildWebDavOptions(), SyncHttp);
            return true;
        }
        catch (Exception ex)
        {
            problem = _services.Secrets.Redact(ex.Message);
            return false;
        }
    }

    private static void CleanUpSyncDirectory(string? directory)
    {
        if (directory is null) { return; }
        try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
    }
}
