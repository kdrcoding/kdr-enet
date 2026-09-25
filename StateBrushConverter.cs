using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace KdrEnet;

public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = (value as string) switch
        {
            "ok" => Color.FromRgb(0x22, 0xC5, 0x5E),
            "warn" => Color.FromRgb(0xEA, 0xB3, 0x08),
            "bad" => Color.FromRgb(0xEF, 0x44, 0x44),
            _ => Color.FromRgb(0x6B, 0x72, 0x80)
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class AlertBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Alert = Freeze(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush Normal = Freeze(Color.FromRgb(0xF4, 0xF7, 0xFB));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Alert : Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
