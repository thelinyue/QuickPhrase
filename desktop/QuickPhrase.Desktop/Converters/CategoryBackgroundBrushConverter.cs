using System;
using System.Globalization;
using System.Windows.Data;

namespace QuickPhrase.Desktop.Converters;

/// <summary>
/// 按分类名取得"铺满底色"笔刷：参数缺省/soft = 浅色默认底色；deep = 选中/展开态加深底色。
/// 用于一级分类 chip 与二级分类标题条的整块背景填充，保持层级视觉一致。
/// </summary>
public sealed class CategoryBackgroundBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? string.Empty;
        var deep = string.Equals(parameter as string, "deep", StringComparison.OrdinalIgnoreCase);
        return deep
            ? CategoryColorHelper.DeepBackground(name)
            : CategoryColorHelper.SoftBackground(name);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
