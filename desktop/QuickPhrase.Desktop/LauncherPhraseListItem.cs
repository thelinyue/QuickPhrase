using QuickPhrase.Core;

namespace QuickPhrase.Desktop;

/// <summary>
/// Launcher 对 Core 搜索结果的轻量显示适配模型。
/// 分类路径由 Core 搜索快照随结果返回，避免 Launcher 为显示分类而访问持久化层。
/// </summary>
public sealed class LauncherPhraseListItem
{
    private LauncherPhraseListItem(Phrase phrase, int index, string categoryPath)
    {
        Phrase = phrase;
        IndexInCategory = index;
        Title = phrase.Title;
        Content = phrase.Body.FirstText;
        CompositionSummary = $"{phrase.Body.SegmentCount} 段 · {phrase.Body.ImageCount} 图";
        ScopeLabel = phrase.Scope == PhraseScope.Enterprise ? "企业" : "个人";
        CategoryPath = string.IsNullOrWhiteSpace(categoryPath) ? "未分类" : categoryPath;
    }

    public Phrase Phrase { get; }
    public Guid PhraseId => Phrase.Id;
    public int IndexInCategory { get; }
    public string Title { get; }
    public string Content { get; }
    public string CompositionSummary { get; }
    public string ScopeLabel { get; }
    public string CategoryPath { get; }

    public static LauncherPhraseListItem FromSearchResult(SearchResult result, int index) => new(result.Phrase, index, result.CategoryPath);

    public static LauncherPhraseListItem FromPhrase(Phrase phrase, int index) => new(phrase, index, "未分类");

    public override string ToString() => $"{IndexInCategory}: {Title}";
}
