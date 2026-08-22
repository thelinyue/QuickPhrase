using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 约束话术库的紧凑列表几何，防止后续改动重新引入固定标题列、分类留白或影响 Launcher 的共享行模板。
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
        Assert.Contains("<GridLength x:Key=\"Size.Library.CompactList.GapColumn\">4</GridLength>", markup);
        Assert.Contains("<Setter Property=\"Height\" Value=\"{StaticResource Size.Library.SubHeader.Height}\" />", style);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"{StaticResource Thickness.None}\" />", style);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"{StaticResource Thickness.None}\" />", style);
        Assert.Contains("<ContentPresenter Margin=\"{StaticResource Thickness.Library.CompactHorizontal}\"", style);
        Assert.Contains("<Setter Property=\"Width\" Value=\"{StaticResource Size.Library.SubHeader.ArrowWidth}\" />", markup);
        Assert.Contains("<ColumnDefinition Width=\"{StaticResource Size.Library.SubHeader.ArrowColumn}\" />", template);
        Assert.Contains("<ColumnDefinition Width=\"{StaticResource Size.Library.CompactList.GapColumn}\" />", template);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", template);
    }

    [Fact]
    public void PhraseRow_UsesSeparateLeftSendActionAndKeepsIndexVisibleOnHover()
    {
        var markup = ReadDesktopFile("Views", "LibraryView.xaml");
        var template = Slice(markup, "<DataTemplate x:Key=\"Template.Library.CompactPhraseRow\"", "</DataTemplate>");
        var phraseList = Slice(markup, "<ListBox x:Name=\"PhraseList\"", "</ListBox>");
        var itemStyle = Slice(phraseList, "<ListBox.ItemContainerStyle>", "</ListBox.ItemContainerStyle>");

        Assert.Contains("<sys:Double x:Key=\"Size.Library.CompactPhrase.Height\">28</sys:Double>", markup);
        Assert.Contains("<GridLength x:Key=\"Size.Library.CompactPhrase.SendActionColumn\">24</GridLength>", markup);
        Assert.Contains("<GridLength x:Key=\"Size.Library.CompactPhrase.IndexColumn\">24</GridLength>", markup);
        Assert.Contains("<Thickness x:Key=\"Thickness.Library.CompactHorizontal\">8,0</Thickness>", markup);
        Assert.Contains("Height=\"{StaticResource Size.Library.CompactPhrase.Height}\"", template);
        Assert.Contains("<ColumnDefinition Width=\"{StaticResource Size.Library.CompactPhrase.SendActionColumn}\" />", template);
        Assert.Contains("<ColumnDefinition Width=\"{StaticResource Size.Library.CompactPhrase.IndexColumn}\" />", template);
        Assert.Contains("<ColumnDefinition Width=\"{StaticResource Size.Library.CompactList.GapColumn}\" />", template);
        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", template);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", template);
        Assert.Equal(3, template.Split("<ColumnDefinition Width=\"{StaticResource Size.Library.CompactList.GapColumn}\" />", StringSplitOptions.None).Length - 1);
        var sendButton = Slice(template, "<Button x:Name=\"SendBtn\"", "/>");
        var indexText = Slice(template, "<TextBlock x:Name=\"IndexText\"", "/>");

        Assert.Contains("Grid.Column=\"0\"", sendButton);
        Assert.Contains("Content=\"←\"", sendButton);
        Assert.Contains("Path=(local:PhraseListActions.SendCommand)", sendButton);
        Assert.Contains("Grid.Column=\"2\"", indexText);
        Assert.True(template.IndexOf("<Button x:Name=\"SendBtn\"", StringComparison.Ordinal) < template.IndexOf("<TextBlock x:Name=\"IndexText\"", StringComparison.Ordinal));
        Assert.DoesNotContain("<Setter TargetName=\"IndexText\" Property=\"Visibility\" Value=\"Collapsed\" />", template);
        Assert.Contains("<Condition Binding=\"{Binding IsMouseOver, ElementName=RowRoot}\" Value=\"True\" />", template);
        Assert.Contains("<Setter TargetName=\"SendBtn\" Property=\"Opacity\" Value=\"1\" />", template);
        Assert.Contains("<Setter TargetName=\"SendBtn\" Property=\"IsHitTestVisible\" Value=\"True\" />", template);

        Assert.Contains("<Setter Property=\"Height\" Value=\"{StaticResource Size.Library.CompactPhrase.Height}\" />", itemStyle);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"{StaticResource Size.Library.CompactPhrase.Height}\" />", itemStyle);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"{StaticResource Thickness.Library.CompactHorizontal}\" />", itemStyle);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"{StaticResource Thickness.None}\" />", itemStyle);
        Assert.Contains("<Condition Binding=\"{Binding IsSubCategory}\" Value=\"True\" />", itemStyle);
        Assert.Contains("Binding DataContext.SearchQuery", itemStyle);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"{StaticResource Thickness.Gap.Inline.Before.LG}\" />", itemStyle);
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
        var sharedRows = ReadDesktopFile("DesignSystem", "Styles", "Lists.xaml");

        Assert.Contains("Template.Library.CompactPhraseRow", library);
        Assert.DoesNotContain("local:PhraseListActions.TitleColumnWidth", library);
        Assert.DoesNotContain("_viewModel.TitleColumnWidth", libraryCode);
        Assert.DoesNotContain("_titleColumnWidth", viewModel);
        Assert.DoesNotContain("using System.Windows;", viewModel);

        Assert.Contains("LauncherPhraseTemplate", launcher);
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
