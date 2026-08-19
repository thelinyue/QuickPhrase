using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickPhrase.Desktop.Converters;

/// <summary>
/// 按分类名稳定映射到 PhraseColor* 固定色板，作为二级分类条的颜色标识。
/// 同名分类颜色稳定，不同分类尽量区分；无名称时回退到默认色。
/// </summary>
public sealed class CategoryColorBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? string.Empty;
        return CategoryColorHelper.GetBrush(name);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

