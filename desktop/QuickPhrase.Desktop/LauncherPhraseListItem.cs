using QuickPhrase.Core;

namespace QuickPhrase.Desktop;

/// <summary>
/// Launcher 对 Core 搜索结果的轻量显示适配模型。
/// 只暴露共享行模板需要的序号、标题和正文；CategoryId 及时间字段仍留在 Phrase 中，绝不进入 Launcher 视觉层。
/// </summary>
public sealed class LauncherPhraseListItem
{
    private LauncherPhraseListItem(Phrase phrase, int index)
    {
        Phrase = phrase;
        IndexInCategory = index;
        Title = phrase.Title;
        Content = phrase.Body.FirstText;
        CompositionSummary = $"{phrase.Body.SegmentCount} 段 · {phrase.Body.ImageCount} 图";
        ScopeLabel = phrase.Scope == PhraseScope.Enterprise ? "企业" : "个人";
    }

    public Phrase Phrase { get; }
    public Guid PhraseId => Phrase.Id;
    public int IndexInCategory { get; }
    public string Title { get; }
    public string Content { get; }
    public string CompositionSummary { get; }
    public string ScopeLabel { get; }

    public static LauncherPhraseListItem FromPhrase(Phrase phrase, int index) => new(phrase, index);

    public override string ToString() => $"{IndexInCategory}: {Title}";
}
