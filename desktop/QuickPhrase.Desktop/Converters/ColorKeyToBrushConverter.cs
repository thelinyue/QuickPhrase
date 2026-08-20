using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickPhrase.Desktop.Converters;

/// <summary>
/// 将话术颜色稳定键映射为主题资源中的 SolidColorBrush。
/// 未知键回退到无颜色，避免无效数据阻断界面显示。
/// </summary>
public sealed class ColorKeyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return System.Windows.DependencyProperty.UnsetValue;
        var key = value as string;
        if (string.IsNullOrWhiteSpace(key)) key = "default";
        key = key.Trim().ToLowerInvariant();

        var resourceKey = "Brush.Phrase." + char.ToUpperInvariant(key[0]) + key.Substring(1);
        if (app.Resources[resourceKey] is System.Windows.Media.Brush brush) return brush;
        if (app.Resources["Brush.Phrase.Default"] is System.Windows.Media.Brush fallback) return fallback;
        return System.Windows.DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
