using System.Windows.Input;
using AccountManager.Core.Accounts;
using AccountManager.Core.Models;
using AccountManager.Core.Quota;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

public enum QuotaLevel
{
    None,
    Ok,
    Warn,
    Critical,
}

/// <summary>Rendering data for one quota window (remaining semantics), mirrors Ui.ps1.</summary>
public sealed class QuotaCellViewModel
{
    public bool HasBar { get; init; }

    public double Percent { get; init; }

    public string Text { get; init; } = "无数据";

    public QuotaLevel Level { get; init; } = QuotaLevel.None;

    public string Tooltip { get; init; } = string.Empty;

    /// <summary>Short reset line shown under the bar, e.g. "1h8m · 10-02 01:57".</summary>
    public string ResetText { get; init; } = string.Empty;

    public static QuotaCellViewModel Note(string text, string? tooltip = null) => new()
    {
        HasBar = false,
        Text = string.IsNullOrWhiteSpace(text) ? "无数据" : text,
        Tooltip = string.IsNullOrWhiteSpace(tooltip) ? text : tooltip!,
        Level = QuotaLevel.None,
    };

    public static QuotaCellViewModel FromResult(AccountQuotaResult? result, bool primary)
    {
        if (result is null) { return Note("无数据"); }

        var state = primary ? result.Usage?.PrimaryState : result.Usage?.SecondaryState;
        var used = primary ? result.Usage?.PrimaryPercent : result.Usage?.SecondaryPercent;
        var reset = primary ? result.Usage?.PrimaryReset : result.Usage?.SecondaryReset;

        var remaining = used is null ? null : (double?)Math.Clamp(100 - used.Value, 0, 100);
        if (remaining is null)
        {
            var text = !string.IsNullOrWhiteSpace(state) ? StateText(state) : StateText(result.State.ToString());
            if (string.IsNullOrWhiteSpace(text)) { text = "无数据"; }
            var tip = text;
            if (!string.IsNullOrWhiteSpace(result.Message)) { tip += Environment.NewLine + result.Message; }
            if (result.Usage?.ResetCreditsAvailable is { } creditsNote) { tip += Environment.NewLine + "重置次数：" + creditsNote; }
            tip += Environment.NewLine + "上次查询：" + result.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return Note(text, tip);
        }

        var resetAbsolute = reset?.ToLocalTime().ToString("MM-dd HH:mm") ?? string.Empty;
        var countdown = QuotaService.FormatDuration(reset);
        var resetText = string.IsNullOrWhiteSpace(countdown)
            ? (string.IsNullOrWhiteSpace(resetAbsolute) ? "重置未知" : resetAbsolute)
            : (string.IsNullOrWhiteSpace(resetAbsolute) ? countdown : countdown + " · " + resetAbsolute);
        var usedRound = (int)Math.Round(Math.Clamp(used!.Value, 0, 100));
        var remainRound = (int)Math.Round(remaining.Value);
        var parts = new List<string> { "已用 " + usedRound + "%", "剩余 " + remainRound + "%" };
        if (!string.IsNullOrWhiteSpace(resetAbsolute))
        {
            var line = "重置 " + resetAbsolute;
            if (!string.IsNullOrWhiteSpace(countdown)) { line += "（" + countdown + "）"; }
            parts.Add(line);
        }
        else { parts.Add("重置未知"); }

        if (result.Usage?.ResetCreditsAvailable is { } credits) { parts.Add("重置次数 " + credits); }

        parts.Add("查询 " + result.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        return new QuotaCellViewModel
        {
            HasBar = true,
            Percent = remaining.Value,
            Text = remainRound + "%",
            Level = LevelForRemaining(remaining.Value),
            Tooltip = string.Join(" · ", parts),
            ResetText = resetText,
        };
    }

    public static QuotaLevel LevelForRemaining(double remaining)
    {
        if (remaining > 60) { return QuotaLevel.Ok; }
        if (remaining > 15) { return QuotaLevel.Warn; }
        return QuotaLevel.Critical;
    }

    public static string StateText(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) { return string.Empty; }
        return state.ToLowerInvariant() switch
        {
            "ok" or "available" => "正常",
            "expired" => "已过期",
            "api" => "API 账号",
            "unsupported" => "不支持",
            "missingtoken" => "缺少令牌",
            "nodata" => "无窗口数据",
            "partial" => "部分窗口",
            "success" => "已更新额度",
            "error" or "failed" => "查询失败",
            "missing" or "missingwindow" => "无此窗口",
            "invalid" => "数据无法识别",
            "unlimited" => "不限制",
            "none" => "无数据",
            "unknown" => "未知",
            _ => state,
        };
    }
}

