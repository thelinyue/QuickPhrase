using System.IO;
using System.Xml.Linq;

namespace QuickPhrase.Desktop.Tests.Theme;

/// <summary>
/// 验证图标驱动主题的文件边界和语义资源契约。
/// 新主题不允许继续携带收藏 Token，也不为旧主题文件提供兼容别名。
/// </summary>
public sealed class BrandThemeContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] RequiredBrushKeys =
    {
        "Brush.Background.Default",
        "Brush.Background.Secondary",
        "Brush.Surface.Default",
        "Brush.Surface.Hover",
        "Brush.Surface.Selected",
        "Brush.Accent.Primary",
        "Brush.Accent.Primary.Hover",
        "Brush.Accent.Primary.Pressed",
        "Brush.Accent.Soft",
        "Brush.Accent.Gold",
        "Brush.Text.Primary",
        "Brush.Text.Secondary",
        "Brush.Text.Disabled",
        "Brush.Text.OnAccent",
        "Brush.Border.Default",
        "Brush.Border.Strong",
        "Brush.Border.Focus",
        "Brush.Selection.Indicator",
    };

    [Fact]
    public void ThemeUsesNewFourFileTokenArchitectureWithoutFavoriteResources()
    {
        var colorsPath = DesignSystemPath("Tokens", "Colors.xaml");
        var brushesPath = DesignSystemPath("Tokens", "Brushes.xaml");
        var lightPath = DesignSystemPath("Themes", "Theme.Light.xaml");
        var darkPath = DesignSystemPath("Themes", "Theme.Dark.xaml");

        Assert.True(File.Exists(colorsPath));
        Assert.True(File.Exists(brushesPath));
        Assert.True(File.Exists(lightPath));
        Assert.True(File.Exists(darkPath));
        Assert.False(File.Exists(DesignSystemPath("Themes", "QuickPhraseTheme.Light.xaml")));
        Assert.False(File.Exists(DesignSystemPath("Themes", "QuickPhraseTheme.Dark.xaml")));

        var allText = string.Join('\n', new[] { colorsPath, brushesPath, lightPath, darkPath }.Select(File.ReadAllText));
        Assert.DoesNotContain("Favorite", allText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("收藏", allText, StringComparison.Ordinal);

        var brushKeys = ReadKeys(brushesPath);
        foreach (var key in RequiredBrushKeys)
            Assert.Contains(key, brushKeys);

        Assert.Equal(ReadKeys(lightPath), ReadKeys(darkPath));
    }

    [Fact]
    public void BrandPrimitivesAndLightSemanticColorsUseApprovedPalette()
    {
        var colors = ReadValues(DesignSystemPath("Tokens", "Colors.xaml"));
        Assert.Equal("#4A90FF", colors["Color.Brand.SkyBlue"]);
        Assert.Equal("#2563EB", colors["Color.Brand.Primary"]);
        Assert.Equal("#1D4ED8", colors["Color.Brand.Primary.Hover"]);
        Assert.Equal("#1E40AF", colors["Color.Brand.Primary.Pressed"]);
        Assert.Equal("#FBBF24", colors["Color.Brand.Gold"]);
        Assert.Equal("#F59E0B", colors["Color.Brand.Gold.Strong"]);

        var light = ReadValues(DesignSystemPath("Themes", "Theme.Light.xaml"));
        Assert.Equal("#F8FAFC", light["Color.Background.Default"]);
        Assert.Equal("#F1F5F9", light["Color.Background.Secondary"]);
        Assert.Equal("#FFFFFF", light["Color.Surface.Default"]);
        Assert.Equal("#EFF6FF", light["Color.Surface.Selected"]);
        Assert.Equal("#1E293B", light["Color.Text.Primary"]);
        Assert.Equal("#64748B", light["Color.Text.Secondary"]);
        Assert.Equal("#E2E8F0", light["Color.Border.Default"]);
    }

    [Fact]
    public void ThemeAggregatorAndServiceReferenceOnlyNewThemeFiles()
    {
        var aggregator = File.ReadAllText(ProjectPath("Themes", "QuickPhraseTheme.xaml"));
        var service = File.ReadAllText(ProjectPath("Services", "ThemeService.cs"));
        var combined = aggregator + service;

        Assert.Contains("Colors.xaml", aggregator, StringComparison.Ordinal);
        Assert.Contains("Brushes.xaml", aggregator, StringComparison.Ordinal);
        Assert.Contains("Theme.Light.xaml", combined, StringComparison.Ordinal);
        Assert.Contains("Theme.Dark.xaml", service, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickPhraseTheme.Light.xaml", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickPhraseTheme.Dark.xaml", combined, StringComparison.Ordinal);
    }

    private static HashSet<string> ReadKeys(string path) =>
        XDocument.Load(path)
            .Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> ReadValues(string path) =>
        XDocument.Load(path)
            .Descendants()
            .Select(element => new
            {
                Key = (string?)element.Attribute(Xaml + "Key"),
                Value = element.Value.Trim(),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(item => item.Key!, item => item.Value, StringComparer.Ordinal);

    private static string DesignSystemPath(params string[] parts) =>
        Path.Combine(new[] { ProjectRoot(), "DesignSystem" }.Concat(parts).ToArray());

    private static string ProjectPath(params string[] parts) =>
        Path.Combine(new[] { ProjectRoot() }.Concat(parts).ToArray());

    private static string ProjectRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "desktop", "QuickPhrase.Desktop"));
}
