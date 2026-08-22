using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 约束话术库与 Launcher 复用的紧凑话术行几何，防止多行正文重新撑开搜索结果。
/// </summary>
public sealed class LibraryCompactListLayoutTests
{
    [Fact]
    public void SubCategoryHeader_UsesCompactNodeGeometry()
    {
        var markup = ReadDesktopFile("Views", "LibraryView.xaml");
        var style = Slice(markup, "<Style x:Key=\"Style.Library.SubHeaderButton\"", "</Style>");
        var template = Slice(markup, "<DataTemplate x:Key=\"SubHeaderTemplate\"", "</DataTemplate>");

        Assert.Contains("<sys:Double x:Key=\"Size.Library.SubHeader.Height\">24</sys:Double>", markup);
        Assert.Contains("<sys:Double x:Key=\"Size.Library.SubHeader.ArrowWidth\">16</sys:Double>", markup);
        Assert.Contains("<GridLength x:Key=\"Size.Library.SubHeader.ArrowColumn\">16</GridLength>", markup);
        Assert.Contains("<GridLength x:Key=\"Size.Library.CompactList.GapColumn\">0</GridLength>", markup);
        Assert.Contains("<Setter Property=\"Height\" Value=\"{StaticResource Size.Library.SubHeader.Height}\" />", style);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"{StaticResource Thickness.None}\" />", style);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"{StaticResource Thickness.None}\" />", style);
        Assert.Contains("<ContentPresenter Margin=\"{StaticResource Thickness.None}\"", style);
        Assert.Contains("<Setter Property=\"Width\" Value=\"{StaticResource Size.Library.SubHeader.ArrowWidth}\" />", markup);
        Assert.Contains("<ColumnDefinition Width=\"{StaticResource Size.Library.SubHeader.ArrowColumn}\" />", template);
        Assert.Contains("<ColumnDefinition Width=\"{StaticResource Size.Library.CompactList.GapColumn}\" />", template);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", template);
    }

    [Fact]
    public void PhraseRow_HidesIndexAndKeepsCompactTitleContentColumns()
    {
        var markup = ReadDesktopFile("Views", "LibraryView.xaml");
        var sharedRows = ReadDesktopFile("DesignSystem", "Styles", "Lists.xaml");
        var sizes = ReadDesktopFile("DesignSystem", "Tokens", "Sizes.xaml");
        var thickness = ReadDesktopFile("DesignSystem", "Tokens", "Thickness.xaml");
        var template = Slice(sharedRows, "<DataTemplate x:Key=\"Template.Phrase.CompactRow\"", "</DataTemplate>");
        var phraseList = Slice(markup, "<ListBox x:Name=\"PhraseList\"", "</ListBox>");
        var itemStyle = Slice(phraseList, "<ListBox.ItemContainerStyle>", "</ListBox.ItemContainerStyle>");
        Assert.Contains("<sys:Double x:Key=\"Size.Phrase.Row.Compact\">28</sys:Double>", sizes);
        Assert.Contains("<Thickness x:Key=\"Thickness.Phrase.Row.CompactHorizontal\">4,0</Thickness>", thickness);
        Assert.Contains("Height=\"{StaticResource Size.Phrase.Row.Compact}\"", template);
        Assert.Contains("<ColumnDefinition Width=\"{StaticResource Size.Phrase.Row.GapColumn}\" />", template);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", template);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", template);
        Assert.Equal(1, template.Split("<ColumnDefinition Width=\"{StaticResource Size.Phrase.Row.GapColumn}\" />", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("IndexText", template);
        Assert.DoesNotContain("IndexInCategory", template);
        Assert.Contains("Background=\"Transparent\"", template);
        Assert.DoesNotContain("SendBtn", template);
        Assert.DoesNotContain("PhraseListActions", template);
        Assert.DoesNotContain("<ControlTemplate.Triggers>", template);
        Assert.Contains("BasedOn=\"{StaticResource Style.ListItem.Phrase.Compact}\"", itemStyle);
        Assert.Contains("x:Key=\"Style.ListItem.Phrase.Compact\"", sharedRows);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"{StaticResource Thickness.Phrase.Row.CompactHorizontal}\" />", sharedRows);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"{StaticResource Thickness.None}\" />", itemStyle);
        Assert.DoesNotContain("IsSubCategory", itemStyle);
        Assert.DoesNotContain("DataContext.SearchQuery", itemStyle);
        Assert.DoesNotContain("Thickness.Gap.Inline.Before.LG", itemStyle);
    }

    [Fact]
    public void PhraseLibraryRows_UseImmediateFullRowHoverAndSelectionColors()
    {
        var markup = ReadDesktopFile("DesignSystem", "Styles", "Lists.xaml");
        var style = Slice(markup, "<Style x:Key=\"Style.ListItem.Phrase.Library\"", "</Style>");

        Assert.Contains("<Border x:Name=\"Root\"", style);
        Assert.Contains("Background=\"{TemplateBinding Background}\"", style);
        Assert.Contains("<Setter TargetName=\"Root\" Property=\"Background\" Value=\"{DynamicResource Brush.Surface.Hover}\" />", style);
        Assert.Contains("<Setter TargetName=\"Root\" Property=\"Background\" Value=\"{DynamicResource Brush.Surface.Selected}\" />", style);
        Assert.DoesNotContain("<Storyboard", style);
        Assert.DoesNotContain("ColorAnimation", style);
        Assert.DoesNotContain("DoubleAnimation", style);
    }

    [Fact]
    public void CategoryLabels_UseColorFillAndSpecifiedTextHierarchy()
    {
        var markup = ReadDesktopFile("Views", "LibraryView.xaml");
        var topCategoryStyle = Slice(markup, "<Style x:Key=\"Style.Library.CategoryChip\"", "</Style>");
        var subCategoryTemplate = Slice(markup, "<DataTemplate x:Key=\"SubHeaderTemplate\"", "</DataTemplate>");

        Assert.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource Brush.Text.OnAccent}\" />", topCategoryStyle);
        Assert.Contains("x:Name=\"DefaultCategoryBackground\"", topCategoryStyle);
        Assert.Contains("Background=\"{Binding Name, Converter={StaticResource CategoryBackgroundBrush}}\"", topCategoryStyle);
        Assert.Contains("x:Name=\"SelectedCategoryBackground\"", topCategoryStyle);
        Assert.Contains("ConverterParameter=deep", topCategoryStyle);
        Assert.Contains("<Setter TargetName=\"SelectedCategoryBackground\" Property=\"Visibility\" Value=\"Visible\" />", topCategoryStyle);
        Assert.DoesNotContain("<Trigger Property=\"IsMouseOver\" Value=\"True\">", topCategoryStyle);
        Assert.DoesNotContain("Brush.Accent.Primary", topCategoryStyle);
        Assert.Contains("Foreground=\"{DynamicResource Brush.Text.Primary}\"", subCategoryTemplate);
        Assert.Contains("FontWeight=\"Bold\"", subCategoryTemplate);
    }

    [Fact]
    public void Library_RemovesResponsiveTitleWidthWhileLauncherUsesFixedColumns()
    {
        var library = ReadDesktopFile("Views", "LibraryView.xaml");
        var libraryCode = ReadDesktopFile("Views", "LibraryView.xaml.cs");
        var viewModel = ReadDesktopFile("ViewModels", "PhraseLibraryViewModel.cs");
        var launcher = ReadDesktopFile("LauncherWindow.xaml");
        var launcherCode = ReadDesktopFile("LauncherWindow.xaml.cs");
        var sharedRows = ReadDesktopFile("DesignSystem", "Styles", "Lists.xaml");

        Assert.Contains("Template.Phrase.CompactRow", library);
        Assert.Contains("LauncherPhraseTemplate", launcher);
        Assert.DoesNotContain("Template.Library.CompactPhraseRow", library);
        Assert.DoesNotContain("local:PhraseListActions.TitleColumnWidth", library);
        Assert.DoesNotContain("local:PhraseListActions.TitleColumnWidth", launcher);
        Assert.DoesNotContain("TitleColumnWidth", launcherCode);
        Assert.DoesNotContain("_viewModel.TitleColumnWidth", libraryCode);
        Assert.DoesNotContain("_titleColumnWidth", viewModel);
        Assert.DoesNotContain("using System.Windows;", viewModel);
        Assert.DoesNotContain("Template.Phrase.Row", sharedRows);
        Assert.Contains("Size.Phrase.Row.Compact", sharedRows);
        Assert.Contains("Size.Launcher.TitleColumn.GridLength", launcher);
        Assert.Contains("Size.Launcher.CategoryColumn.GridLength", launcher);
        Assert.DoesNotContain("PhraseListActions.TitleColumnWidth", launcher);
        Assert.Contains("Size.Phrase.Row.Minimum", sharedRows);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"未找到起始标记：{start}");

        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"未找到结束标记：{end}");
        return source[startIndex..(endIndex + end.Length)];
    }

    private static string ReadDesktopFile(params string[] segments)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root, "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}
