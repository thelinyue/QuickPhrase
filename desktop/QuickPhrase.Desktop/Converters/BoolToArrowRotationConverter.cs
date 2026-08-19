using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickPhrase.Desktop.Converters;

/// <summary>
/// bool(IsExpanded) → RotateTransform.Angle：true（展开）→ 0°；false（折叠）→ -90°。
/// 用于 SubHeader 的 chevron 折叠箭头（展开朝下，折叠朝右）。
/// </summary>
public sealed class BoolToArrowRotationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var expanded = value is bool flag && flag;
        return expanded ? 0.0 : -90.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}