using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests.Theme;

public class DesignTokenTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QuickPhrase.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    private static ResourceDictionary LoadTheme()
    {
        var path = Path.Combine(FindRepoRoot(), "desktop", "QuickPhrase.Desktop", "Themes", "QuickPhraseTheme.xaml");
        using var stream = File.OpenRead(path);
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
    }

    [Fact]
    public void AccentColor_MatchesBrand()
    {
        // 对齐闪语原型 OKLCH violet-500 (#6D45F5)
        var dict = LoadTheme();
        var color = (Color)dict["AccentColor"];
        Assert.Equal("#FF6D45F5", color.ToString());
    }

    [Fact]
    public void PhraseColorBrushes_Exist_AndMatchEditorColorKeys()
    {
        var dict = LoadTheme();
        var expected = EditorViewModel.ColorKeys
            .Select(c => "PhraseColor" + char.ToUpperInvariant(c.Key[0]) + c.Key.Substring(1))
            .ToArray();
        Assert.Equal(10, expected.Length);
        foreach (var option in EditorViewModel.ColorKeys)
        {
            var key = "PhraseColor" + char.ToUpperInvariant(option.Key[0]) + option.Key.Substring(1);
            Assert.True(dict.Contains(key), $"missing brush {key}");
            var brush = Assert.IsType<SolidColorBrush>(dict[key]);
            Assert.Equal("#FF" + option.Hex[1..], brush.Color.ToString());
        }
    }

    [Fact]
    public void Typography_And_Spacing_Tokens_Exist()
    {
        var dict = LoadTheme();
        Assert.IsType<FontFamily>(dict["UiFontFamily"]);
        // 对齐闪语原型紧凑密度
        Assert.Equal(34d, (double)dict["ButtonHeight"]);
        Assert.Equal(36d, (double)dict["SearchBoxHeight"]);
        // 话术行高 32px（2026-08-18 紧凑化调整：由 40 收窄，行间距更紧凑）
        Assert.Equal(32d, (double)dict["PhraseRowMinHeight"]);
        Assert.Equal(new CornerRadius(8), (CornerRadius)dict["RadiusMedium"]);
        Assert.Equal(new CornerRadius(4), (CornerRadius)dict["RadiusXs"]);
    }

    [Fact]
    public void BrandColorTokens_AlignToFlashPrototype()
    {
        // 关键品牌色 hex 锁定，避免回归
        var dict = LoadTheme();
        Assert.Equal("#FF6D45F5", ((Color)dict["AccentColor"]).ToString());
        Assert.Equal("#FF5731E8", ((Color)dict["AccentDarkColor"]).ToString());
        Assert.Equal("#FFF5F3FF", ((Color)dict["AccentSoftColor"]).ToString());
        Assert.Equal("#FFE4E4E9", ((Color)dict["DividerColor"]).ToString());
        Assert.Equal("#FF74747F", ((Color)dict["MutedTextColor"]).ToString());
        Assert.Equal("#FFFAFAFC", ((Color)dict["AppBackgroundColor"]).ToString());
        Assert.Equal("#FF1C1C22", ((Color)dict["TextColor"]).ToString());
        Assert.Equal("#FFD63A3A", ((Color)dict["DangerColor"]).ToString());
        Assert.Equal("#FF9974FF", ((Color)dict["FocusRingColor"]).ToString());
    }
}

