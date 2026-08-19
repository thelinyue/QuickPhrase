using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickPhrase.Desktop.Converters;

/// <summary>
/// 输入一级分类名，取得其 base 色并调淡（降饱和 + 提亮），用于二级分类
/// 的颜色标识与文字，使其视觉层级弱于对应的一级分类。
/// </summary>
public sealed class CategoryTintBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? string.Empty;
        return CategoryColorHelper.Tint(CategoryColorHelper.GetBrush(name));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
