using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickPhrase.Desktop.Converters;

/// <summary>bool 取反，用于「全部」chip 的选中态（无分类筛选时选中）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag && flag ? false : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag && flag ? false : true;
}