using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>导出选择中的分类项，保持包导出 UI 与 Core 选择契约隔离。</summary>
public sealed partial class PhrasePackageExportCategorySelectionViewModel : ObservableObject
{
    public Category Category { get; }
    public int Depth { get; }

    [ObservableProperty]
    private bool _isSelected;

    public PhrasePackageExportCategorySelectionViewModel(Category category, int depth)
    {
        Category = category;
        Depth = depth;
    }
}

/// <summary>导出选择中的话术项，只展示标题和所属分类，不修改话术内容。</summary>
public sealed partial class PhrasePackageExportPhraseSelectionViewModel : ObservableObject
{
    public Phrase Phrase { get; }
    public string CategoryName { get; }

    [ObservableProperty]
    private bool _isSelected;

    public PhrasePackageExportPhraseSelectionViewModel(Phrase phrase, string categoryName)
    {
        Phrase = phrase;
        CategoryName = categoryName;
    }
}

/// <summary>
/// 导出范围和选择模型。Core 负责真正构造导出闭包，本类只维护 UI 选择和默认包名称。
/// </summary>
public sealed partial class ExportPhrasePackageViewModel : ObservableObject
{
    public PhrasePackageLocalSnapshot Snapshot { get; }
    public ObservableCollection<PhrasePackageExportCategorySelectionViewModel> Categories { get; }
    public ObservableCollection<PhrasePackageExportPhraseSelectionViewModel> Phrases { get; }
    public IReadOnlyList<PhrasePackageExportScope> Scopes { get; } = Enum.GetValues<PhrasePackageExportScope>();

    [ObservableProperty]
    private PhrasePackageExportScope _scope = PhrasePackageExportScope.All;

    [ObservableProperty]
    private string _name = "我的话术包";

    [ObservableProperty]
    private string? _errorMessage;

    public ExportPhrasePackageViewModel(PhrasePackageLocalSnapshot snapshot)
    {
        Snapshot = snapshot;
        var byId = snapshot.Categories.ToDictionary(category => category.Id);
        Categories = new ObservableCollection<PhrasePackageExportCategorySelectionViewModel>(
            snapshot.Categories
                .OrderBy(category => Depth(category.Id, byId))
                .ThenBy(category => category.SortOrder)
                .ThenBy(category => category.Name, StringComparer.Ordinal)
                .Select(category => new PhrasePackageExportCategorySelectionViewModel(category, Depth(category.Id, byId))));
        Phrases = new ObservableCollection<PhrasePackageExportPhraseSelectionViewModel>(
            snapshot.Phrases
                .OrderBy(phrase => phrase.SortOrder)
                .ThenBy(phrase => phrase.Title, StringComparer.Ordinal)
                .Select(phrase => new PhrasePackageExportPhraseSelectionViewModel(
                    phrase,
                    byId.TryGetValue(phrase.CategoryId, out var category) ? category.Name : "未分类")));
    }

    public string? ValidateSelection()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "请输入话术包名称。";
        if (Name.Trim().Length > PhrasePackageFormat.MaxNameLength) return "话术包名称不能超过 80 个字。";
        if (Scope == PhrasePackageExportScope.Categories && !Categories.Any(item => item.IsSelected)) return "请至少选择一个分类。";
        if (Scope == PhrasePackageExportScope.Phrases && !Phrases.Any(item => item.IsSelected)) return "请至少选择一条话术。";
        return null;
    }

    public PhrasePackageDocument BuildDocument(DateTimeOffset? createdAt = null)
    {
        ErrorMessage = ValidateSelection();
        if (ErrorMessage is not null) throw new InvalidOperationException(ErrorMessage);

        var selection = new PhrasePackageExportSelection(
            Scope,
            Name.Trim(),
            Categories.Where(item => item.IsSelected).Select(item => item.Category.Id),
            Phrases.Where(item => item.IsSelected).Select(item => item.Phrase.Id));
        return PhrasePackagePlanner.BuildExportDocument(Snapshot, selection, createdAt ?? DateTimeOffset.UtcNow);
    }

    public void SetAllSelected(bool selected)
    {
        if (Scope == PhrasePackageExportScope.Categories)
            foreach (var item in Categories) item.IsSelected = selected;
        else if (Scope == PhrasePackageExportScope.Phrases)
            foreach (var item in Phrases) item.IsSelected = selected;
    }

    private static int Depth(Guid id, IReadOnlyDictionary<Guid, Category> categories)
    {
        var depth = 0;
        var cursor = id;
        var visited = new HashSet<Guid>();
        while (categories.TryGetValue(cursor, out var category) && category.ParentId.HasValue && visited.Add(cursor))
        {
            depth++;
            cursor = category.ParentId.Value;
        }
        return depth;
    }
}