/// <summary>Row-level callbacks implemented by the main view model.</summary>
public interface IRowActions
{
    Task SwitchAsync(AccountRowViewModel row);

    Task RefreshAsync(AccountRowViewModel row);

    Task ResetAsync(AccountRowViewModel row);

    Task DeleteAsync(AccountRowViewModel row);

    Task ConfigureAsync(AccountRowViewModel row);
}

/// <summary>One account row bound to the list.</summary>
public sealed partial class AccountRowViewModel : ObservableObject
{
    private readonly IRowActions _actions;

    public AccountRowViewModel(AccountRow row, IRowActions actions)
    {
        Row = row;
        _actions = actions;
        Quota5h = QuotaCellViewModel.Note("未查询");
        QuotaWk = QuotaCellViewModel.Note("未查询");
        StateText = AccountStateText(row.StatusCode);

        var palette = PickPalette(row);
        PlanBackground = new SolidColorBrush(palette.Bg);
        PlanForeground = new SolidColorBrush(palette.Fg);

        RefreshSubscriptionClock();

        if (row.Kind == AccountKind.Codex)
        {
            ResetCreditsText = "重置";
            ResetCreditsTooltip = "点击查询并使用一张 Codex 重置券（会先弹出到期明细并确认）。";
        }
    }

    /// <summary>Subscription info: remaining time (天/小时/分), or "—" when the source has no expiry.</summary>
    [ObservableProperty]
    private string _subscriptionDaysText = string.Empty;

    [ObservableProperty]
    private string _subscriptionTooltip = string.Empty;

    /// <summary>
    /// Recomputes the subscription countdown for the current time. Called when the row is built and
    /// on every quota refresh so the hours/minutes tiers stay live.
    /// </summary>
    public void RefreshSubscriptionClock()
    {
        if (Row.Kind == AccountKind.Api)
        {
            SubscriptionDaysText = string.Empty;
            SubscriptionTooltip = string.Empty;
            return;
        }

        SubscriptionDaysText = SubscriptionClock.FormatRemaining(Row.SubscriptionExpires, DateTimeOffset.UtcNow);

        if (Row.SubscriptionExpires is not { } until)
        {
            SubscriptionTooltip = "订阅到期：未知（未能获取到该账号的订阅信息）";
            return;
        }

        var local = until.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        SubscriptionTooltip = SubscriptionDaysText == "已到期"
            ? "订阅到期：" + local + "（已到期）"
            : "订阅到期：" + local + "（剩余 " + SubscriptionDaysText + "）";
    }

    public AccountRow Row { get; }

    public string Key => Row.Key;

    public string IdentityKey => string.IsNullOrWhiteSpace(Row.Provider) || string.IsNullOrWhiteSpace(Row.Identity)
        ? string.Empty
        : Row.Provider + "|" + Row.Identity;

    public bool IsCodex => Row.Kind == AccountKind.Codex;

    public bool IsApi => Row.Kind == AccountKind.Api;

    public bool IsCurrent => IsCodex && Row.Active;

    public string Title => string.IsNullOrWhiteSpace(Row.Label)
        ? (string.IsNullOrWhiteSpace(Row.Identity) ? Row.Key : Row.Identity)
        : Row.Label;

