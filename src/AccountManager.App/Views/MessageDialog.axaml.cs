using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AccountManager.App.Views;

/// <summary>Small modal message box: info / warning / confirm.</summary>
public partial class MessageDialog : Window
{
    private readonly TaskCompletionSource<bool> _result = new();

    public MessageDialog()
    {
        InitializeComponent();
    }

    public static Task<bool> ShowAsync(Window? owner, string title, string message, bool confirm, bool dangerous)
    {
        var dialog = new MessageDialog();
        dialog.Title = title;
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.YesButton.Content = confirm ? (dangerous ? "删除" : "确定") : "确定";
        dialog.NoButton.IsVisible = confirm;
        if (dangerous)
        {
            dialog.YesButton.Classes.Add("danger");
        }

        if (owner is null)
        {
            dialog.Show();
            dialog._result.SetResult(false);
            return dialog._result.Task;
        }

        dialog.ShowDialog(owner).ContinueWith(_ => dialog._result.TrySetResult(false));
        return dialog._result.Task;
    }

    private void OnYes(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(true);
        Close();
    }

    private void OnNo(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(false);
        Close();
    }
}
