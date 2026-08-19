using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickPhrase.Desktop.Converters;

/// <summary>
/// enum → 显示字符串（V1 暂直接 ToString；后续可接本地化资源）。
/// </summary>
public sealed class EnumToLocalizedStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
