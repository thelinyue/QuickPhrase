using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 主界面、话术库和编辑器的 Design System 迁移契约。
/// 测试只扫描正式 WPF XAML，防止页面重新引入旧主题别名或视觉字面量，
/// 同时保留列表虚拟化、绑定和命令等既有交互契约。
/// </summary>
public sealed class MainLibraryEditorDesignSystemTests
{
    private static readonly Regex HexColor = new(@"#[0-9A-Fa-f]{3,8}\b", RegexOptions.Compiled);
    private static readonly Regex DirectFontSize = new(@"\bFontSize\s*=", RegexOptions.Compiled);
    private static readonly Regex DirectCornerRadius = new("\\bCornerRadius\\s*=\\s*\\\"(?!\\{StaticResource Radius\\.)", RegexOptions.Compiled);

    [Fact]
    public void MainWindow_UsesGlobalWindowAndThemeTokens()
    {
        var markup = ReadDesktopXaml("MainWindow.xaml");

        Assert.Contains("Width=\"{StaticResource Size.MainWindow.Width}\"", markup);
        Assert.Contains("Height=\"{StaticResource Size.MainWindow.Height}\"", markup);
        Assert.Contains("MinWidth=\"{StaticResource Size.MainWindow.MinimumWidth}\"", markup);
        Assert.Contains("MinHeight=\"{StaticResource Size.MainWindow.MinimumHeight}\"", markup);
        Assert.Contains("CaptionHeight=\"{StaticResource Size.TitleBar.Height}\"", markup);
        Assert.Contains("<RowDefinition Height=\"{StaticResource Size.TitleBar.GridLength}\" />", markup);
        Assert.Contains("Style=\"{StaticResource Style.Window.Shell}\"", markup);
        Assert.Contains("Style=\"{StaticResource Style.Surface.ContentRegion}\"", markup);

        Assert.DoesNotContain("WindowBackgroundBrush", markup);
        Assert.DoesNotContain("SurfacePrimaryBrush", markup);
        Assert.DoesNotContain("UiFontFamily", markup);
    }

    [Fact]
    public void EditorView_UsesSharedTextInputButtonAndSurfaceResources()
    {
        var markup = ReadDesktopXaml("Views", "EditorView.xaml");

        Assert.Contains("Style=\"{StaticResource Style.Text.Label}\"", markup);
        Assert.Contains("Style=\"{StaticResource Style.Input.Default}\"", markup);
        Assert.Contains("Style=\"{StaticResource Style.Select.Default}\"", markup);
        Assert.Contains("Style=\"{StaticResource Style.Button.Primary}\"", markup);
        Assert.Contains("Style=\"{StaticResource Style.Button.Secondary}\"", markup);
        Assert.Contains("Style=\"{StaticResource Style.Button.Danger}\"", markup);
        Assert.Contains("Style=\"{StaticResource Style.Surface.Page}\"", markup);
        Assert.Contains("Background=\"{DynamicResource Brush.Surface.Default}\"", markup);
        Assert.Contains("BorderBrush=\"{DynamicResource Brush.Border.Default}\"", markup);
        Assert.Contains("Value=\"{DynamicResource Brush.Border.Focus}\"", markup);
        Assert.Contains("Foreground=\"{DynamicResource Brush.Status.Error}\"", markup);
        Assert.Contains("MinHeight=\"{StaticResource Size.PhraseRichEditor.MinimumHeight}\"", markup);
        Assert.Contains("CornerRadius=\"{StaticResource Radius.Control}\"", markup);

        AssertNoVisualLiterals(markup, "EditorView.xaml");
        Assert.DoesNotContain("BaseTextBox", markup);
        Assert.DoesNotContain("ComboBoxFieldStyle", markup);
        Assert.DoesNotContain("PrimaryButton", markup);
        Assert.DoesNotContain("SecondaryButton", markup);
        Assert.DoesNotContain("GhostButton", markup);
        Assert.DoesNotContain("TextH2", markup);
        Assert.DoesNotContain("TextEyebrow", markup);
    }

