using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickPhrase.Desktop.Converters;

/// <summary>
/// 将话术颜色稳定键映射为主题资源中的 SolidColorBrush。
/// 旧版 red/yellow 仅用于读取兼容，显示时统一落到 pink/tan；未知键回退到无颜色。
/// </summary>
public sealed class ColorKeyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return System.Windows.DependencyProperty.UnsetValue;
        var key = value as string;
        if (string.IsNullOrWhiteSpace(key)) key = "default";
        key = key.Trim().ToLowerInvariant() switch
        {
            "red" => "pink",
            "yellow" => "tan",
            _ => key.Trim().ToLowerInvariant(),
        };

        var resourceKey = "PhraseColor" + char.ToUpperInvariant(key[0]) + key.Substring(1);
        if (app.Resources[resourceKey] is System.Windows.Media.Brush brush) return brush;
        if (app.Resources["PhraseColorDefault"] is System.Windows.Media.Brush fallback) return fallback;
        return System.Windows.DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
