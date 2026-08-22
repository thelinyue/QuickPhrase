using System;
using System.IO;
using QuickPhrase.Core;
using QuickPhrase.Desktop.ViewModels;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 约束共享话术行把话术颜色应用到标题文字，而不是只保留在编辑器色板中。
/// </summary>
public sealed class PhraseTitleColorMarkupTests
{
    [Fact]
    public void CompactPhraseRow_BindsTitleForegroundToPhraseColor()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "desktop",
            "QuickPhrase.Desktop",
            "DesignSystem",
            "Styles",
            "Lists.xaml"));
        var title = Slice(markup, "TextWrapping=\"NoWrap\"", "Text=\"{Binding Title}\" />");

        Assert.Contains(
            "Foreground=\"{Binding ColorKey, Converter={StaticResource ColorKeyToBrush}}\"",
            title,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhraseItem_ApplyPublishesUpdatedColorKey()
    {
        var categoryId = Guid.NewGuid();
        var original = CreatePhrase(categoryId, "default");
        var updated = CreatePhrase(categoryId, "blue");
        var item = new PhraseItemViewModel(original, "分类");
        var changed = false;
        item.PropertyChanged += (_, args) => changed |= args.PropertyName == nameof(PhraseItemViewModel.ColorKey);

        item.Apply(updated, "分类");

        Assert.True(changed);
        Assert.Equal("blue", item.ColorKey);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"未找到起始标记：{start}");

        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"未找到结束标记：{end}");
        return source[startIndex..(endIndex + end.Length)];
    }

    private static Phrase CreatePhrase(Guid categoryId, string colorKey)
        => new(
            Guid.NewGuid(),
            "标题",
            PhraseBody.FromText("内容"),
            categoryId,
            ShortcutMode.None,
            null,
            0,
            null,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            colorKey);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }
}



