using CommunityToolkit.Mvvm.ComponentModel;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 单条话术的显示模型，封装不可变 Phrase 领域记录，暴露 PhraseRowTemplate 所需的绑定字段。
/// Owner 指向所在 LibraryViewModel，使行内 ContextMenu 能直接调用库级命令。
/// </summary>
public partial class PhraseItemViewModel : ObservableObject
{
    private Phrase _model;

    public PhraseItemViewModel(Phrase model, string? categoryName)
    {
        _model = model;
        Title = model.Title;
        Content = model.Content;
        Snippet = MakeSnippet(model.Content);
        CategoryName = categoryName;
        Shortcut = model.Shortcut?.Display;
        ColorKey = model.ColorKey;
        SortOrder = model.SortOrder;
    }

    public Guid Id => _model.Id;
    public long Version => _model.Version;
    public Guid CategoryId => _model.CategoryId;
    public ShortcutMode ShortcutMode => _model.ShortcutMode;
    public ShortcutValue? ShortcutValue => _model.Shortcut;
    public PhraseLibraryViewModel? Owner { get; set; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _content;
    [ObservableProperty] private string _snippet;
    [ObservableProperty] private string? _categoryName;
    [ObservableProperty] private string? _shortcut;
    [ObservableProperty] private string _colorKey;

    /// <summary>持久化的拖拽排序顺序（由仓储层排序后回写，与分类 SortOrder 对齐）。</summary>
    public int SortOrder { get; set; }

    public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);

    /// <summary>在当前一级分类视图内的序号（从 1 起），用于话术行模板的序号列。</summary>
    public int IndexInCategory { get; set; }

    /// <summary>是否归属二级分类（用于触发额外左缩进 28px）。</summary>
    public bool IsSubCategory { get; set; }

    /// <summary>用服务端返回的最新 Phrase 刷新所有可观察属性（编辑保存、移动等操作后调用）。</summary>
    public void Apply(Phrase model, string? categoryName)
    {
        _model = model;
        Title = model.Title;
        Content = model.Content;
        Snippet = MakeSnippet(model.Content);
        CategoryName = categoryName;
        Shortcut = model.Shortcut?.Display;
        ColorKey = model.ColorKey;
        SortOrder = model.SortOrder;
    }

    /// <summary>回放底层领域记录，用于构造 UpdatePhraseCommand 等写操作。</summary>
    public Phrase ToPhrase() => _model;

    private static string MakeSnippet(string content)
    {
        const int max = 90;
        var trimmed = content.Replace("\r", " ").Replace("\n", " ").Trim();
        return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max) + "…";
    }
}
