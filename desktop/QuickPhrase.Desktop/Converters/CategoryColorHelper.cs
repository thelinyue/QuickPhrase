using System;

namespace QuickPhrase.Desktop.Converters;

/// <summary>
/// 分类颜色辅助：基于分类名稳定映射到 Brush.Phrase.* 调色板（取 base 色），
/// 并提供 Tint 调淡（降饱和度 + 提高亮度），用于二级分类基于其对应一级
/// 分类颜色派生出更浅的层级色。
/// </summary>
internal static class CategoryColorHelper
{
    // 分类标识使用固定色板中的非默认颜色；default 的白色保留给“无颜色”话术。
    private static readonly string[] Palette =
    {
        "Brush.Phrase.Orange", "Brush.Phrase.Blue", "Brush.Phrase.Magenta", "Brush.Phrase.Purple",
        "Brush.Phrase.Green", "Brush.Phrase.Pink", "Brush.Phrase.Teal", "Brush.Phrase.Tan", "Brush.Phrase.Gray"
    };

    /// <summary>按分类名稳定哈希到 Brush.Phrase.* 调色板，取得 base 色笔刷。</summary>
    public static System.Windows.Media.Brush GetBrush(string name)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return System.Windows.Media.Brushes.Transparent;

        var key = string.IsNullOrEmpty(name) ? "default" : name;
        var hash = 0;
        foreach (var c in key) hash = (hash * 31 + c) & 0x7fffffff;
        var resourceKey = Palette[hash % Palette.Length];

        if (app.Resources[resourceKey] is System.Windows.Media.Brush brush) return brush;
        if (app.Resources["Brush.Phrase.Default"] is System.Windows.Media.Brush fallback) return fallback;
        return System.Windows.Media.Brushes.Gray;
    }

    /// <summary>铺满底色用的浅色背景：保留 base 色的色相与少量饱和度，但把亮度拉高，
    /// 作为分类（一级 chip / 二级标题条）默认态的整块填充底色。</summary>
    public static System.Windows.Media.Brush SoftBackground(string name)
    {
        var brush = GetBrush(name);
        if (brush is not System.Windows.Media.SolidColorBrush scb) return brush;
        var (h, s, l) = RgbToHsl(scb.Color);
        s = Math.Min(0.55, s * 0.7 + 0.05);
        l = Math.Min(0.93, l * 0.35 + 0.60);
        return new System.Windows.Media.SolidColorBrush(HslToRgb(h, s, l));
    }

    /// <summary>选中/展开态的加深底色：在 base 色基础上提高饱和度、压低亮度，
    /// 作为分类选中后的整块填充底色（与浅色默认态形成对比）。</summary>
    public static System.Windows.Media.Brush DeepBackground(string name)
    {
        var brush = GetBrush(name);
        if (brush is not System.Windows.Media.SolidColorBrush scb) return brush;
        var (h, s, l) = RgbToHsl(scb.Color);
        s = Math.Min(0.90, s * 0.95 + 0.08);
        l = Math.Max(0.40, l * 0.55);
        return new System.Windows.Media.SolidColorBrush(HslToRgb(h, s, l));
    }

    /// <summary>对 base 色降饱和度并提高亮度（调淡），使二级分类颜色更浅、层级更弱。</summary>
    public static System.Windows.Media.Brush Tint(System.Windows.Media.Brush brush)
    {
        if (brush is not System.Windows.Media.SolidColorBrush scb) return brush;
        var (h, s, l) = RgbToHsl(scb.Color);
        s *= 0.55;                                // 降低饱和度
        l = Math.Min(0.62, l * 0.5 + 0.32);      // 提高亮度，并限制上限保证文字可读
        return new System.Windows.Media.SolidColorBrush(HslToRgb(h, s, l)) { Opacity = scb.Opacity };
    }

    private static (double H, double S, double L) RgbToHsl(System.Windows.Media.Color c)
    {
        var r = c.R / 255.0; var g = c.G / 255.0; var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        var d = max - min;
        if (d == 0) return (0, 0, l);
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;
        return (h, s, l);
    }

    private static System.Windows.Media.Color HslToRgb(double h, double s, double l)
    {
        if (s == 0) return System.Windows.Media.Color.FromRgb(ToByte(l), ToByte(l), ToByte(l));
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        var r = HueToRgb(p, q, h + 1.0 / 3);
        var g = HueToRgb(p, q, h);
        var b = HueToRgb(p, q, h - 1.0 / 3);
        return System.Windows.Media.Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}


