using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace QuickPhrase.Desktop.Tests.Theme;

/// <summary>
/// 校验 Design System 的生产聚合边界。颜色值、Light/Dark 对称性与 Token 类型由
/// GlobalDesignSystemTokenTests 覆盖；本类只防止入口顺序回退、旧字典重新接入或旧 Alias 复活。
/// </summary>
public class DesignTokenTests
{
    private static readonly string[] LegacyResourceKeys =
    {
        "AccentBrush", "AccentHoverBrush", "DividerBrush", "SurfaceBrush", "BorderSubtleBrush",
        "AppBackgroundBrush", "MutedTextBrush", "BrandPrimaryBrush", "FocusBrush", "DangerBrush",
        "TextBody", "TextCaption", "TextMutedBrush", "BaseTextBox", "ToggleSwitchStyle", "NavigationItem",
        "SettingsSidebarWidth", "SettingsContentMaxWidth", "SettingsPagePadding", "SettingsHeaderTitle",
        "SettingsHeaderDescription", "SettingsSectionTitle", "SettingsGroup", "SettingRow", "SettingAction",
        "PhraseListItemContainerStyle", "UiFontFamily", "ButtonHeight", "SearchBoxHeight", "PhraseRowMinHeight",
        "RadiusMedium", "RadiusXs", "QuickPhraseBrandIcon", "DialogWindow", "SurfacePrimaryBrush",
        "TextH2", "SeparatorBrush", "PhraseColor",
    };

    [Fact]
    public void App_LoadsOnlyTheFrozenProductionEntriesInOrder()
    {
        Assert.Equal(
            new[]
            {
                "Themes/Converters.xaml",
                "Themes/QuickPhraseTheme.xaml",
                "Themes/Controls.xaml",
            },
            ReadMergedSources(DesktopPath("App.xaml")));
    }

    [Fact]
    public void ThemeAggregator_LoadsTokensThenLightThemeInFrozenOrder()
    {
        Assert.Equal(
            new[]
            {
                "../DesignSystem/Tokens/Typography.xaml",
                "../DesignSystem/Tokens/Thickness.xaml",
                "../DesignSystem/Tokens/Radius.xaml",
                "../DesignSystem/Tokens/Sizes.xaml",
                "../DesignSystem/Tokens/Motion.xaml",
                "../DesignSystem/Themes/QuickPhraseTheme.Light.xaml",
            },
            ReadMergedSources(DesktopPath("Themes", "QuickPhraseTheme.xaml")));
    }

    [Fact]
    public void ControlsAggregator_LoadsStylesThenComponentsInFrozenOrder()
    {
        Assert.Equal(
            new[]
            {
                "../DesignSystem/Styles/Text.xaml",
                "../DesignSystem/Styles/Buttons.xaml",
                "../DesignSystem/Styles/Inputs.xaml",
                "../DesignSystem/Styles/SelectionControls.xaml",
                "../DesignSystem/Styles/Lists.xaml",
                "../DesignSystem/Styles/Surfaces.xaml",
                "../DesignSystem/Styles/Dialogs.xaml",
                "../DesignSystem/Components/Components.xaml",
            },
            ReadMergedSources(DesktopPath("Themes", "Controls.xaml")));
    }

    [Fact]
    public void LegacyThemeAndPhraseListDictionaries_AreRemoved()
    {
        Assert.False(File.Exists(DesktopPath("Themes", "QuickPhraseTheme.Dark.xaml")));
        Assert.False(File.Exists(DesktopPath("Themes", "PhraseListResources.xaml")));
        Assert.True(File.Exists(DesktopPath("DesignSystem", "Themes", "QuickPhraseTheme.Dark.xaml")));
    }

    [Fact]
    public void ProductionXaml_DoesNotReferenceLegacyResourceKeys()
    {
        var desktopRoot = DesktopPath();
        foreach (var file in Directory.EnumerateFiles(desktopRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(file);
            foreach (var legacyKey in LegacyResourceKeys)
            {
                Assert.DoesNotContain(
                    $"x:Key=\"{legacyKey}\"",
                    markup,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    $"Resource {legacyKey}}}",
                    markup,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    $"ResourceKey=\"{legacyKey}\"",
                    markup,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ProductionCSharp_DoesNotReferenceLegacyResourceKeys()
    {
        var desktopRoot = DesktopPath();
        var buildDirectorySegments = new[]
        {
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        };

        foreach (var file in Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (buildDirectorySegments.Any(segment => file.Contains(segment, StringComparison.OrdinalIgnoreCase)))
                continue;

            var source = File.ReadAllText(file);
            foreach (var legacyKey in LegacyResourceKeys)
            {
                Assert.DoesNotContain(
                    $"\"{legacyKey}",
                    source,
                    StringComparison.Ordinal);
            }
        }
    }
    [Fact]
    public void ProductionXaml_UsesTokensForVisualMetrics()
    {
        var metricAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Margin", "Padding", "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight",
            "BorderThickness", "ResizeBorderThickness", "FontSize", "CornerRadius",
        };
        var violations = new List<string>();
        var designSystemTokens = Path.GetFullPath(DesktopPath("DesignSystem", "Tokens")) + Path.DirectorySeparatorChar;
        var designSystemThemes = Path.GetFullPath(DesktopPath("DesignSystem", "Themes")) + Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(DesktopPath(), "*.xaml", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(file);
            if (fullPath.StartsWith(designSystemTokens, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(designSystemThemes, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var attribute in document.Descendants().Attributes())
            {
                if (!metricAttributes.Contains(attribute.Name.LocalName))
                    continue;

                var value = attribute.Value.Trim();
                if (value is "0" or "Auto" or "*" || value.StartsWith("{", StringComparison.Ordinal))
                    continue;

                if (!char.IsDigit(value[0]) && value[0] != '-')
                    continue;

                var line = (attribute.Parent as IXmlLineInfo)?.LineNumber ?? 0;
                violations.Add($"{Path.GetRelativePath(DesktopPath(), file)}:{line} {attribute.Name.LocalName}=\"{value}\"");
            }
        }

        Assert.True(
            violations.Count == 0,
            "生产 XAML 的视觉尺寸和间距必须引用 Token：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void ProductionXaml_DoesNotContainLegacyPurpleBrandColors()
    {
        var legacyColors = new[]
        {
            "#6D45F5", "#5731E8", "#F5F3FF", "#7858FF", "#4628C4",
            "#9974FF", "#3D356B",
        };

        foreach (var file in Directory.EnumerateFiles(DesktopPath(), "*.xaml", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(file);
            foreach (var color in legacyColors)
                Assert.DoesNotContain(color, markup, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<string> ReadMergedSources(string path)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var document = XDocument.Load(path, LoadOptions.SetLineInfo);
        return document
            .Descendants(presentation + "ResourceDictionary")
            .Where(element => element.Attribute("Source") is not null)
            .Select(element => element.Attribute("Source")!.Value)
            .ToArray();
    }

    private static string DesktopPath(params string[] segments)
    {
        var parts = new List<string>
        {
            FindRepoRoot(),
            "desktop",
            "QuickPhrase.Desktop",
        };
        parts.AddRange(segments);
        return Path.Combine(parts.ToArray());
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }
}