    /// <summary>Second line: nothing for Codex (workspace id is not shown), safe provider id for API.</summary>
    public string Subtitle => IsCodex ? string.Empty : Row.Provider;

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    public string ProviderDetail
    {
        get
        {
            if (IsCodex) { return Row.CredentialId; }
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Row.BaseUrl)) { parts.Add(Row.BaseUrl); }
            if (Row.Models.Count > 0) { parts.Add(Row.Models.Count + " 个模型"); }
            return parts.Count == 0 ? "—" : string.Join(" · ", parts);
        }
    }

    public string PlanText => string.IsNullOrWhiteSpace(Row.Plan) ? "—" : Row.Plan;

    /// <summary>Team-plan badge colour: same ChatGPT account/workspace id → same colour.</summary>
    public IBrush PlanBackground { get; }

    public IBrush PlanForeground { get; }

    // 8 dark-friendly (background, foreground) chip pairs; index is a stable hash of the team id.
    private static readonly (Color Bg, Color Fg)[] TeamPalette =
    {
        (Color.Parse("#2A2450"), Color.Parse("#B9A6FF")),
        (Color.Parse("#16304A"), Color.Parse("#6FB6FF")),
        (Color.Parse("#12383A"), Color.Parse("#5FD6C8")),
        (Color.Parse("#143A26"), Color.Parse("#5FD08A")),
        (Color.Parse("#3A2E12"), Color.Parse("#E7B65C")),
        (Color.Parse("#3A1E2A"), Color.Parse("#F08BA6")),
        (Color.Parse("#202A56"), Color.Parse("#8FA0FF")),
        (Color.Parse("#28380F"), Color.Parse("#A9D65C")),
    };

    private static readonly (Color Bg, Color Fg) ApiPalette = (Color.Parse("#252B47"), Color.Parse("#9AA6FF"));

    private static (Color Bg, Color Fg) PickPalette(AccountRow row)
        => row.Kind == AccountKind.Api ? ApiPalette : TeamPalette[StableIndex(TeamKeyOf(row)) % TeamPalette.Length];

    /// <summary>The workspace identity used for team colour: account id, else workspace, else key.</summary>
    private static string TeamKeyOf(AccountRow row)
    {
        var key = row.AccountId;
        if (string.IsNullOrWhiteSpace(key)) { key = row.Workspace; }
        if (string.IsNullOrWhiteSpace(key)) { key = row.Key; }
        return key ?? string.Empty;
    }

    /// <summary>Workspace/team identity, used to prefer a switch inside the same organisation.</summary>
    public string TeamKey => TeamKeyOf(Row);

    private static int StableIndex(string key)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in key) { hash ^= ch; hash *= 16777619; }
            return (int)(hash & 0x7fffffff);
        }
    }

    public string AccountTooltip => Row.Kind == AccountKind.Codex
        ? "凭据 " + Row.CredentialId
        : "供应商 " + Row.Provider + Environment.NewLine + ProviderDetail;

    [ObservableProperty]
    private bool _isQuotaSelected;

    [ObservableProperty]
    private bool _isImportSelected;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private QuotaCellViewModel _quota5h;

    [ObservableProperty]
    private QuotaCellViewModel _quotaWk;

    [ObservableProperty]
    private string _stateText = string.Empty;

    /// <summary>Live "reset credits" badge (from the usage API); hidden when unknown/API rows.</summary>
    [ObservableProperty]
    private bool _hasResetCredits;

    [ObservableProperty]
    private string _resetCreditsText = string.Empty;

    [ObservableProperty]
    private string _resetCreditsTooltip = string.Empty;

    public void SetState(string text) => StateText = text;

    /// <summary>Friendly label for an account's own status code (not the quota state).</summary>
    public static string AccountStateText(string? code) => code switch
    {
        "Ready" => "就绪",
        "Expired" => "已过期",
        "Missing" => "缺少令牌",
        "Stored" => "已保存",
        "ConfiguredOnly" => "仅配置",
        "Import" => "待导入",
        _ => string.IsNullOrWhiteSpace(code) ? "就绪" : code,
    };

    [RelayCommand]
    private Task Switch() => IsCodex ? _actions.SwitchAsync(this) : Task.CompletedTask;

    [RelayCommand]
    private Task Refresh() => IsCodex ? _actions.RefreshAsync(this) : Task.CompletedTask;

    [RelayCommand]
    private Task Reset() => IsCodex ? _actions.ResetAsync(this) : Task.CompletedTask;

    [RelayCommand]
    private Task Delete() => _actions.DeleteAsync(this);

    [RelayCommand]
    private Task Configure() => IsApi ? _actions.ConfigureAsync(this) : Task.CompletedTask;
}
