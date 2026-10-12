using System.Globalization;
using AccountManager.App.ViewModels;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AccountManager.App.Converters;

/// <summary>Maps a quota level to the progress-bar fill brush.</summary>
public sealed class QuotaLevelToBrushConverter : IValueConverter
{
    public static readonly QuotaLevelToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is QuotaLevel l ? l : QuotaLevel.None;
        return level switch
        {
            QuotaLevel.Ok => new SolidColorBrush(Color.Parse("#2FA36B")),
            QuotaLevel.Warn => new SolidColorBrush(Color.Parse("#E0902B")),
            QuotaLevel.Critical => new SolidColorBrush(Color.Parse("#D64545")),
            _ => new SolidColorBrush(Color.Parse("#C3C7D1")),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
