using AccountManager.App.Services;
using AccountManager.App.Views;
using AccountManager.App.ViewModels;
using AccountManager.Core.Accounts;
using AccountManager.Core.Actions;
using AccountManager.Core.Models;
using AccountManager.Core.Security;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AccountManager.App.Tests.TestAppBuilder))]

namespace AccountManager.App.Tests;

public static class TestAppBuilder
{
    // Headless setup has no desktop lifetime: App never creates services, reads credentials,
    // starts auto-refresh, or launches the user's real main window.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<AccountManager.App.App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont();
}

public class DialogTests
{
    private const string FakeAuthorizationUrl =
        "https://auth.example.test/oauth/authorize?state=fake-state&code_challenge=fake-challenge";

    [AvaloniaFact]
    public void AddAccountWindowContainsExactlyTheTwoRequestedTabs()
    {
        var dialog = new AddAccountDialog();
        dialog.Show();
        try
        {
            var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Equal(new[] { "OpenAI OAuth", "添加 API" }, tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()));
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "回调地址（没有自动回调时使用）");
            tabs.SelectedIndex = 1;
            dialog.UpdateLayout();
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "供应商名称");
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "API Key");
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public async Task ConfigOnlyEditorAllowsNameAndFirstKeyAndParsesCommaSeparatedModels()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = ApiConfigDialog.ShowAsync(owner, new ApiConfigInput
            {
                ProviderId = "stable-id", DisplayName = "Original", Title = "Original",
                BaseUrl = "https://example.test", Models = new[] { "m" }, HasCredential = false,
            });
            var dialog = owner.OwnedWindows.OfType<ApiConfigDialog>().Single();
            Assert.True(dialog.FindControl<TextBox>("KeyBox")!.IsEnabled);
            Assert.Equal("Original", dialog.FindControl<TextBox>("NameBox")!.Text);
            dialog.FindControl<TextBox>("NameBox")!.Text = "Renamed";
            dialog.FindControl<TextBox>("ModelsBox")!.Text = "a,b\nc,a";
            dialog.FindControl<TextBox>("KeyBox")!.Text = "fake-key";
            Click(dialog, "保存");
            var result = await pending;
            Assert.NotNull(result);
            Assert.Equal("Renamed", result.DisplayName);
            Assert.Equal(new[] { "a", "b", "c" }, result.Models);
            Assert.Equal("fake-key", result.NewKey);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task InvalidApiInputDoesNotCloseEditorOrLoseForm()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = ApiConfigDialog.ShowAsync(owner, new ApiConfigInput
            {
                ProviderId = "id", DisplayName = "Name", BaseUrl = "https://example.test", Models = new[] { "m" }, HasCredential = true,
            });
            var dialog = owner.OwnedWindows.OfType<ApiConfigDialog>().Single();
            dialog.FindControl<TextBox>("BaseUrlBox")!.Text = "http://example.test";
            Click(dialog, "保存");
            Assert.False(pending.IsCompleted);
            Assert.Contains("HTTPS", dialog.FindControl<TextBlock>("ErrorText")!.Text);
            Assert.Equal("http://example.test", dialog.FindControl<TextBox>("BaseUrlBox")!.Text);
            Click(dialog, "取消");
            Assert.Null(await pending);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ApiEditorDisplaysAndPreservesBothExistingProtocolsWithBlankKey()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            foreach (var useResponses in new[] { false, true })
            {
                var pending = ApiConfigDialog.ShowAsync(owner, new ApiConfigInput
                {
                    ProviderId = "stable-id", DisplayName = "示例供应商", Title = "示例供应商",
                    BaseUrl = "https://example.test/v1", Models = new[] { "model-a", "model-b" },
                    HasCredential = true, UseResponses = useResponses,
                });
                var dialog = owner.OwnedWindows.OfType<ApiConfigDialog>().Single();
                Assert.Equal(useResponses, dialog.FindControl<RadioButton>("ResponsesRadio")!.IsChecked);
                Assert.Equal(!useResponses, dialog.FindControl<RadioButton>("ChatRadio")!.IsChecked);
                Assert.True(string.IsNullOrEmpty(dialog.FindControl<TextBox>("KeyBox")!.Text));
                dialog.UpdateLayout();
                CapturePreview(dialog, useResponses ? "api-config-responses.png" : "api-config-chat.png");
                Click(dialog, "保存");
                var result = await pending;
                Assert.NotNull(result);
                Assert.Equal(useResponses, result.UseResponses);
                Assert.True(string.IsNullOrEmpty(result.NewKey));
                Assert.Equal("示例供应商", result.DisplayName);
                Assert.Equal("https://example.test/v1", result.BaseUrl);
                Assert.Equal(new[] { "model-a", "model-b" }, result.Models);
            }
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ApiEditorCanSwitchProtocolInEitherDirectionAndCancelDiscardsSelection()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            foreach (var useResponses in new[] { false, true })
            {
                var input = new ApiConfigInput
                {
                    ProviderId = "stable-id", DisplayName = "Name", BaseUrl = "https://example.test", Models = new[] { "m" },
                    HasCredential = true, UseResponses = useResponses,
                };
                var pending = ApiConfigDialog.ShowAsync(owner, input);
                var dialog = owner.OwnedWindows.OfType<ApiConfigDialog>().Single();
                dialog.FindControl<RadioButton>("ResponsesRadio")!.IsChecked = !useResponses;
                Click(dialog, "保存");
                var result = await pending;
                Assert.NotNull(result);
                Assert.Equal(!useResponses, result.UseResponses);
                Assert.True(string.IsNullOrEmpty(result.NewKey));

                pending = ApiConfigDialog.ShowAsync(owner, input);
                dialog = owner.OwnedWindows.OfType<ApiConfigDialog>().Single();
                dialog.FindControl<RadioButton>("ResponsesRadio")!.IsChecked = !useResponses;
                Click(dialog, "取消");
                Assert.Null(await pending);
            }
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task AddApiViewModelValidatesAndPreventsDoubleSubmitAndUsesStableId()
    {
        var services = new FakeAddServices();
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null));
        vm.SelectedTabIndex = 1;
        await vm.SaveApiCommand.ExecuteAsync(null);
        Assert.Contains("供应商名称", vm.ErrorText);
        Assert.Equal(0, services.Writes);
        vm.DisplayName = "Test API";
        vm.BaseUrl = "https://example.test/v1";
        vm.ModelsText = "a,b\na";
        vm.ApiKey = "fake-secret";
        vm.UseResponses = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.WaitBeforeSave = release.Task;
        var first = vm.SaveApiCommand.ExecuteAsync(null);
        Assert.True(vm.IsSaving);
        Assert.False(vm.CanOperate);
        await vm.SaveApiCommand.ExecuteAsync(null);
        Assert.Equal(1, services.Writes);
        release.SetResult();
        await first;
        Assert.NotNull(vm.Result);
        Assert.StartsWith("api-", vm.Result.ProviderID);
        Assert.True(services.UseResponses);
        Assert.Equal(new[] { "a", "b" }, services.Models);
        Assert.Equal("[REDACTED]", services.Secrets.Redact("fake-secret"));
    }

    [AvaloniaFact]
    public async Task RenderAddAccountBothTabsWithoutAnyRealAccountsOrBrowser()
    {
        await using var vm = new AddAccountViewModel(new FakeAddServices(), new DialogService(() => null), _ => throw new InvalidOperationException("No browser in tests"))
        {
            DisplayName = "示例供应商", BaseUrl = "https://api.example.com/v1", ModelsText = "model-a\nmodel-b", ApiKey = "fake-preview-key",
        };
        var dialog = new AddAccountDialog { DataContext = vm };
        dialog.Show();
        try
        {
            dialog.UpdateLayout();
            CapturePreview(dialog, "add-account-oauth.png");
            vm.SelectedTabIndex = 1;
            dialog.UpdateLayout();
            CapturePreview(dialog, "add-account-api.png");
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("fake-preview-key") == true);
        }
        finally { dialog.Close(); }
    }

    // ── Task 5: the browser opener is awaitable, keeps the pending OAuth session, and never
    //    leaks the authorization URL. These use a synthetic session seam and never touch a real
    //    loopback listener, token endpoint, or browser. ──────────────────────────────────────

    [AvaloniaFact]
    public async Task FailedBrowserOpenKeepsThePendingAuthorizationAndNeverLeaksTheUrl()
    {
        var session = new FakeBrowserOAuthSession(FakeAuthorizationUrl);
        var services = new FakeAddServices { OAuthAvailable = true, PendingSession = session };
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null),
            openAsync: (_, _) => throw new InvalidOperationException("xdg-open failed: " + FakeAuthorizationUrl));

        await vm.OpenBrowserCommand.ExecuteAsync(null);

        Assert.Equal(FakeAuthorizationUrl, vm.AuthorizationUrl);
        Assert.True(vm.WaitingForAuthorization);
        Assert.False(session.Disposed);
        Assert.DoesNotContain(FakeAuthorizationUrl, vm.ErrorText);
        Assert.DoesNotContain("state=fake-state", vm.ErrorText);
        Assert.DoesNotContain("xdg-open failed", vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task UserCancellingTheBrowserOpenDoesNotClearTheAuthorizationSession()
    {
        var session = new FakeBrowserOAuthSession(FakeAuthorizationUrl);
        var services = new FakeAddServices { OAuthAvailable = true, PendingSession = session };
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null),
            openAsync: (_, _) => throw new OperationCanceledException());

        await vm.OpenBrowserCommand.ExecuteAsync(null);

        Assert.Equal(FakeAuthorizationUrl, vm.AuthorizationUrl);
        Assert.True(vm.WaitingForAuthorization);
        Assert.False(session.Disposed);
    }

    [AvaloniaFact]
    public async Task TheExistingOpenBrowserActionIsStillInvokedThroughTheAsyncWrapper()
    {
        string? opened = null;
        var session = new FakeBrowserOAuthSession(FakeAuthorizationUrl);
        var services = new FakeAddServices { OAuthAvailable = true, PendingSession = session };
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null),
            openBrowser: url => opened = url);

        await vm.OpenBrowserCommand.ExecuteAsync(null);

        Assert.Equal(FakeAuthorizationUrl, opened);
        Assert.Equal(FakeAuthorizationUrl, vm.AuthorizationUrl);
        Assert.False(session.Disposed);
    }

    [AvaloniaFact]
    public async Task OpenBrowserAwaitsTheInjectedAsyncOpener()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeBrowserOAuthSession(FakeAuthorizationUrl);
        var services = new FakeAddServices { OAuthAvailable = true, PendingSession = session };
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null),
            openAsync: (_, _) => gate.Task);

        var run = vm.OpenBrowserCommand.ExecuteAsync(null);

        Assert.False(run.IsCompleted);
        gate.SetResult();
        await run;
        Assert.Equal(FakeAuthorizationUrl, vm.AuthorizationUrl);
    }

    [AvaloniaFact]
    public async Task FailedManualCallbackKeepsThePendingAuthorization()
    {
        var session = new FakeBrowserOAuthSession(FakeAuthorizationUrl);
        var services = new FakeAddServices { OAuthAvailable = true, PendingSession = session };
        await using var vm = new AddAccountViewModel(services, new DialogService(() => null));

        await vm.PrimeAuthorizationAsync();
        vm.CallbackUrl = "http://localhost:1455/auth/callback?code=fake&state=fake-state";
        await vm.SubmitCallbackCommand.ExecuteAsync(null);

        Assert.Equal(FakeAuthorizationUrl, vm.AuthorizationUrl);
        Assert.False(session.Disposed);
    }

    [AvaloniaFact]
    public async Task SettingsDialogExposesWebDavSyncFieldsAndReturnsThem()
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = SettingsDialog.ShowAsync(owner, new SettingsInput
            {
                CliPath = string.Empty,
                AutoRefreshMinutes = 5,
                AutoSwitchAccount = false,
                WebDavUrl = "https://dav.example.com/dav/",
                WebDavUsername = "alice@example.com",
                WebDavPasswordStored = true,
            });
            var dialog = owner.OwnedWindows.OfType<SettingsDialog>().Single();
            dialog.UpdateLayout();
            Assert.Equal("https://dav.example.com/dav/", dialog.FindControl<TextBox>("WebDavUrlBox")!.Text);
            Assert.Equal("alice@example.com", dialog.FindControl<TextBox>("WebDavUserBox")!.Text);
            Assert.Null(dialog.FindControl<TextBox>("WebDavPathBox"));
            Assert.True(string.IsNullOrEmpty(dialog.FindControl<TextBox>("WebDavPassBox")!.Text));
            // Sync actions live inside the settings dialog now.
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "上传到云端");
            Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "从云端下载");

            dialog.FindControl<TextBox>("WebDavPassBox")!.Text = "app-pass";
            Click(dialog, "保存");
            var result = await pending;
            Assert.NotNull(result);
            Assert.Equal("https://dav.example.com/dav/", result!.WebDavUrl);
            Assert.Equal("alice@example.com", result.WebDavUsername);
            Assert.Equal("app-pass", result.WebDavPassword);
            Assert.Equal(SettingsAction.None, result.Action);
        }
        finally { owner.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("上传到云端", SettingsAction.Upload)]
    [InlineData("从云端下载", SettingsAction.Download)]
    public async Task SettingsDialogSyncButtons_ReturnTheChosenAction(string button, SettingsAction expected)
    {
        var owner = new Window();
        owner.Show();
        try
        {
            var pending = SettingsDialog.ShowAsync(owner, new SettingsInput
            {
                WebDavUrl = "https://dav.example.com/dav/",
                WebDavUsername = "alice@example.com",
                WebDavPasswordStored = true,
            });
            var dialog = owner.OwnedWindows.OfType<SettingsDialog>().Single();
            dialog.UpdateLayout();

            Click(dialog, button);
            var result = await pending;
            Assert.NotNull(result);
            Assert.Equal(expected, result!.Action);
        }
        finally { owner.Close(); }
    }

    private static void CapturePreview(Window dialog, string name)
    {
        var directory = Environment.GetEnvironmentVariable("ACCOUNT_MANAGER_UI_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(directory)) { return; }
        Directory.CreateDirectory(directory);
        using var bitmap = dialog.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(directory, name));
    }

    private sealed class FakeAddServices : IAddAccountServices
    {
        public SecretRegistry Secrets { get; } = new(false);
        public int Writes;
        public bool UseResponses;
        public IReadOnlyList<string> Models = Array.Empty<string>();
        public Task? WaitBeforeSave;

        /// <summary>Set true to let the synthetic OAuth session be created in Task 5 tests.</summary>
        public bool OAuthAvailable;

        /// <summary>Synthetic in-flight session for the Task 5 seam; never a real loopback listener.</summary>
        public IBrowserOAuthSession? PendingSession;

        public Task EnsureBrowserOAuthAsync()
            => OAuthAvailable ? Task.CompletedTask : throw new InvalidOperationException("No real OAuth in UI tests");

        /// <summary>Retained production API; the fake must never start a real OAuth service.</summary>
        public CodexOAuthService CreateOAuthService()
            => throw new InvalidOperationException("No real OAuth in UI tests");

        public IBrowserOAuthSession StartBrowserOAuthSession()
            => PendingSession ?? throw new InvalidOperationException("No real OAuth in UI tests");

        public Task<CodexAddPreview> PreviewCodexAuthorizationAsync(JsonObject account) => throw new InvalidOperationException();
        public Task<AccountAddResult> SaveCodexAuthorizationAsync(CodexAddPreview preview, bool replaceExisting) => throw new InvalidOperationException();
        public async Task<AccountAddResult> CreateApiProviderAsync(string id, string name, string url, IReadOnlyList<string> models, string key, bool useResponses)
        {
            Writes++;
            Models = models;
            UseResponses = useResponses;
            if (WaitBeforeSave is not null) { await WaitBeforeSave; }
            return new AccountAddResult { Saved = true, ProviderID = id, Label = name };
        }
    }

    /// <summary>
    /// Minimal synthetic pending authorization. The callback always fails and the completion never
    /// resolves, so the view model keeps the session alive without any real network or listener.
    /// </summary>
    private sealed class FakeBrowserOAuthSession : IBrowserOAuthSession
    {
        private readonly TaskCompletionSource<JsonObject> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeBrowserOAuthSession(string authorizationUrl) => AuthorizationUrl = authorizationUrl;

        public string AuthorizationUrl { get; }

        public bool Disposed { get; private set; }

        public Task<JsonObject> Completion => _completion.Task;

        public Task SubmitCallbackAsync(string callbackUrl)
            => Task.FromException(new InvalidOperationException("callback rejected in tests"));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _completion.TrySetException(new OperationCanceledException());
            return ValueTask.CompletedTask;
        }
    }

    private static void Click(Window dialog, string content)
        => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == content)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
