using AccountManager.App.Services;
using AccountManager.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AccountManager.App.Tests;

public class SettingsLayoutTests
{
    [AvaloniaFact]
    public void MainWindowHasNoSystemProxySwitchOrProxyStatus()
    {
        var window = new MainWindow { Width = 1000, Height = 560 };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.Null(window.FindControl<ToggleButton>("ProxySwitch"));
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
            Assert.DoesNotContain(texts, text => text.Contains("系统代理"));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "重新应用");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SettingsCanBeResizedInsteadOfGrowingOffScreen()
    {
        var dialog = new SettingsDialog();
        Assert.True(dialog.CanResize);
        Assert.Equal(SizeToContent.Manual, dialog.SizeToContent);
        Assert.Equal(SystemDecorations.Full, dialog.SystemDecorations);
    }

    [AvaloniaTheory]
    [InlineData(480, 320)]
    [InlineData(560, 440)]
    public void SmallSettingsWindowKeepsSaveAndCancelVisibleWhileContentScrolls(double width, double height)
    {
        var dialog = new SettingsDialog { Width = width, Height = height, SizeToContent = SizeToContent.Manual };
        dialog.Show();
        try
        {
            dialog.UpdateLayout();
            var scroll = Assert.Single(dialog.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Content is StackPanel);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            foreach (var text in new[] { "保存", "取消" })
            {
                var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == text);
                Assert.DoesNotContain(scroll, button.GetVisualAncestors());
                var position = button.TranslatePoint(default, dialog);
                Assert.NotNull(position);
                Assert.InRange(position!.Value.Y, 0, dialog.ClientSize.Height - button.Bounds.Height);
            }
            scroll.Offset = new Vector(0, double.MaxValue);
            dialog.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void EscapeCancelsWithoutSavingChanges()
    {
        var dialog = new SettingsDialog();
        dialog.Show();
        dialog.FindControl<TextBox>("CliBox")!.Text = "unsaved-path";
        dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        try
        {
            Assert.False(dialog.IsVisible);
            Assert.Null(dialog.Result);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void DetailedHelpIsAvailableAsTooltipInsteadOfFillingTheForm()
    {
        var dialog = new SettingsDialog();
        dialog.Show();
        try
        {
            var visibleText = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty);
            Assert.DoesNotContain(visibleText, text => text.StartsWith("提示：桌面版"));
            Assert.DoesNotContain(visibleText, text => text.Contains("坚果云等需"));
            Assert.NotNull(ToolTip.GetTip(dialog.FindControl<TextBox>("CliBox")!));
            Assert.Null(dialog.FindControl<TextBox>("ProxyUrlBox"));
            Assert.Null(dialog.FindControl<TextBox>("DesktopPathBox"));
            Assert.NotNull(ToolTip.GetTip(dialog.FindControl<TextBox>("WebDavPassBox")!));
        }
        finally { dialog.Close(); }
    }

    private static void Click(Window dialog, string content)
        => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == content)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
