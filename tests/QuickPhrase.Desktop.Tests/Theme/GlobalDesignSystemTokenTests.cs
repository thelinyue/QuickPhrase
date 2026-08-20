using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Markup;
using System.Xml.Linq;

namespace QuickPhrase.Desktop.Tests.Theme;

/// <summary>
/// 验证全局 Design System 的基础 Token 和主题契约。
/// 测试直接读取并物化 XAML 真源，不为旧资源键提供兼容别名。
/// </summary>
public sealed class GlobalDesignSystemTokenTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] TokenFiles =
    {
        "Typography.xaml",
        "Thickness.xaml",
        "Radius.xaml",
        "Sizes.xaml",
        "Motion.xaml",
    };

    private static readonly string[] FrozenTokenMergeOrder =
    {
        "Typography.xaml",
        "Thickness.xaml",
        "Radius.xaml",
        "Sizes.xaml",
        "Motion.xaml",
    };

    private static readonly string[] RequiredThemeColorKeys =
    {
        "Color.Brand.Primary",
        "Color.Brand.Primary.Hover",
        "Color.Brand.Primary.Pressed",
        "Color.Brand.BlueStrong",
        "Color.Brand.Ice",
        "Color.Brand.IceLight",
        "Color.Brand.Gold",
        "Color.Brand.Gold.Hover",
        "Color.Background.Window",
        "Color.Background.Navigation",
        "Color.Surface.Primary",
        "Color.Surface.Secondary",
        "Color.Surface.Elevated",
        "Color.Text.Primary",
        "Color.Text.Secondary",
        "Color.Text.Muted",
        "Color.Text.Disabled",
        "Color.Text.OnBrand",
        "Color.Border.Default",
        "Color.Border.Subtle",
        "Color.Border.Focus",
        "Color.State.Hover",
        "Color.State.Selected",
        "Color.State.SelectedBorder",
        "Color.State.SelectionIndicator",
        "Color.Status.Success",
        "Color.Status.Warning",
        "Color.Status.Error",
        "Color.Favorite.Inactive",
        "Color.Favorite.Active",
        "Color.Overlay",
        "Color.Shadow.Default",
        "Color.Phrase.Default",
        "Color.Phrase.Orange",
        "Color.Phrase.Blue",
        "Color.Phrase.Magenta",
        "Color.Phrase.Purple",
        "Color.Phrase.Green",
        "Color.Phrase.Pink",
        "Color.Phrase.Teal",
        "Color.Phrase.Tan",
        "Color.Phrase.Gray",
    };

    private static readonly string[] RequiredShadowEffectKeys =
    {
        "Effect.Shadow.Dialog",
        "Effect.Shadow.Elevated",
        "Effect.Shadow.Popup",
    };

    private static readonly HashSet<string> ForbiddenLegacyThemeKeys = new(StringComparer.Ordinal)
    {
        "AccentBrush",
        "AccentHoverBrush",
        "AppBackgroundBrush",
        "BorderSubtleBrush",
        "DividerBrush",
        "MutedTextBrush",
        "SurfaceBrush",
    };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("找不到 QuickPhrase 仓库根目录。");
    }

    private static string DesignSystemPath(params string[] segments)
    {
        var parts = new[]
        {
            FindRepoRoot(),
            "desktop",
            "QuickPhrase.Desktop",
            "DesignSystem",
        }.Concat(segments).ToArray();

        return Path.Combine(parts);
    }

    private static ResourceDictionary LoadDictionary(string path)
    {
        using var stream = File.OpenRead(path);
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static XDocument LoadDocument(string path) => XDocument.Load(path, LoadOptions.PreserveWhitespace);

    private static IReadOnlyDictionary<string, XElement> ReadKeyedElements(string path)
    {
        return LoadDocument(path)
            .Descendants()
            .Where(element => element.Attribute(XamlNamespace + "Key") is not null)
            .ToDictionary(
                element => element.Attribute(XamlNamespace + "Key")!.Value,
                element => element,
                StringComparer.Ordinal);
    }

    private static string ThemePath(string fileName) => DesignSystemPath("Themes", fileName);

    private static void AssertResourceValues<T>(
        ResourceDictionary dictionary,
        IEnumerable<(string Key, T Expected)> expectedValues)
    {
        foreach (var (key, expected) in expectedValues)
            Assert.Equal(expected, Assert.IsType<T>(dictionary[key]));
    }

    private static void AssertKeysUsePrefix(
        string fileName,
        IEnumerable<string> keys,
        params string[] allowedPrefixes)
    {
        foreach (var key in keys)
        {
            Assert.Contains('.', key);
            Assert.True(
                allowedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)),
                $"{fileName} 包含未批准的资源键或旧 Alias：{key}");
        }
    }

    private static Color ParseColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value)!;

    private static double CalculateContrastRatio(Color first, Color second)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) =>
            (0.2126 * Linearize(color.R)) +
            (0.7152 * Linearize(color.G)) +
            (0.0722 * Linearize(color.B));

        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static ResourceDictionary LoadMergedDesignSystem(string themeFileName)
    {
        var merged = new ResourceDictionary();
        foreach (var tokenFileName in FrozenTokenMergeOrder)
            merged.MergedDictionaries.Add(LoadDictionary(DesignSystemPath("Tokens", tokenFileName)));

        merged.MergedDictionaries.Add(LoadDictionary(ThemePath(themeFileName)));
        return merged;
    }

    [Fact]
    public void RequiredTokenFilesAndSemanticKeys_ArePresentAndUseDottedNames()
    {
        var requiredKeysByFile = new Dictionary<string, string[]>
        {
            ["Typography.xaml"] = new[]
            {
                "Typography.FontFamily.UI", "Typography.FontFamily.Mono",
                "Typography.Title.Large.FontSize", "Typography.Title.Large.FontWeight", "Typography.Title.Large.LineHeight",
                "Typography.Title.Medium.FontSize", "Typography.Title.Medium.FontWeight", "Typography.Title.Medium.LineHeight",
                "Typography.Title.Small.FontSize", "Typography.Title.Small.FontWeight", "Typography.Title.Small.LineHeight",
                "Typography.Body.Large.FontSize", "Typography.Body.Large.FontWeight", "Typography.Body.Large.LineHeight",
                "Typography.Body.Medium.FontSize", "Typography.Body.Medium.FontWeight", "Typography.Body.Medium.LineHeight",
                "Typography.Body.Small.FontSize", "Typography.Body.Small.FontWeight", "Typography.Body.Small.LineHeight",
                "Typography.Caption.FontSize", "Typography.Caption.FontWeight", "Typography.Caption.LineHeight",
                "Typography.Label.FontSize", "Typography.Label.FontWeight", "Typography.Label.LineHeight",
                "Typography.Mono.FontSize", "Typography.Mono.FontWeight", "Typography.Mono.LineHeight",
            },
            ["Thickness.xaml"] = new[]
            {
                "Thickness.None", "Thickness.XS", "Thickness.SM", "Thickness.MD", "Thickness.LG",
                "Thickness.XL", "Thickness.XXL", "Thickness.XXXL", "Thickness.4XL", "Thickness.5XL",
                "Thickness.Border.Default", "Thickness.Page", "Thickness.Section", "Thickness.Card",
                "Thickness.Dialog", "Thickness.Popup", "Thickness.Control.Button.Compact",
                "Thickness.Control.Button.Default", "Thickness.Control.Input", "Thickness.Control.Input.Multiline", "Thickness.Window.ResizeBorder",
                "Thickness.Gap.Inline.XS", "Thickness.Gap.Inline.SM", "Thickness.Gap.Inline.MD", "Thickness.Gap.Inline.LG",
                "Thickness.Gap.Inline.Before.MD", "Thickness.Gap.Inline.Before.LG",
                "Thickness.Gap.Stack.XS", "Thickness.Gap.Stack.SM", "Thickness.Gap.Stack.MD", "Thickness.Gap.Stack.LG",
                "Thickness.Gap.Stack.Before.XS", "Thickness.Gap.Stack.Before.SM", "Thickness.Gap.Stack.Before.MD",
                "Thickness.Gap.Stack.Before.LG", "Thickness.Gap.Stack.Before.XL",
                "Thickness.Launcher.Section", "Thickness.Launcher.Preview", "Thickness.Launcher.FooterHint",
                "Thickness.Onboarding.Content", "Thickness.Onboarding.IntroDescription", "Thickness.Onboarding.CopyBlock",
                "Thickness.Library.Header", "Thickness.Library.Toolbar", "Thickness.Library.Footer",
                "Thickness.Dialog.Footer", "Thickness.Dialog.SectionLabel", "Thickness.Dialog.Footer.Padding", "Thickness.Dialog.Option",
                "Thickness.Dialog.InlineAction", "Thickness.Dialog.Summary", "Thickness.Dialog.SummaryValue",
                "Thickness.Dialog.SummaryValue.Last", "Thickness.Dialog.Label",
                "Thickness.Settings.Page", "Thickness.Settings.Row",
            },
            ["Radius.xaml"] = new[]
            {
                "Radius.None", "Radius.XS", "Radius.Small", "Radius.Medium", "Radius.Large", "Radius.XL",
                "Radius.Control", "Radius.Card", "Radius.Popup", "Radius.Dialog", "Radius.Launcher",
            },
            ["Sizes.xaml"] = new[]
            {
                "Size.Control.Compact", "Size.Control.Default", "Size.Button.Icon.Width", "Size.Button.Icon.Height",
                "Size.Input.Search", "Size.Switch.Width", "Size.Switch.Height", "Size.Switch.Thumb",
                "Size.TitleBar.Height", "Size.TitleBar.GridLength", "Size.TitleBar.CaptionButton.Width",
                "Size.Navigation.Item", "Size.Phrase.Row.Minimum", "Size.Phrase.IndexColumn.GridLength", "Size.Editor.Body.Height", "Size.Editor.Content.Maximum",
                "Size.Settings.Sidebar.Width", "Size.Settings.Sidebar.GridLength", "Size.Settings.Content.Maximum",
                "Size.MainWindow.Width", "Size.MainWindow.Height", "Size.MainWindow.MinimumWidth", "Size.MainWindow.MinimumHeight",
                "Size.SettingsWindow.Width", "Size.SettingsWindow.Height", "Size.SettingsWindow.MinimumWidth", "Size.SettingsWindow.MinimumHeight",
                "Size.Launcher.Width", "Size.Launcher.Height", "Size.Launcher.MinimumWidth", "Size.Launcher.MinimumHeight", "Size.Launcher.MaximumHeight",
                "Size.Onboarding.Width", "Size.Onboarding.Height", "Size.Onboarding.MinimumWidth", "Size.Onboarding.MinimumHeight",
                "Size.Onboarding.TitleBar.GridLength", "Size.Onboarding.Header.GridLength", "Size.Onboarding.Footer.GridLength",
                "Size.Onboarding.Progress.Height", "Size.Onboarding.PhraseBody.Height",
                "Size.Onboarding.PracticeIndicator.Gutter.GridLength", "Size.Onboarding.PracticeIndicator.Diameter",
                "Size.Onboarding.FooterStatus.Height", "Size.Onboarding.FooterStatus.GridLength", "Size.Library.BrandIcon", "Size.List.SelectionIndicator.Width",
                "Size.Dialog.Category.Width", "Size.Dialog.Category.Height", "Size.Dialog.Category.MinimumHeight", "Size.Dialog.Status.MinimumHeight",
                "Size.Dialog.Export.Width", "Size.Dialog.Export.Height", "Size.Dialog.Export.MinimumWidth", "Size.Dialog.Export.MinimumHeight",
                "Size.Dialog.Import.Width", "Size.Dialog.Import.Height", "Size.Dialog.Import.MinimumWidth", "Size.Dialog.Import.MinimumHeight",
                "Size.Dialog.Navigation.Width", "Size.Dialog.Navigation.Height", "Size.Dialog.PhraseMove.Width", "Size.Dialog.PhraseMove.Height",
                "Size.Dialog.Shortcut.Width", "Size.Dialog.Shortcut.Height",
                "Size.SearchHistory.Gutter.GridLength", "Size.StatePresenter.MinimumHeight",
            },
            ["Motion.xaml"] = new[]
            {
                "Motion.Duration.Fast", "Motion.Duration.Normal", "Motion.Duration.Slow",
                "Motion.Easing.Standard", "Motion.Easing.Emphasized",
            },
        };

        var approvedPrefixByFile = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Typography.xaml"] = "Typography.",
            ["Thickness.xaml"] = "Thickness.",
            ["Radius.xaml"] = "Radius.",
            ["Sizes.xaml"] = "Size.",
            ["Motion.xaml"] = "Motion.",
        };

        foreach (var fileName in TokenFiles)
        {
            var path = DesignSystemPath("Tokens", fileName);
            Assert.True(File.Exists(path), $"缺少 Token 文件：{path}");
            var keyedElements = ReadKeyedElements(path);

            foreach (var key in requiredKeysByFile[fileName])
                Assert.True(keyedElements.ContainsKey(key), $"{fileName} 缺少资源键 {key}");

            AssertKeysUsePrefix(fileName, keyedElements.Keys, approvedPrefixByFile[fileName]);
        }
    }

    [Fact]
    public void LightAndDarkThemes_ExposeIdenticalGovernedSemanticKeySets()
    {
        var light = ReadKeyedElements(ThemePath("QuickPhraseTheme.Light.xaml"));
        var dark = ReadKeyedElements(ThemePath("QuickPhraseTheme.Dark.xaml"));

        Assert.Equal(light.Keys.OrderBy(key => key), dark.Keys.OrderBy(key => key));

        foreach (var theme in new[] { (Name: "Light Theme", Resources: light), (Name: "Dark Theme", Resources: dark) })
        {
            AssertKeysUsePrefix(theme.Name, theme.Resources.Keys, "Color.", "Brush.", "Effect.Shadow.");

            foreach (var forbiddenKey in ForbiddenLegacyThemeKeys)
                Assert.DoesNotContain(forbiddenKey, theme.Resources.Keys);

            foreach (var colorKey in RequiredThemeColorKeys)
            {
                Assert.True(theme.Resources.ContainsKey(colorKey), $"{theme.Name} 缺少 {colorKey}");
                Assert.Equal("Color", theme.Resources[colorKey].Name.LocalName);

                var brushKey = colorKey.Replace("Color.", "Brush.", StringComparison.Ordinal);
                Assert.True(theme.Resources.ContainsKey(brushKey), $"{theme.Name} 缺少 {brushKey}");
            }

            var shadowKeys = theme.Resources.Keys
                .Where(key => key.StartsWith("Effect.Shadow.", StringComparison.Ordinal))
                .OrderBy(key => key)
                .ToArray();
            Assert.Equal(RequiredShadowEffectKeys, shadowKeys);
        }
    }

    [Fact]
    public void DesignSystemXamlOutsideThemes_ContainsNoHexOrColorDeclarations()
    {
        var hexColor = new Regex(
            @"(?<![0-9A-Fa-f])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3})(?![0-9A-Fa-f])",
            RegexOptions.CultureInvariant);
        var themesDirectory = Path.GetFullPath(DesignSystemPath("Themes")) + Path.DirectorySeparatorChar;
        var xamlFiles = Directory
            .EnumerateFiles(DesignSystemPath(), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).StartsWith(themesDirectory, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(xamlFiles);
        foreach (var path in xamlFiles)
        {
            var content = File.ReadAllText(path);
            Assert.DoesNotMatch(hexColor, content);

            var document = LoadDocument(path);
            var colorDeclarations = document
                .Descendants()
                .Where(element => element.Name.LocalName is "Color" or "SolidColorBrush")
                .Select(element => element.Name.LocalName)
                .Concat(document
                    .Descendants()
                    .Attributes()
                    .Where(attribute =>
                        attribute.Name.LocalName == "Color" &&
                        !attribute.Value.StartsWith("{DynamicResource ", StringComparison.Ordinal) &&
                        !attribute.Value.StartsWith("{StaticResource ", StringComparison.Ordinal))
                    .Select(attribute => $"Color=\"{attribute.Value}\""))
                .ToArray();

            Assert.True(
                colorDeclarations.Length == 0,
                $"主题外 XAML 不得声明 Color 或 SolidColorBrush：{Path.GetRelativePath(DesignSystemPath(), path)}；发现 {string.Join(", ", colorDeclarations)}");
        }
    }

    [Theory]
    [InlineData("QuickPhraseTheme.Light.xaml")]
    [InlineData("QuickPhraseTheme.Dark.xaml")]
    public void ThemeBrushesAndShadowEffects_UseDynamicResource(string fileName)
    {
        var keyedElements = ReadKeyedElements(ThemePath(fileName));

        foreach (var pair in keyedElements.Where(pair => pair.Key.StartsWith("Brush.", StringComparison.Ordinal)))
        {
            Assert.Equal("SolidColorBrush", pair.Value.Name.LocalName);
            var expectedColorKey = pair.Key.Replace("Brush.", "Color.", StringComparison.Ordinal);
            Assert.Equal($"{{DynamicResource {expectedColorKey}}}", pair.Value.Attribute("Color")?.Value);
        }

        foreach (var effectKey in RequiredShadowEffectKeys)
        {
            var effect = keyedElements[effectKey];
            Assert.Equal("DropShadowEffect", effect.Name.LocalName);
            Assert.Equal("{DynamicResource Color.Shadow.Default}", effect.Attribute("Color")?.Value);
        }
    }

    [Fact]
    public void LightAndDarkThemes_ExposeApprovedFixedColorsAndOnBrandContrast()
    {
        var light = ReadKeyedElements(ThemePath("QuickPhraseTheme.Light.xaml"));
        var dark = ReadKeyedElements(ThemePath("QuickPhraseTheme.Dark.xaml"));

        var expectedLight = new Dictionary<string, string>
        {
            ["Color.Brand.Primary"] = "#3478F6",
            ["Color.Brand.Primary.Hover"] = "#2869E8",
            ["Color.Brand.Primary.Pressed"] = "#2059C9",
            ["Color.Brand.BlueStrong"] = "#2563D9",
            ["Color.Brand.Gold"] = "#F2B735",
            ["Color.Background.Window"] = "#EDF3FA",
            ["Color.Background.Navigation"] = "#E8F0F9",
            ["Color.Surface.Primary"] = "#FFFFFF",
            ["Color.Surface.Secondary"] = "#F5F8FC",
            ["Color.Surface.Elevated"] = "#FFFFFF",
            ["Color.Text.Primary"] = "#172033",
            ["Color.Text.Secondary"] = "#44516A",
            ["Color.Text.Muted"] = "#6F7D94",
            ["Color.Text.Disabled"] = "#A5AFBD",
            ["Color.Text.OnBrand"] = "#FFFFFF",
            ["Color.Border.Default"] = "#CBD6E4",
            ["Color.Border.Subtle"] = "#D9E2EC",
            ["Color.Border.Focus"] = "#3478F6",
            ["Color.State.Hover"] = "#EAF3FF",
            ["Color.State.Selected"] = "#DCEAFF",
            ["Color.State.SelectedBorder"] = "#76AAFF",
            ["Color.Status.Success"] = "#2E9B63",
            ["Color.Status.Warning"] = "#E5A12D",
            ["Color.Status.Error"] = "#D64545",
        };

        var expectedDark = new Dictionary<string, string>
        {
            ["Color.Brand.BlueStrong"] = "#9BCBFF",
            ["Color.Background.Window"] = "#141B26",
            ["Color.Background.Navigation"] = "#182331",
            ["Color.Surface.Primary"] = "#1D2A39",
            ["Color.Surface.Secondary"] = "#223142",
            ["Color.Surface.Elevated"] = "#26384B",
            ["Color.Text.Primary"] = "#F3F7FC",
            ["Color.Text.Secondary"] = "#C4D0DE",
            ["Color.Text.Muted"] = "#93A4B8",
            ["Color.Text.OnBrand"] = "#172033",
            ["Color.Border.Default"] = "#3B4B60",
            ["Color.Border.Focus"] = "#8EC5FF",
            ["Color.State.Selected"] = "#203F67",
            ["Color.State.SelectedBorder"] = "#75AEFF",
        };

        foreach (var expected in expectedLight)
            Assert.Equal(expected.Value, light[expected.Key].Value.Trim());

        foreach (var expected in expectedDark)
            Assert.Equal(expected.Value, dark[expected.Key].Value.Trim());

        var lightContrast = CalculateContrastRatio(
            ParseColor(light["Color.Brand.BlueStrong"].Value.Trim()),
            ParseColor(light["Color.Text.OnBrand"].Value.Trim()));
        var darkContrast = CalculateContrastRatio(
            ParseColor(dark["Color.Brand.BlueStrong"].Value.Trim()),
            ParseColor(dark["Color.Text.OnBrand"].Value.Trim()));

        Assert.True(lightContrast >= 4.5, $"Light Primary Button 对比度不足：{lightContrast:F2}:1");
        Assert.True(darkContrast >= 4.5, $"Dark Primary Button 对比度不足：{darkContrast:F2}:1");
    }

    [Fact]
    public void PhrasePalette_PreservesExistingBusinessColors()
    {
        var expected = new Dictionary<string, string>
        {
            ["Color.Phrase.Default"] = "#FFFFFF",
            ["Color.Phrase.Orange"] = "#FF8839",
            ["Color.Phrase.Blue"] = "#178BFF",
            ["Color.Phrase.Magenta"] = "#FF73FF",
            ["Color.Phrase.Purple"] = "#AF60FF",
            ["Color.Phrase.Green"] = "#41C028",
            ["Color.Phrase.Pink"] = "#F67E91",
            ["Color.Phrase.Teal"] = "#00A8A8",
            ["Color.Phrase.Tan"] = "#CB9563",
            ["Color.Phrase.Gray"] = "#5C6772",
        };

        foreach (var fileName in new[] { "QuickPhraseTheme.Light.xaml", "QuickPhraseTheme.Dark.xaml" })
        {
            var theme = ReadKeyedElements(ThemePath(fileName));
            foreach (var pair in expected)
                Assert.Equal(pair.Value, theme[pair.Key].Value.Trim());
        }
    }

    [Fact]
    public void TypographyTokens_ParseAndExposeApprovedScaleAndFallbacks()
    {
        var dictionary = LoadDictionary(DesignSystemPath("Tokens", "Typography.xaml"));
        var expected = new (string Prefix, double Size, FontWeight Weight, double LineHeight)[]
        {
            ("Typography.Title.Large", 18, FontWeights.SemiBold, 24),
            ("Typography.Title.Medium", 16, FontWeights.SemiBold, 22),
            ("Typography.Title.Small", 14, FontWeights.SemiBold, 20),
            ("Typography.Body.Large", 14, FontWeights.Normal, 22),
            ("Typography.Body.Medium", 13, FontWeights.Normal, 20),
            ("Typography.Body.Small", 12, FontWeights.Normal, 18),
            ("Typography.Caption", 12, FontWeights.Normal, 16),
            ("Typography.Label", 13, FontWeights.Medium, 18),
            ("Typography.Mono", 13, FontWeights.Normal, 18),
        };

        Assert.Equal(
            "Segoe UI Variable, Segoe UI, Microsoft YaHei UI, Microsoft YaHei",
            Assert.IsType<FontFamily>(dictionary["Typography.FontFamily.UI"]).Source);
        Assert.Equal(
            "Cascadia Mono, Consolas, Segoe UI Mono",
            Assert.IsType<FontFamily>(dictionary["Typography.FontFamily.Mono"]).Source);

        foreach (var item in expected)
        {
            Assert.Equal(item.Size, Assert.IsType<double>(dictionary[$"{item.Prefix}.FontSize"]));
            Assert.Equal(item.Weight, Assert.IsType<FontWeight>(dictionary[$"{item.Prefix}.FontWeight"]));
            Assert.Equal(item.LineHeight, Assert.IsType<double>(dictionary[$"{item.Prefix}.LineHeight"]));
        }
    }

    [Fact]
    public void ThicknessRadiusSizeAndMotionTokens_ParseAndExposeAllApprovedValues()
    {
        var thickness = LoadDictionary(DesignSystemPath("Tokens", "Thickness.xaml"));
        AssertResourceValues(thickness, new (string Key, Thickness Expected)[]
        {
            ("Thickness.None", new Thickness(0)),
            ("Thickness.XS", new Thickness(4)),
            ("Thickness.SM", new Thickness(8)),
            ("Thickness.MD", new Thickness(12)),
            ("Thickness.LG", new Thickness(16)),
            ("Thickness.XL", new Thickness(20)),
            ("Thickness.XXL", new Thickness(24)),
            ("Thickness.XXXL", new Thickness(32)),
            ("Thickness.4XL", new Thickness(40)),
            ("Thickness.5XL", new Thickness(48)),
            ("Thickness.Border.Default", new Thickness(1)),
            ("Thickness.Page", new Thickness(24)),
            ("Thickness.Section", new Thickness(0, 0, 0, 24)),
            ("Thickness.Card", new Thickness(16)),
            ("Thickness.Dialog", new Thickness(24)),
            ("Thickness.Popup", new Thickness(12)),
            ("Thickness.Control.Button.Compact", new Thickness(12, 0, 12, 0)),
            ("Thickness.Control.Button.Default", new Thickness(16, 0, 16, 0)),
            ("Thickness.Control.Input", new Thickness(12, 0, 12, 0)),
            ("Thickness.Control.Input.Multiline", new Thickness(12, 10, 12, 10)),
            ("Thickness.Window.ResizeBorder", new Thickness(6)),
            ("Thickness.Gap.Inline.XS", new Thickness(0, 0, 4, 0)),
            ("Thickness.Gap.Inline.SM", new Thickness(0, 0, 8, 0)),
            ("Thickness.Gap.Inline.MD", new Thickness(0, 0, 12, 0)),
            ("Thickness.Gap.Inline.LG", new Thickness(0, 0, 16, 0)),
            ("Thickness.Gap.Inline.Before.MD", new Thickness(12, 0, 0, 0)),
            ("Thickness.Gap.Inline.Before.LG", new Thickness(16, 0, 0, 0)),
            ("Thickness.Gap.Stack.XS", new Thickness(0, 0, 0, 4)),
            ("Thickness.Gap.Stack.SM", new Thickness(0, 0, 0, 8)),
            ("Thickness.Gap.Stack.MD", new Thickness(0, 0, 0, 12)),
            ("Thickness.Gap.Stack.LG", new Thickness(0, 0, 0, 16)),
            ("Thickness.Gap.Stack.Before.XS", new Thickness(0, 4, 0, 0)),
            ("Thickness.Gap.Stack.Before.SM", new Thickness(0, 8, 0, 0)),
            ("Thickness.Gap.Stack.Before.MD", new Thickness(0, 12, 0, 0)),
            ("Thickness.Gap.Stack.Before.LG", new Thickness(0, 16, 0, 0)),
            ("Thickness.Gap.Stack.Before.XL", new Thickness(0, 20, 0, 0)),
            ("Thickness.Launcher.Section", new Thickness(0, 12, 0, 8)),
            ("Thickness.Launcher.Preview", new Thickness(0, 4, 0, 12)),
            ("Thickness.Launcher.FooterHint", new Thickness(0, 0, 16, 0)),
            ("Thickness.Onboarding.Content", new Thickness(0, 20, 0, 16)),
            ("Thickness.Onboarding.IntroDescription", new Thickness(0, 12, 0, 20)),
            ("Thickness.Onboarding.CopyBlock", new Thickness(0, 12, 0, 16)),
            ("Thickness.Library.Header", new Thickness(20, 8, 20, 8)),
            ("Thickness.Library.Toolbar", new Thickness(16, 8, 16, 8)),
            ("Thickness.Library.Footer", new Thickness(20, 0, 20, 0)),
            ("Thickness.Dialog.Footer", new Thickness(0, 16, 0, 0)),
            ("Thickness.Dialog.SectionLabel", new Thickness(0, 16, 0, 8)),
            ("Thickness.Dialog.Footer.Padding", new Thickness(0, 16, 0, 0)),
            ("Thickness.Dialog.Option", new Thickness(0, 4, 0, 4)),
            ("Thickness.Dialog.InlineAction", new Thickness(16, 0, 8, 0)),
            ("Thickness.Dialog.Summary", new Thickness(0, 8, 0, 12)),
            ("Thickness.Dialog.SummaryValue", new Thickness(4, 0, 16, 0)),
            ("Thickness.Dialog.SummaryValue.Last", new Thickness(4, 0, 0, 0)),
            ("Thickness.Dialog.Label", new Thickness(0, 0, 0, 8)),
            ("Thickness.Settings.Page", new Thickness(40, 32, 40, 32)),
            ("Thickness.Settings.Row", new Thickness(16, 8, 16, 8)),
        });

        var radius = LoadDictionary(DesignSystemPath("Tokens", "Radius.xaml"));
        AssertResourceValues(radius, new (string Key, CornerRadius Expected)[]
        {
            ("Radius.None", new CornerRadius(0)),
            ("Radius.XS", new CornerRadius(4)),
            ("Radius.Small", new CornerRadius(6)),
            ("Radius.Medium", new CornerRadius(8)),
            ("Radius.Large", new CornerRadius(12)),
            ("Radius.XL", new CornerRadius(16)),
            ("Radius.Control", new CornerRadius(6)),
            ("Radius.Card", new CornerRadius(8)),
            ("Radius.Popup", new CornerRadius(8)),
            ("Radius.Dialog", new CornerRadius(12)),
            ("Radius.Launcher", new CornerRadius(12)),
        });

        var sizes = LoadDictionary(DesignSystemPath("Tokens", "Sizes.xaml"));
        AssertResourceValues(sizes, new (string Key, double Expected)[]
        {
            ("Size.Control.Compact", 32),
            ("Size.Control.Default", 36),
            ("Size.Button.Icon.Width", 32),
            ("Size.Button.Icon.Height", 32),
            ("Size.Input.Search", 36),
            ("Size.Switch.Width", 40),
            ("Size.Switch.Height", 22),
            ("Size.Switch.Thumb", 18),
            ("Size.TitleBar.Height", 32),
            ("Size.TitleBar.CaptionButton.Width", 48),
            ("Size.Editor.Body.Height", 140),
            ("Size.Editor.Content.Maximum", 480),
            ("Size.Navigation.Item", 40),
            ("Size.Phrase.Row.Minimum", 32),
            ("Size.Settings.Sidebar.Width", 176),
            ("Size.Settings.Content.Maximum", 640),
            ("Size.MainWindow.Width", 1200),
            ("Size.MainWindow.Height", 760),
            ("Size.MainWindow.MinimumWidth", 900),
            ("Size.MainWindow.MinimumHeight", 560),
            ("Size.SettingsWindow.Width", 860),
            ("Size.SettingsWindow.Height", 680),
            ("Size.SettingsWindow.MinimumWidth", 560),
            ("Size.SettingsWindow.MinimumHeight", 480),
            ("Size.Launcher.Width", 760),
            ("Size.Launcher.Height", 300),
            ("Size.Launcher.MinimumWidth", 680),
            ("Size.Launcher.MinimumHeight", 260),
            ("Size.Launcher.MaximumHeight", 520),
            ("Size.Onboarding.Width", 640),
            ("Size.Onboarding.Height", 520),
            ("Size.Onboarding.MinimumWidth", 560),
            ("Size.Onboarding.MinimumHeight", 480),
            ("Size.Onboarding.Progress.Height", 4),
            ("Size.Onboarding.PhraseBody.Height", 88),
            ("Size.Onboarding.PracticeIndicator.Diameter", 8),
            ("Size.Onboarding.FooterStatus.Height", 28),
            ("Size.Library.BrandIcon", 24),
            ("Size.List.SelectionIndicator.Width", 4),
            ("Size.Dialog.Category.Width", 380),
            ("Size.Dialog.Category.Height", 240),
            ("Size.Dialog.Category.MinimumHeight", 200),
            ("Size.Dialog.Status.MinimumHeight", 20),
            ("Size.Dialog.Export.Width", 560),
            ("Size.Dialog.Export.Height", 620),
            ("Size.Dialog.Export.MinimumWidth", 500),
            ("Size.Dialog.Export.MinimumHeight", 460),
            ("Size.Dialog.Import.Width", 520),
            ("Size.Dialog.Import.Height", 560),
            ("Size.Dialog.Import.MinimumWidth", 460),
            ("Size.Dialog.Import.MinimumHeight", 420),
            ("Size.Dialog.Navigation.Width", 400),
            ("Size.Dialog.Navigation.Height", 192),
            ("Size.Dialog.PhraseMove.Width", 400),
            ("Size.Dialog.PhraseMove.Height", 200),
            ("Size.Dialog.Shortcut.Width", 420),
            ("Size.Dialog.Shortcut.Height", 240),
            ("Size.StatePresenter.MinimumHeight", 84),
        });
        Assert.Equal(
            new GridLength(176),
            Assert.IsType<GridLength>(sizes["Size.Settings.Sidebar.GridLength"]));
        Assert.Equal(
            new GridLength(32),
            Assert.IsType<GridLength>(sizes["Size.TitleBar.GridLength"]));
        Assert.Equal(new GridLength(28), Assert.IsType<GridLength>(sizes["Size.Onboarding.TitleBar.GridLength"]));
        Assert.Equal(new GridLength(72), Assert.IsType<GridLength>(sizes["Size.Onboarding.Header.GridLength"]));
        Assert.Equal(new GridLength(80), Assert.IsType<GridLength>(sizes["Size.Onboarding.Footer.GridLength"]));
        Assert.Equal(new GridLength(20), Assert.IsType<GridLength>(sizes["Size.Onboarding.PracticeIndicator.Gutter.GridLength"]));
        Assert.Equal(new GridLength(28), Assert.IsType<GridLength>(sizes["Size.Onboarding.FooterStatus.GridLength"]));
        Assert.Equal(new GridLength(8), Assert.IsType<GridLength>(sizes["Size.SearchHistory.Gutter.GridLength"]));

        var motion = LoadDictionary(DesignSystemPath("Tokens", "Motion.xaml"));
        Assert.Equal(TimeSpan.FromMilliseconds(80), Assert.IsType<Duration>(motion["Motion.Duration.Fast"]).TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(140), Assert.IsType<Duration>(motion["Motion.Duration.Normal"]).TimeSpan);
        Assert.Equal(TimeSpan.FromMilliseconds(200), Assert.IsType<Duration>(motion["Motion.Duration.Slow"]).TimeSpan);

        var standard = Assert.IsType<CubicEase>(motion["Motion.Easing.Standard"]);
        var emphasized = Assert.IsType<CubicEase>(motion["Motion.Easing.Emphasized"]);
        Assert.Equal(EasingMode.EaseOut, standard.EasingMode);
        Assert.Equal(EasingMode.EaseInOut, emphasized.EasingMode);
    }

    [Theory]
    [InlineData("QuickPhraseTheme.Light.xaml", "#EDF3FA", "#B8C7D9")]
    [InlineData("QuickPhraseTheme.Dark.xaml", "#141B26", "#0B111A")]
    public void FrozenMergeOrder_ParsesAndMaterializesEveryResource(
        string themeFileName,
        string expectedWindowColor,
        string expectedShadowColor)
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var merged = LoadMergedDesignSystem(themeFileName);

            Assert.Equal(6, merged.MergedDictionaries.Count);
            Assert.True(merged.MergedDictionaries[0].Contains("Typography.FontFamily.UI"));
            Assert.True(merged.MergedDictionaries[1].Contains("Thickness.XS"));
            Assert.True(merged.MergedDictionaries[2].Contains("Radius.Control"));
            Assert.True(merged.MergedDictionaries[3].Contains("Size.Control.Default"));
            Assert.True(merged.MergedDictionaries[4].Contains("Motion.Duration.Normal"));
            Assert.True(merged.MergedDictionaries[5].Contains("Color.Brand.Primary"));

            var keys = merged.MergedDictionaries
                .SelectMany(dictionary => dictionary.Keys.Cast<object>())
                .ToArray();
            Assert.Equal(keys.Length, keys.Distinct().Count());

            foreach (var key in keys)
                Assert.NotNull(merged[key]);

            var probe = new Border { Resources = merged };
            probe.SetResourceReference(Border.BackgroundProperty, "Brush.Background.Window");
            probe.SetResourceReference(UIElement.EffectProperty, "Effect.Shadow.Elevated");

            var background = Assert.IsType<SolidColorBrush>(probe.Background);
            var shadow = Assert.IsType<DropShadowEffect>(probe.Effect);
            Assert.Equal(ParseColor(expectedWindowColor), background.Color);
            Assert.Equal(ParseColor(expectedShadowColor), shadow.Color);
        });
    }

}
