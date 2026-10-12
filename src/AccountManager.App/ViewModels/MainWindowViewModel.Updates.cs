using System.Net.Http;
using AccountManager.App.Services;
using AccountManager.Core.Updates;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // Dedicated public-download client: never shares account requests or authorization headers.
    private static readonly HttpClient UpdateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly GitHubUpdateService _updates = new(UpdateHttp, AppUpdateInfo.Owner, AppUpdateInfo.Repository, CurrentUpdatePlatform());
    private UpdateRelease? _availableUpdate;
    private CancellationTokenSource? _updateCancellation;

    private static UpdatePlatform CurrentUpdatePlatform()
        => !OperatingSystem.IsLinux() ? UpdatePlatform.WindowsX64
            : DebInstallUpdater.IsSupported ? UpdatePlatform.LinuxDeb : UpdatePlatform.LinuxX64;

    /// <summary>The .deb installation updates through the package manager; the portable builds replace themselves.</summary>
    private static bool AutoUpdateSupported => DebInstallUpdater.IsSupported || SelfUpdater.IsSupported;

    public string CurrentVersionText => "v" + AppUpdateInfo.DisplayVersion;

    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private bool _hasUpdate;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private bool _canCancelUpdate;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _updateStatusText = string.Empty;

    [RelayCommand]
    private Task CheckUpdatesAsync() => CheckForUpdatesAsync(silent: false);

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (IsCheckingUpdate || IsUpdating) { return; }
        IsCheckingUpdate = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var release = await _updates.CheckAsync(AppUpdateInfo.Version, timeout.Token);
            _availableUpdate = release;
            HasUpdate = release is not null;
            UpdateStatusText = release is null ? string.Empty : "发现新版本 v" + release.Version.ToString(3);
            if (!silent)
            {
                if (release is null)
                {
                    await _dialogs.ShowInfoAsync("检查更新", "当前版本 v" + AppUpdateInfo.DisplayVersion + "，暂无更新。");
                }
                else { await InstallUpdateAsync(); }
            }
        }
        catch (Exception ex)
        {
            _services.Log.Warn("检查更新失败（" + ex.GetType().Name + "），稍后可手动重试。");
            if (!silent)
            {
                await _dialogs.ShowWarningAsync("检查更新失败", "暂时无法读取 GitHub 发布信息，请检查网络后重试。");
            }
        }
        finally { IsCheckingUpdate = false; }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        var release = _availableUpdate;
        if (release is null || IsUpdating) { return; }
        if (IsBusy || IsQuotaBusy)
        {
            await _dialogs.ShowInfoAsync("升级", "请等待当前操作完成后再升级。");
            return;
        }
        if (!AutoUpdateSupported)
        {
            var guidance = OperatingSystem.IsLinux()
                ? "自动升级支持通过 .deb 安装的版本，以及当前用户拥有且可写的 Linux x64 单文件便携版。请从以下页面下载并手动升级：\n"
                : "自动升级支持 Windows x64 单文件发布版。请从以下页面下载：\n";
            await _dialogs.ShowInfoAsync("升级", guidance + AppUpdateInfo.ReleaseUrl);
            return;
        }

        IsBusy = true;
        var handedOver = false;
        string? downloadDirectory = null;
        string? downloadedFile = null;
        LinuxUpdateSwap.FileProof? downloadedFileProof = null;
        LinuxUpdateSwap.DirectoryProof? downloadDirectoryProof = null;
        LinuxUpdateSwap.DirectoryProof? downloadedStagingDirectoryProof = null;
        try
        {
            var notes = release.Notes.Length > 2000 ? release.Notes[..2000] + "…" : release.Notes;
            if (!await _dialogs.ConfirmAsync("升级到 v" + release.Version.ToString(3),
                    "当前版本 v" + AppUpdateInfo.DisplayVersion + "\n\n" + notes
                    + "\n\n确认后自动下载，校验完成后重启本工具。账号和设置会保留。")) { return; }

            IsUpdating = true;
            CanCancelUpdate = true;
            UpdateProgress = 0;
            UpdateStatusText = "准备下载…";
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            _updateCancellation = cancellation;
            downloadDirectory = Path.Combine(Path.GetTempPath(), "OpenCodeAccountManager-update-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(downloadDirectory) || File.Exists(downloadDirectory))
            {
                throw new IOException("The private update staging path already exists.");
            }
            Directory.CreateDirectory(downloadDirectory);
            downloadDirectoryProof = LinuxUpdateSwap.CaptureDirectory(downloadDirectory);
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                if (!CanCancelUpdate || _updateCancellation != cancellation) { return; }
                UpdateProgress = p.Percent;
                UpdateStatusText = $"下载 {p.Percent:0}% · {p.BytesReceived / 1048576d:0.0} / {p.TotalBytes / 1048576d:0.0} MB";
            });
            var file = await _updates.DownloadAsync(release, downloadDirectory, progress, cancellation.Token);
            downloadedFile = file;
            try { downloadedFileProof = LinuxUpdateSwap.Capture(file); } catch { }
            try { downloadedStagingDirectoryProof = LinuxUpdateSwap.CaptureDirectory(Path.GetDirectoryName(file)!); } catch { }
            CanCancelUpdate = false;
            UpdateProgress = 100;
            UpdateStatusText = "校验完成，正在重启到新版…";
            PersistSettings();
            // The download deadline must not cancel an already-verified installation. The
            // hand-over has its own bounded startup handshake inside SelfUpdater.
            if (release.Platform == UpdatePlatform.LinuxDeb)
            {
                // The detached script owns the package from here on and removes it after installing.
                DebInstallUpdater.Launch(file, Environment.ProcessId);
                handedOver = true;
            }
            else
            {
                await SelfUpdater.PrepareAndLaunchAsync(file, release.Version, CancellationToken.None);
            }
            if (Application.Current is App app) { app.ExitApp(); }
        }
        catch (OperationCanceledException)
        {
            CanCancelUpdate = false;
            UpdateStatusText = "下载已取消或超时，可点击升级重试。";
        }
        catch (Exception ex)
        {
            CanCancelUpdate = false;
            UpdateStatusText = "升级失败，可重试。";
            _services.Log.Warn("升级失败（" + ex.GetType().Name + "）。");
            await _dialogs.ShowWarningAsync("升级失败", _services.Secrets.Redact(ex.Message));
        }
        finally
        {
            _updateCancellation = null;
            CanCancelUpdate = false;
            IsUpdating = false;
            IsBusy = false;
            if (downloadDirectory is not null && !handedOver)
            {
                CleanupDownloadedArtifact(downloadDirectory, downloadedFile, downloadedFileProof,
                    downloadDirectoryProof, downloadedStagingDirectoryProof);
            }
        }
    }

    internal static void CleanupDownloadedArtifact(
        string root,
        string? file,
        LinuxUpdateSwap.FileProof? proof,
        LinuxUpdateSwap.DirectoryProof? rootProof = null,
        LinuxUpdateSwap.DirectoryProof? artifactDirectoryProof = null)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            string? artifactDirectory = null;
            if (!string.IsNullOrWhiteSpace(file))
            {
                var fullFile = Path.GetFullPath(file);
                var relative = Path.GetRelativePath(fullRoot, fullFile);
                if (relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !Path.IsPathRooted(relative))
                {
                    artifactDirectory = Path.GetDirectoryName(fullFile);
                    if (proof is not null && LinuxUpdateSwap.Matches(proof, fullFile))
                    {
                        try { File.Delete(fullFile); } catch { }
                    }
                }
            }

            if (!string.IsNullOrEmpty(artifactDirectory) && artifactDirectoryProof is not null &&
                !string.Equals(artifactDirectory, fullRoot, PathComparison))
            {
                LinuxUpdateSwap.TryDeleteOwnedEmptyDirectory(artifactDirectory, artifactDirectoryProof);
            }
            LinuxUpdateSwap.TryDeleteOwnedEmptyDirectory(fullRoot, rootProof);
        }
        catch { /* Unknown concurrent entries are deliberately left in place. */ }
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [RelayCommand]
    private void CancelUpdate() => _updateCancellation?.Cancel();
}