    [Fact]
    public void LibraryView_UsesSemanticThemeResourcesAndPreservesVirtualization()
    {
        var markup = ReadDesktopXaml("Views", "LibraryView.xaml");
        var sharedRows = ReadDesktopXaml("DesignSystem", "Styles", "Lists.xaml");

        Assert.Contains("Style=\"{StaticResource Style.View.Root}\"", markup);
        Assert.Contains("Background=\"{DynamicResource Brush.Surface.Default}\"", markup);
        Assert.Contains("Foreground=\"{DynamicResource Brush.Text.Primary}\"", markup);
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource Brush.Text.OnAccent}\" />", markup);
        Assert.Contains("Background=\"{Binding Name, Converter={StaticResource CategoryBackgroundBrush}}\"", markup);
        Assert.Contains("ConverterParameter=deep", markup);
        Assert.DoesNotContain("<Setter TargetName=\"Root\" Property=\"Background\" Value=\"{DynamicResource Brush.Accent.Primary.Pressed}\" />", markup);
        Assert.Contains("Style=\"{StaticResource Style.Input.Search}\"", markup);
        Assert.DoesNotContain("Style=\"{StaticResource Style.Button.Icon}\"", sharedRows);
        Assert.Contains("Style=\"{StaticResource Style.Menu.Item.Danger}\"", markup);
        Assert.Contains("VirtualizingStackPanel.IsVirtualizing=\"True\"", markup);
        Assert.Contains("VirtualizingStackPanel.VirtualizationMode=\"Recycling\"", markup);
        Assert.Contains("ScrollViewer.CanContentScroll=\"True\"", markup);
        Assert.Contains("ItemsSource=\"{Binding VisibleItems}\"", markup);
        Assert.Contains("MouseDoubleClick=\"PhraseList_MouseDoubleClick\"", markup);
        Assert.DoesNotContain("Command=\"{Binding OpenSettingsCommand}\"", markup);
        Assert.DoesNotContain("Source=\"{StaticResource Image.Brand.AppIcon}\"", markup);
        Assert.DoesNotContain("Style.Library.SettingsButton", markup);
        Assert.DoesNotContain("Size.Library.Footer.Height", markup);

        AssertNoVisualLiterals(markup, "LibraryView.xaml");
        Assert.DoesNotContain("SearchBoxStyle", markup);
        Assert.DoesNotContain("IconButton", markup);
        Assert.DoesNotContain("DangerMenuItem", markup);
        Assert.DoesNotContain("TextLauncherTitle", markup);
        Assert.DoesNotContain("QuickPhraseBrandIcon", markup);
    }

    [Fact]
    public void ListsDictionary_ContainsSharedPhraseRowWithoutLegacyThemeAliases()
    {
        var markup = ReadDesktopXaml("DesignSystem", "Styles", "Lists.xaml");

        Assert.Contains("x:Key=\"Style.ListItem.Phrase.Library\"", markup);
        Assert.Contains("BasedOn=\"{StaticResource Style.ListItem.Phrase}\"", markup);
        Assert.Contains("x:Key=\"Template.Phrase.CompactRow\"", markup);
        Assert.Contains("Style=\"{StaticResource Style.Text.Label}\"", markup);
        Assert.Contains("Style=\"{StaticResource Style.Text.Body.Medium}\"", markup);
        Assert.DoesNotContain("Style=\"{StaticResource Style.Button.Icon}\"", markup);
        Assert.Contains("Height=\"{StaticResource Size.Phrase.Row.Compact}\"", markup);
        Assert.Contains("Value=\"{DynamicResource Brush.Surface.Hover}\"", markup);
        Assert.Contains("Value=\"{DynamicResource Brush.Surface.Selected}\"", markup);
        Assert.Contains("Value=\"{DynamicResource Brush.Border.Focus}\"", markup);

        AssertNoVisualLiterals(markup, "Lists.xaml");
        Assert.DoesNotContain("PhraseRowMinHeight", markup);
        Assert.DoesNotContain("TextMono", markup);
        Assert.DoesNotContain("TextListTitle", markup);
        Assert.DoesNotContain("TextListBody", markup);
        Assert.DoesNotContain("SendRowButton", markup);
        Assert.DoesNotContain("SelectionBackgroundBrush", markup);
        Assert.DoesNotContain("SelectionBorderBrush", markup);
        Assert.DoesNotContain("<ItemsControl", markup);
        Assert.DoesNotContain("<ListBox", markup);
        Assert.DoesNotContain("<ListView", markup);
    }

    private static void AssertNoVisualLiterals(string markup, string fileName)
    {
        Assert.False(HexColor.IsMatch(markup), $"{fileName} 不应包含 Hex 颜色字面量。");
        Assert.False(DirectFontSize.IsMatch(markup), $"{fileName} 不应直接声明 FontSize。");
        Assert.False(DirectCornerRadius.IsMatch(markup), $"{fileName} 的 CornerRadius 必须引用 Radius Token。");
    }

    private static string ReadDesktopXaml(params string[] segments)
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(new[] { root, "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray());
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}
