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
    public void EditorBodyUsesSemanticMultilineInputTokens()
    {
        var root = FindRepositoryRoot();
        var editor = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "EditorView.xaml"));

        Assert.Contains("AcceptsReturn=\"True\"", editor, StringComparison.Ordinal);
        Assert.Contains("Height=\"{StaticResource Size.Editor.Body.Height}\"", editor, StringComparison.Ordinal);
        Assert.Contains("Padding=\"{StaticResource Thickness.Control.Input.Multiline}\"", editor, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource Style.Input.Default}\"", editor, StringComparison.Ordinal);
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
