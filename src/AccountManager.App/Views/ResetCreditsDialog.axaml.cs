using AccountManager.App.Services;
using AccountManager.Core.Quota;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace AccountManager.App.Views;

/// <summary>One rendered reset-credit row in the confirmation dialog.</summary>
public sealed class ResetCreditRow
{
    public string StatusText { get; init; } = string.Empty;

    public IBrush StatusBackground { get; init; } = Brushes.Transparent;

    public IBrush StatusForeground { get; init; } = Brushes.Gray;

    public string GrantedText { get; init; } = string.Empty;

    public string ExpiresText { get; init; } = string.Empty;

    public string TitleText { get; init; } = string.Empty;

    public bool HasTitle { get; init; }

    public bool IsTarget { get; init; }
}

/// <summary>
/// Shows the queried reset credits (status / granted / expiry) and only returns true when the
/// user confirms redeeming the target credit. The actual HTTP call happens in the view model.
/// </summary>
public partial class ResetCreditsDialog : Window
{
    public ResetCreditsDialog()
    {
        InitializeComponent();
    }

    public bool Confirmed { get; private set; }

    public static async Task<bool> ShowAsync(Window owner, ResetCreditsInput input)
    {
        var dialog = new ResetCreditsDialog();
        dialog.Build(input);
        await dialog.ShowDialog(owner);
        return dialog.Confirmed;
    }

    private void Build(ResetCreditsInput input)
    {
        HeaderText.Text = "重置用量" + (string.IsNullOrWhiteSpace(input.AccountTitle) ? string.Empty : "：" + input.AccountTitle);
        SummaryText.Text = "可用重置次数：" + input.List.AvailableCount + "。";

        var now = DateTimeOffset.Now;
        var rows = new List<ResetCreditRow>();
        foreach (var credit in input.List.Credits)
        {
            var isAvailable = credit.IsAvailable;
            var countdown = ResetCreditService.FormatExpiry(credit.ExpiresAt, now);
            var expiresAbsolute = credit.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
            var expiresText = credit.ExpiresAt is null
                ? "到期：未知"
                : "到期：" + (string.IsNullOrWhiteSpace(countdown) ? expiresAbsolute : countdown + "（" + expiresAbsolute + "）");

            rows.Add(new ResetCreditRow
            {
                StatusText = StatusTextOf(credit.Status),
                StatusBackground = Brush(isAvailable ? "#15301F" : "#252B47"),
                StatusForeground = Brush(isAvailable ? "#5FD08A" : "#9AA6FF"),
                GrantedText = "发放：" + (credit.GrantedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知"),
                ExpiresText = expiresText,
                TitleText = credit.Title,
                HasTitle = !string.IsNullOrWhiteSpace(credit.Title),
                IsTarget = input.Target is not null && credit.Id == input.Target.Id,
            });
        }

        CreditsList.ItemsSource = rows;

        if (input.Target is null)
        {
            TargetBanner.IsVisible = false;
            ConfirmButton.IsEnabled = false;
            HintText.Text = "当前没有可用的重置次数。";
            return;
        }

        var target = input.Target;
        var targetCountdown = ResetCreditService.FormatExpiry(target.ExpiresAt, now);
        var targetAbsolute = target.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知";
        TargetText.Text = "将使用最早到期的一张（到期 " + targetAbsolute
            + (string.IsNullOrWhiteSpace(targetCountdown) ? string.Empty : "，" + targetCountdown) + "）。";
        TargetBanner.IsVisible = true;
        ConfirmButton.IsEnabled = true;
        HintText.Text = "确认后将立即消耗这张重置券并重置用量窗口，此操作不可撤销。";
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private static string StatusTextOf(string status) => status.ToLowerInvariant() switch
    {
        "available" => "可用",
        "redeemed" or "consumed" or "used" => "已用",
        "expired" => "已过期",
        "" => "未知",
        _ => status,
    };

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
