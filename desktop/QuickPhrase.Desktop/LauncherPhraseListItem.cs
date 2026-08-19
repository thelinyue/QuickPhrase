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
        Content = phrase.Content;
    }

    public Phrase Phrase { get; }
    public Guid PhraseId => Phrase.Id;
    public int IndexInCategory { get; }
    public string Title { get; }
    public string Content { get; }

    public static LauncherPhraseListItem FromPhrase(Phrase phrase, int index) => new(phrase, index);

    public override string ToString() => $"{IndexInCategory}: {Title}";
}
