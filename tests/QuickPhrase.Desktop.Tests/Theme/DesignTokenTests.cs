using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
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

    private static ResourceDictionary LoadTheme(string fileName = "QuickPhraseTheme.xaml")
    {
        var path = Path.Combine(FindRepoRoot(), "desktop", "QuickPhrase.Desktop", "Themes", fileName);
        using var stream = File.OpenRead(path);
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
    }

    private static void AssertColor(ResourceDictionary dictionary, string key, string expected)
    {
        var color = Assert.IsType<Color>(dictionary[key]);
        Assert.Equal(expected, color.ToString());
    }

    [Fact]
    public void LightTheme_ExposesYoungZeusBrandTokens()
    {
        var dict = LoadTheme();
        var expected = new Dictionary<string, string>
        {
            ["BrandPrimaryColor"] = "#FF4C8DFF",
            ["BrandPrimaryHoverColor"] = "#FF3D7FEB",
            ["BrandPrimaryPressedColor"] = "#FF326FD6",
            ["BrandIceColor"] = "#FF8EC5FF",
            ["BrandIceLightColor"] = "#FFE8F4FF",
            ["BrandGoldColor"] = "#FFF5B940",
            ["BrandGoldLightColor"] = "#FFFFD76A",
            ["WindowBackgroundColor"] = "#FFF7F9FC",
            ["NavigationBackgroundColor"] = "#FFF2F6FB",
            ["SurfaceColor"] = "#FFFFFFFF",
            ["SurfaceSecondaryColor"] = "#FFF9FBFE",
            ["BorderNormalColor"] = "#FFDDE3EA",
            ["SeparatorColor"] = "#FFE8EDF3",
            ["TextPrimaryColor"] = "#FF182230",
            ["TextSecondaryColor"] = "#FF475467",
            ["TextMutedColor"] = "#FF7B8796",
            ["SelectionBackgroundColor"] = "#FFEAF3FF",
            ["HoverBackgroundColor"] = "#FFF3F8FF",
            ["FocusColor"] = "#FF4C8DFF",
            ["SuccessColor"] = "#FF32A66A",
            ["WarningColor"] = "#FFE9A23B",
            ["DangerColor"] = "#FFD64545",
        };

        foreach (var pair in expected)
            AssertColor(dict, pair.Key, pair.Value);

        Assert.IsType<SolidColorBrush>(dict["BrandPrimaryBrush"]);
        Assert.IsType<SolidColorBrush>(dict["SelectionBackgroundBrush"]);
        Assert.IsType<SolidColorBrush>(dict["BrandGoldBrush"]);
    }

    [Fact]
    public void DarkTheme_ExposesIndependentYoungZeusMapping()
    {
        var dict = LoadTheme("QuickPhraseTheme.Dark.xaml");
        var expected = new Dictionary<string, string>
        {
            ["BrandPrimaryColor"] = "#FF75AEFF",
            ["BrandPrimaryHoverColor"] = "#FF8EC5FF",
            ["BrandPrimaryPressedColor"] = "#FF5D97E8",
            ["BrandIceColor"] = "#FFB9DBFF",
            ["BrandIceLightColor"] = "#FF1D3550",
            ["WindowBackgroundColor"] = "#FF141B26",
            ["NavigationBackgroundColor"] = "#FF182331",
            ["SurfaceColor"] = "#FF1D2A39",
            ["SurfaceSecondaryColor"] = "#FF223142",
            ["BorderNormalColor"] = "#FF3B4B60",
            ["SeparatorColor"] = "#FF2B3A4B",
            ["TextPrimaryColor"] = "#FFF3F7FC",
            ["TextSecondaryColor"] = "#FFC4D0DE",
            ["TextMutedColor"] = "#FF93A4B8",
            ["SelectionBackgroundColor"] = "#FF203F67",
            ["HoverBackgroundColor"] = "#FF1F344E",
            ["FocusColor"] = "#FF8EC5FF",
            ["SuccessColor"] = "#FF58C78F",
            ["WarningColor"] = "#FFF3B85B",
            ["DangerColor"] = "#FFF07878",
        };

        foreach (var pair in expected)
            AssertColor(dict, pair.Key, pair.Value);
    }

    [Fact]
    public void LegacyThemeKeys_RemainCompatibleWithSemanticTokens()
    {
        var light = LoadTheme();
        var dark = LoadTheme("QuickPhraseTheme.Dark.xaml");

        AssertColor(light, "AccentColor", "#FF4C8DFF");
        AssertColor(light, "AccentDarkColor", "#FF326FD6");
        AssertColor(light, "AccentSoftColor", "#FFE8F4FF");
        AssertColor(light, "AccentRowColor", "#FFEAF3FF");
        AssertColor(light, "DividerColor", "#FFE8EDF3");
        AssertColor(light, "BorderSubtleColor", "#FFE8EDF3");
        AssertColor(light, "MutedTextColor", "#FF7B8796");
        AssertColor(light, "TextColor", "#FF182230");
        AssertColor(light, "FocusRingColor", "#FF4C8DFF");

        AssertColor(dark, "AccentColor", "#FF75AEFF");
        AssertColor(dark, "AccentDarkColor", "#FF5D97E8");
        AssertColor(dark, "AccentSoftColor", "#FF1D3550");
        AssertColor(dark, "AccentRowColor", "#FF203F67");
        AssertColor(dark, "DividerColor", "#FF2B3A4B");
        AssertColor(dark, "TextColor", "#FFF3F7FC");
        AssertColor(dark, "FocusRingColor", "#FF8EC5FF");
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
        // 主题色重构不得改变既有紧凑密度和控件几何尺寸。
        Assert.Equal(34d, (double)dict["ButtonHeight"]);
        Assert.Equal(36d, (double)dict["SearchBoxHeight"]);
        Assert.Equal(32d, (double)dict["PhraseRowMinHeight"]);
        Assert.Equal(new CornerRadius(8), (CornerRadius)dict["RadiusMedium"]);
        Assert.Equal(new CornerRadius(4), (CornerRadius)dict["RadiusXs"]);
    }

    [Fact]
    public void ProductionXaml_DoesNotContainLegacyPurpleBrandColors()
    {
        var desktopRoot = Path.Combine(FindRepoRoot(), "desktop", "QuickPhrase.Desktop");
        var legacyColors = new[]
        {
            "#6D45F5", "#5731E8", "#F5F3FF", "#7858FF", "#4628C4",
            "#9974FF", "#3D356B"
        };

        foreach (var file in Directory.EnumerateFiles(desktopRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var color in legacyColors)
                Assert.DoesNotContain(color, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
