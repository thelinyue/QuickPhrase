using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickPhrase.Desktop.Converters;

/// <summary>
/// 将分类的 ParentId 映射为 optgroup 分组名：null → "一级分类"，否则 → "二级分类"。
/// 供 EditorView 分类下拉的 PropertyGroupDescription.Converter 使用（对齐 design-system.md 5.7）。
/// </summary>
public sealed class CategoryLevelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? "一级分类" : "二级分类";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
