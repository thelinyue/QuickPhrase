using System;
using System.IO;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 普通输入框共享模板的布局回归约束：文本起点只由 Padding 决定，模板不得预留不存在的前置图标列。
/// </summary>
public sealed class TextBoxLayoutTests
{
    [Fact]
    public void SharedTextBoxUsesPaddingForContentHostWithoutLeadingIconColumn()
    {
        var inputs = ReadInputs();
        var style = ExtractStyle(inputs, "Style.Input.Base");

        Assert.Contains("<Style x:Key=\"Style.Input.Default\" TargetType=\"TextBox\" BasedOn=\"{StaticResource Style.Input.Base}\" />", inputs, StringComparison.Ordinal);

        Assert.Contains("<Setter Property=\"Padding\" Value=\"{StaticResource Thickness.Control.Input}\" />", style);
        Assert.Contains("<ScrollViewer x:Name=\"PART_ContentHost\"", style);
        Assert.Contains("Margin=\"{TemplateBinding Padding}\"", style);
        Assert.DoesNotContain("Margin=\"28,0,16,0\"", style);
        Assert.DoesNotContain("<Path", style, StringComparison.Ordinal);
        Assert.Contains(@"<Condition Property=""IsKeyboardFocused"" Value=""True"" />", style, StringComparison.Ordinal);
        Assert.Contains(@"x:Name=""FocusRing""", style, StringComparison.Ordinal);
    }

    [Fact]
    public void CategoryEditorUsesSharedBaseTextBoxStyle()
    {
        var root = FindRepositoryRoot();
        var dialog = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "Dialogs", "CategoryDialog.xaml"));

        Assert.Contains("<TextBox x:Name=\"NameBox\" Grid.Row=\"1\" Style=\"{StaticResource Style.Input.Default}\"", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void OnboardingRegularFieldsUseImplicitSharedTextBoxStyle()
    {
        var root = FindRepositoryRoot();
        var onboarding = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "OnboardingWindow.xaml"));

        Assert.Contains("<TextBox Text=\"{Binding CategoryName, UpdateSourceTrigger=PropertyChanged}\" Style=\"{StaticResource Style.Input.Default}\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("<TextBox Text=\"{Binding PhraseTitle, UpdateSourceTrigger=PropertyChanged}\" Style=\"{StaticResource Style.Input.Default}\"", onboarding, StringComparison.Ordinal);
        Assert.Contains("Padding=\"{StaticResource Thickness.MD}\"", onboarding, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorUsesOneSemanticPhraseRichTextInput()
    {
        var root = FindRepositoryRoot();
        var editor = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "EditorView.xaml"));
        var component = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "DesignSystem", "Components", "PhraseRichTextEditor.xaml"));

        Assert.Contains("<components:PhraseRichTextEditor x:Name=\"RichEditor\"", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("SegmentList", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding Segments}\"", editor, StringComparison.Ordinal);
        Assert.Contains("<RichTextBox x:Name=\"EditorBox\"", component, StringComparison.Ordinal);
        Assert.Contains("Padding=\"{StaticResource Thickness.Control.Input.Multiline}\"", component, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"话术图文内容编辑区\"", component, StringComparison.Ordinal);
    }

    [Fact]
    public void PhraseRichEditorPastePrefersImageAndOnlyAcceptsUnicodePlainText()
    {
        var root = FindRepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "DesignSystem", "Components", "PhraseRichTextEditor.xaml.cs"));

        Assert.Contains("DataObject.AddPastingHandler", code, StringComparison.Ordinal);
        Assert.True(code.IndexOf("DataFormats.Bitmap", StringComparison.Ordinal) < code.IndexOf("DataFormats.UnicodeText", StringComparison.Ordinal));
        Assert.DoesNotContain("DataFormats.Rtf", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DataFormats.Html", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DataFormats.Xaml", code, StringComparison.Ordinal);
        Assert.Contains("EditorBox.Selection.Text = text", code, StringComparison.Ordinal);
    }

    private static string ReadInputs()
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "DesignSystem", "Styles", "Inputs.xaml"));
    }

    private static string ExtractStyle(string controls, string key)
    {
        var start = controls.IndexOf($"<Style x:Key=\"{key}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {key}。" );
        var end = controls.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{key} 缺少结束标记。" );
        return controls[start..(end + "</Style>".Length)];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}
