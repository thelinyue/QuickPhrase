using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 导入预览中的一个分类选择项。结构性祖先会随规划器自动补齐，用户只选择实际希望导入的分类。
/// </summary>
public sealed partial class PhrasePackageCategorySelectionViewModel : ObservableObject
{
    public PhrasePackageCategory Category { get; }
    public int Depth { get; }

    [ObservableProperty]
    private bool _isSelected;

    public PhrasePackageCategorySelectionViewModel(PhrasePackageCategory category, int depth, bool isSelected)
    {
        Category = category;
        Depth = depth;
        _isSelected = isSelected;
    }
}

/// <summary>
/// 话术包导入预览和确认模型。它不打开文件选择器，只负责包内分类选择、重新规划和结果统计。
/// </summary>
public sealed partial class ImportPhrasePackageViewModel : ObservableObject
{
    private readonly ICommandService _commands;
    private PhrasePackageLocalSnapshot _snapshot;

    public PhrasePackageDocument Package { get; }
    public ObservableCollection<PhrasePackageCategorySelectionViewModel> Categories { get; }
    public PhrasePackageImportPlan Plan { get; private set; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    public int NewCategoryCount => Plan.NewCategoryCount;
    public int NewPhraseCount => Plan.NewPhraseCount;
    public int SkippedDuplicateCount => Plan.SkippedDuplicateCount;
    public bool HasSelection => Package.Categories.Count == 0 || Categories.Any(item => item.IsSelected);

    public ImportPhrasePackageViewModel(
        ICommandService commands,
        PhrasePackageDocument package,
        PhrasePackageLocalSnapshot snapshot)
    {
        _commands = commands;
        Package = package;
        _snapshot = snapshot;
        Categories = new ObservableCollection<PhrasePackageCategorySelectionViewModel>(
            package.Categories
                .OrderBy(category => Depth(category.Id, package.Categories))
                .ThenBy(category => category.SortOrder)
                .ThenBy(category => category.Name, StringComparer.Ordinal)
                .Select(category => new PhrasePackageCategorySelectionViewModel(
                    category,
                    Depth(category.Id, package.Categories),
                    true)));
        Plan = BuildPlan();
        foreach (var item in Categories)
            item.PropertyChanged += Category_PropertyChanged;
    }

    /// <summary>重新读取本地分类和话术快照，避免预览期间本机数据变化导致计划过期。</summary>
    public async Task RebuildPlanAsync(CancellationToken cancellationToken = default)
    {
        _snapshot = await _commands.CapturePhrasePackageSnapshotAsync(cancellationToken);
        RebuildPlan();
    }

    /// <summary>只根据当前快照重建计划，供分类勾选变化时立即刷新预览统计。</summary>
    public void RebuildPlan()
    {
        Plan = BuildPlan();
        OnPropertyChanged(nameof(NewCategoryCount));
        OnPropertyChanged(nameof(NewPhraseCount));
        OnPropertyChanged(nameof(SkippedDuplicateCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    public string? ValidateSelection()
    {
        if (Package.Categories.Count > 0 && !Categories.Any(item => item.IsSelected))
            return "请至少选择一个分类。";
        return null;
    }

    public void SetAllSelected(bool selected)
    {
        foreach (var item in Categories) item.IsSelected = selected;
        RebuildPlan();
    }

    private PhrasePackageImportPlan BuildPlan() =>
        PhrasePackagePlanner.BuildImportPlan(
            Package,
            _snapshot,
            Categories.Where(item => item.IsSelected).Select(item => item.Category.Id).ToHashSet());

    private void Category_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhrasePackageCategorySelectionViewModel.IsSelected))
            RebuildPlan();
    }

    private static int Depth(Guid id, IReadOnlyList<PhrasePackageCategory> categories)
    {
        var byId = categories.ToDictionary(category => category.Id);
        var depth = 0;
        var cursor = id;
        var visited = new HashSet<Guid>();
        while (byId.TryGetValue(cursor, out var category) && category.ParentId.HasValue && visited.Add(cursor))
        {
            depth++;
            cursor = category.ParentId.Value;
        }
        return depth;
    }
}
