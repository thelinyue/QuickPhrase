using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 话术库视图模型：负责搜索、列表选择、插入、复制、删除和打开编辑器。
/// 所有数据访问都经由 ICommandService（→ Core 契约 → Platform.Windows 实现），不直接碰 SQLite。
/// </summary>
public partial class PhraseLibraryViewModel : ObservableObject
{
    private static readonly Guid EnterpriseRootId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    private readonly ICommandService _commands;
    private Dictionary<Guid, string> _categoryNames = new();

    public PhraseLibraryViewModel(ICommandService commands)
    {
        _commands = commands;
    }

    [ObservableProperty] private string _searchQuery = "";
    // 搜索请求版本：输入即搜会允许多个内存搜索并行完成，只接受最后一次查询的结果。
    private long _searchRequestVersion;

    // 搜索输入首尾清理，并在文本变化后立即触发现有搜索命令。
    // 版本号由 Search() 校验，避免快速连续输入时旧结果覆盖新结果。
    partial void OnSearchQueryChanged(string? oldValue, string newValue)
    {
        var trimmed = newValue.Trim();
        if (trimmed != newValue)
        {
            SearchQuery = trimmed;
            return;
        }

        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDescription));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsSearchResultVisible));
        OnPropertyChanged(nameof(IsSearchResultEmpty));
        HasSearchError = false;
        OnPropertyChanged(nameof(IsSearchResultEmpty));
        _ = SearchCommand.ExecuteAsync(null);
    }
    [ObservableProperty] private ObservableCollection<PhraseItemViewModel> _phrases = new();
    [ObservableProperty] private PhraseItemViewModel? _selectedPhrase;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasError;

    /// <summary>
    /// 搜索结果独立于话术库主列表保存，搜索时不会破坏当前分类浏览状态。
    /// </summary>
    [ObservableProperty] private ObservableCollection<PhraseItemViewModel> _searchResults = new();
    [ObservableProperty] private bool _isSearchBusy;
    [ObservableProperty] private bool _hasSearchError;

    /// <summary>共享状态呈现器使用的派生状态：没有加载中/错误且当前列表为空。</summary>
    public bool IsEmpty => !IsBusy && !HasError && VisibleItems.Count == 0;
    public string EmptyStateTitle => string.IsNullOrWhiteSpace(SearchQuery) ? "暂无话术" : "没有找到对应关键词";
    public string EmptyStateDescription => string.IsNullOrWhiteSpace(SearchQuery) ? "创建第一条话术，之后可在闪念中快速插入。" : "换个关键词试试，或清空搜索条件。";
    public bool IsSearchResultVisible => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool IsSearchResultEmpty => IsSearchResultVisible && !IsSearchBusy && !HasSearchError && SearchResults.Count == 0;

    // 分类（一级 chips 横向 + 二级内联嵌套）
    [ObservableProperty] private ObservableCollection<CategoryItem> _categories = new();
    // 一级分类（ParentId == null），带 IsSelected 标记，供顶部 chips 绑定
    [ObservableProperty] private ObservableCollection<CategoryItem> _topCategories = new();
    // 当前选中的一级分类；null 表示未选中具体一级
    [ObservableProperty] private Guid? _selectedCategoryId;
    // 一级分类可见性筛选（true = 选中一级后只显示该一级内的话术）
    [ObservableProperty] private bool _isCategoryFilterActive;
    // 扁平化列表：SubHeaderItem（二级标题条）与 PhraseItemViewModel（话术行）混合，
    // 与原型"一级直挂话术 + 二级内联区"的嵌套树呈现方式一致。
    [ObservableProperty] private ObservableCollection<object> _visibleItems = new();

    public event EventHandler<PhraseItemViewModel>? EditRequested;
    public event EventHandler? NewRequested;
    public event EventHandler<PhraseItemViewModel>? MoveRequested;
    public event EventHandler? NewCategoryRequested;
    public event EventHandler<CategoryItem>? RenameCategoryRequested;
    public event EventHandler<CategoryItem>? DeleteCategoryRequested;
    public event EventHandler<CategoryItem>? NewSubCategoryRequested;
    public event EventHandler<CategoryItem>? NewPhraseInCategoryRequested;
    public event EventHandler? OpenSettingsRequested;

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        HasError = false;
        OnPropertyChanged(nameof(IsEmpty));
        IsBusy = true;
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var categories = await _commands.ListCategoriesAsync();
            _categoryNames = categories.ToDictionary(c => c.Id, c => c.Name);
            var phrases = await _commands.ListPhrasesAsync();

            // 构建分类计数：一级 + 二级都计数
            var counts = phrases
                .GroupBy(p => p.CategoryId)
                .ToDictionary(g => g.Key, g => g.Count());

            // 企业分类挂在独立只读根节点下；个人分类仍保持原有两级结构。
            var mappedCategories = categories
                .Where(c => c.Scope == PhraseScope.Personal)
                // 一级分类也必须在首次加载时展开，否则 AppendCategory 会在根节点提前返回，
                // 已成功创建的二级分类不会进入 VisibleItems，用户会误以为创建失败。
                .Select(c => new CategoryItem(c.Id, c.Name, c.ParentId, c.SortOrder, counts.TryGetValue(c.Id, out var count) ? count : 0, IsExpanded: true, Version: c.Version, Scope: c.Scope))
                .ToList();
            var enterpriseCategories = categories.Where(c => c.Scope == PhraseScope.Enterprise).ToArray();
            if (enterpriseCategories.Length > 0)
            {
                mappedCategories.Add(new CategoryItem(EnterpriseRootId, "企业话术", null, int.MaxValue, enterpriseCategories.Sum(c => counts.TryGetValue(c.Id, out var count) ? count : 0), IsExpanded: true, Scope: PhraseScope.Enterprise, IsSynthetic: true));
                mappedCategories.AddRange(enterpriseCategories.Select(c => new CategoryItem(c.Id, c.Name, c.ParentId ?? EnterpriseRootId, c.SortOrder, counts.TryGetValue(c.Id, out var count) ? count : 0, IsExpanded: true, Version: c.Version, Scope: PhraseScope.Enterprise)));
            }
            Categories = new ObservableCollection<CategoryItem>(mappedCategories);
            var topIds = mappedCategories.Where(c => c.ParentId is null).Select(c => c.Id).ToHashSet();
            var defaultTop = mappedCategories.FirstOrDefault(c => c.ParentId is null && c.Scope == PhraseScope.Personal) ?? mappedCategories.FirstOrDefault(c => c.ParentId is null);
            SelectedCategoryId = defaultTop?.Id;
            IsCategoryFilterActive = defaultTop is not null;
            RefreshTopCategories();

            var allPhrases = phrases.Select(p => new PhraseItemViewModel(p, CategoryNameOf(p.CategoryId)) { Owner = this }).ToList();
            AssignIndicesAndSub(allPhrases, topIds);
            Phrases = new ObservableCollection<PhraseItemViewModel>(allPhrases);
            SetSearchResults([]);
            RebuildVisibleItems();
            StatusMessage = $"共 {Phrases.Count} 条话术";
            HasError = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>
    /// 给每条话术设序号与 IsSubCategory 标记。序号在「选中一级视图」内连续：先直接归属一级，再二级区。
    /// </summary>
    private void AssignIndicesAndSub(List<PhraseItemViewModel> items, HashSet<Guid> topCategoryIds)
    {
        var selectedTop = SelectedCategoryId;
        var scope = selectedTop.HasValue
            ? items.Where(p => topCategoryIds.Contains(p.CategoryId)
                ? p.CategoryId == selectedTop.Value
                : topCategoryIds.Contains(p.CategoryId)).ToList()
            : items;

        // 简化：全局范围内计算，直接归属一级在前，二级在后
        var ordered = scope
            .OrderBy(p => topCategoryIds.Contains(p.CategoryId) ? 0 : 1)
            .ThenBy(p => p.Title, StringComparer.CurrentCulture)
            .ToList();

        var idx = 1;
        foreach (var item in ordered)
        {
            item.IndexInCategory = idx++;
            item.IsSubCategory = !topCategoryIds.Contains(item.CategoryId);
        }
    }

    private string? CategoryNameOf(Guid id) => _categoryNames.TryGetValue(id, out var name) ? name : "未分类";

    /// <summary>供 LibraryView 在编辑器保存/移动后就地刷新对应话术行，保持列表状态。</summary>
    public string ResolveCategoryName(Guid id) => CategoryNameOf(id) ?? "未分类";

    /// <summary>
    /// 将持久化层返回的最新话术合并到内存列表；跨分类移动时同步维护源分类和目标分类计数。
    /// </summary>
    public void RefreshFromPhrase(Phrase phrase)
    {
        var existing = Phrases.FirstOrDefault(p => p.Id == phrase.Id);
        var previousCategoryId = existing?.CategoryId;
        if (existing is not null) existing.Apply(phrase, CategoryNameOf(phrase.CategoryId));
        else Phrases.Add(new PhraseItemViewModel(phrase, CategoryNameOf(phrase.CategoryId)) { Owner = this });

        var searchResult = SearchResults.FirstOrDefault(p => p.Id == phrase.Id);
        searchResult?.Apply(phrase, CategoryNameOf(phrase.CategoryId));

        if (previousCategoryId.HasValue && previousCategoryId.Value != phrase.CategoryId)
        {
            UpdateCategoryCount(previousCategoryId.Value, -1);
            UpdateCategoryCount(phrase.CategoryId, 1);
            RefreshTopCategories();
        }
        RebuildVisibleItems();
    }

    /// <summary>移动成功后刷新列表并向用户明确反馈目标分类。</summary>
    public void RefreshMovedPhrase(Phrase phrase)
    {
        var targetCategoryName = ResolveCategoryName(phrase.CategoryId);
        RefreshFromPhrase(phrase);
        StatusMessage = $"已移动到“{targetCategoryName}”";
    }

    /// <summary>安全调整单个分类的计数，防止失败或并发状态导致负数显示。</summary>
    private void UpdateCategoryCount(Guid categoryId, int delta)
    {
        var category = Categories.FirstOrDefault(item => item.Id == categoryId);
        if (category is null) return;
        var index = Categories.IndexOf(category);
        Categories[index] = category with { Count = Math.Max(0, category.Count + delta) };
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>顶部一级 chips 点击：选中一级分类并重建列表。</summary>
    [RelayCommand]
    private void SelectCategory(Guid? categoryId)
    {
        if (SelectedCategoryId == categoryId) return;
        SelectedCategoryId = categoryId;
        IsCategoryFilterActive = categoryId.HasValue;
        RefreshTopCategories();
        RebuildVisibleItems();
    }

    /// <summary>根据当前选中分类刷新一级 chips 的 IsSelected 标记。</summary>
    private void RefreshTopCategories()
    {
        var items = new List<CategoryItem>(Categories.Count);
        foreach (var c in Categories)
        {
            if (c.ParentId != null) continue;
            items.Add(c with { IsSelected = c.Id == SelectedCategoryId });
        }
        TopCategories = new ObservableCollection<CategoryItem>(items);
    }

    /// <summary>折叠/展开二级分类（点击 SubHeader）。</summary>
    [RelayCommand]
    private void ToggleSubCategory(Guid? categoryId)
    {
        if (!categoryId.HasValue) return;
        var target = Categories.FirstOrDefault(c => c.Id == categoryId.Value);
        if (target is null) return;
        var index = Categories.IndexOf(target);
        Categories[index] = target with { IsExpanded = !target.IsExpanded };
        RebuildVisibleItems();
    }

    /// <summary>新建一级分类（由 LibraryView 顶部"+"按钮触发）。</summary>
    [RelayCommand]
    private void NewCategory() => NewCategoryRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>在指定一级分类下新建二级分类（由一级 chip 右键菜单触发）。</summary>
    [RelayCommand]
    private void NewSubCategory(CategoryItem? category)
    {
        if (category is null) return;
        if (!category.CanManage) { StatusMessage = "企业分类由管理员维护。"; return; }
        NewSubCategoryRequested?.Invoke(this, category);
    }

    /// <summary>在指定分类下新建话术（由一级 chip / 二级标题条右键菜单触发）。</summary>
    [RelayCommand]
    private void NewPhraseInCategory(CategoryItem? category)
    {
        if (category is null) return;
        if (!category.CanManage) { StatusMessage = "企业分类由管理员维护。"; return; }
        NewPhraseInCategoryRequested?.Invoke(this, category);
    }

    /// <summary>打开设置（由底部 App Footer 设置按钮触发）。</summary>
    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>重命名分类（由右键菜单触发）。</summary>
    [RelayCommand]
    private void RenameCategory(CategoryItem? category)
    {
        if (category is null) return;
        if (!category.CanManage) { StatusMessage = "企业分类由管理员维护。"; return; }
        RenameCategoryRequested?.Invoke(this, category);
    }

    /// <summary>删除分类（由右键菜单触发）。</summary>
    [RelayCommand]
    private void DeleteCategory(CategoryItem? category)
    {
        if (category is null) return;
        if (!category.CanManage) { StatusMessage = "企业分类由管理员维护。"; return; }
        DeleteCategoryRequested?.Invoke(this, category);
    }

    /// <summary>
    /// 重建扁平化列表（VisibleItems）：选中一级后，先排该一级直接归属的话术，
    /// 再按 SortOrder 排列其下各二级：每个二级先放 SubHeaderItem（可折叠），
    /// 展开时再插入该二级的话术。未选中时（全部）按一级分组排列。
    /// </summary>
    public void RebuildVisibleItems()
    {
        var roots = Categories.Where(c => c.ParentId is null).OrderBy(c => c.Scope).ThenBy(c => c.SortOrder).ToList();
        var items = new List<object>();
        if (SelectedCategoryId is null) foreach (var root in roots) AppendCategory(root, items, includeHeader: false);
        else
        {
            var root = roots.FirstOrDefault(c => c.Id == SelectedCategoryId);
            if (root is not null) AppendCategory(root, items, includeHeader: false);
        }
        var index = 1;
        foreach (var phrase in items.OfType<PhraseItemViewModel>())
        {
            phrase.IndexInCategory = index++;
            phrase.IsSubCategory = Categories.FirstOrDefault(c => c.Id == phrase.CategoryId)?.ParentId is not null;
        }
        VisibleItems = new ObservableCollection<object>(items);
    }

    private void AppendCategory(CategoryItem category, List<object> items, bool includeHeader)
    {
        if (includeHeader) items.Add(new SubHeaderItem(category) { ParentName = Categories.FirstOrDefault(c => c.Id == category.ParentId)?.Name });
        foreach (var phrase in Phrases.Where(p => p.CategoryId == category.Id).OrderBy(p => p.SortOrder)) items.Add(phrase);
        if (!category.IsExpanded && !category.IsSynthetic) return;
        foreach (var child in Categories.Where(c => c.ParentId == category.Id).OrderBy(c => c.SortOrder)) AppendCategory(child, items, includeHeader: true);
    }

    /// <summary>
    /// 拖拽排序后持久化：按传入顺序为每个话术分配新 SortOrder 并循环提交。
    /// 排序号以 10 为步长，便于后续插入。失败不会回滚已写入的部分（每条独立事务），但在 UI 上保持乐观更新。
    /// </summary>
    public async Task ReorderPhrasesAsync(Guid categoryId, IList<PhraseItemViewModel> orderedItems)
    {
        if (orderedItems is null || orderedItems.Count == 0) return;
        if (orderedItems.Any(item => !item.CanManage)) { StatusMessage = "企业话术由管理员维护。"; return; }
        var anyFailed = false;
        for (var i = 0; i < orderedItems.Count; i++)
        {
            var item = orderedItems[i];
            var newSort = (i + 1) * 10;
            if (item.SortOrder == newSort) continue;
            item.SortOrder = newSort;
            var model = item.ToPhrase();
            var result = await _commands.UpdatePhraseAsync(new UpdatePhraseCommand(
                item.Id, item.Version, model.Title, model.Body, model.CategoryId,
                model.ShortcutMode, model.Shortcut?.Display, model.ColorKey, newSort));
            if (!result.IsSuccess)
            {
                anyFailed = true;
                StatusMessage = $"排序保存失败：{result.Error?.Message}";
                break;
            }
            item.Apply(result.Value!, CategoryNameOf(result.Value!.CategoryId));
        }
        if (!anyFailed)
        {
            // 写库成功后按新 SortOrder 重建列表，界面顺序才会刷新
            RebuildVisibleItems();
            StatusMessage = "已保存新顺序";
        }
    }

    /// <summary>
    /// 拖拽分类 chip 重排后持久化：通过 MoveCategoryCommand（同父级 + 新 SortOrder）。
    /// 当新顺序导致 SortOrder 与现存项冲突时，先整体 +10*N 拉大间隔再写入。
    /// </summary>
    public async Task ReorderCategoriesAsync(IList<CategoryItem> orderedTops)
    {
        if (orderedTops is null || orderedTops.Count == 0) return;
        if (orderedTops.Any(item => !item.CanManage)) { StatusMessage = "企业分类由管理员维护。"; return; }
        // 先把所有目标 sort_order 抬高 10*N+1，避免与现有值冲突；写入目标值
        for (var i = 0; i < orderedTops.Count; i++)
        {
            var cat = orderedTops[i];
            var newSort = (i + 1) * 10;
            if (cat.SortOrder == newSort) continue;
            var catInStore = Categories.FirstOrDefault(c => c.Id == cat.Id);
            if (catInStore is null) continue;
            var index = Categories.IndexOf(catInStore);
            var result = await _commands.MoveCategoryAsync(new MoveCategoryCommand(
                cat.Id, catInStore.Version, cat.ParentId, newSort));
            if (!result.IsSuccess)
            {
                StatusMessage = $"分类排序保存失败：{result.Error?.Message}";
                return;
            }
            Categories[index] = catInStore with { SortOrder = newSort, Version = result.Value!.Version };
        }
        // 同步刷新顶部 chips
        await LoadAsync();
    }

    [RelayCommand]
    private Task RetryLoad() => LoadAsync();

    [RelayCommand]
    private async Task Search()
    {
        // 输入即搜允许新查询覆盖旧查询，不能用 IsBusy 直接丢弃后续输入。
        var requestVersion = Interlocked.Increment(ref _searchRequestVersion);
        HasSearchError = false;
        var q = (SearchQuery ?? string.Empty).Trim();
        IsSearchBusy = q.Length > 0;
        if (q.Length > 0) SetSearchResults([]);
        OnPropertyChanged(nameof(IsSearchResultEmpty));
        try
        {
            IReadOnlyList<Phrase> phrases = q.Length == 0
                ? await _commands.ListPhrasesAsync()
                : await _commands.SearchPhrasesAsync(q, 50);

            // 旧请求即使已经完成，也不得覆盖最后一次输入对应的列表和状态。
            if (requestVersion != Volatile.Read(ref _searchRequestVersion)) return;

            if (q.Length == 0)
            {
                Phrases = new ObservableCollection<PhraseItemViewModel>(
                    phrases.Select(p => new PhraseItemViewModel(p, CategoryNameOf(p.CategoryId)) { Owner = this }));
                SetSearchResults([]);
            }
            else
            {
                SetSearchResults(phrases.Select((p, index) =>
                {
                    var item = new PhraseItemViewModel(p, CategoryNameOf(p.CategoryId))
                    {
                        Owner = this,
                        SearchResultIndex = index + 1,
                    };
                    return item;
                }));
            }
            RebuildVisibleItems();
            StatusMessage = q.Length == 0 ? $"共 {Phrases.Count} 条话术" : $"“{q}” 匹配 {SearchResults.Count} 条";
            HasError = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            if (requestVersion == Volatile.Read(ref _searchRequestVersion))
            {
                HasSearchError = true;
                SetSearchResults([]);
                StatusMessage = $"搜索失败：{ex.Message}";
            }
        }
        finally
        {
            if (requestVersion == Volatile.Read(ref _searchRequestVersion))
            {
                IsSearchBusy = false;
                OnPropertyChanged(nameof(IsSearchResultEmpty));
            }
        }
    }

    /// <summary>替换搜索结果并通知浮层空状态，主列表数据源不参与此操作。</summary>
    private void SetSearchResults(IEnumerable<PhraseItemViewModel> items)
    {
        var resultItems = items.ToList();
        for (var index = 0; index < resultItems.Count; index++)
            resultItems[index].SearchResultIndex = index + 1;

        SearchResults = new ObservableCollection<PhraseItemViewModel>(resultItems);
        OnPropertyChanged(nameof(IsSearchResultEmpty));
    }

    [RelayCommand]
    private void Copy(PhraseItemViewModel? item)
    {
        if (item is null) return;
        System.Windows.Clipboard.SetText(item.Content);
        StatusMessage = "已复制到剪贴板";
    }

    [RelayCommand]
    private void Move(PhraseItemViewModel? item)
    {
        if (item is null) return;
        if (!item.CanManage) { StatusMessage = "企业话术由管理员维护。"; return; }
        MoveRequested?.Invoke(this, item);
    }

    [RelayCommand]
    private async Task Delete(PhraseItemViewModel? item)
    {
        if (item is null) return;
        if (!item.CanManage) { StatusMessage = "企业话术由管理员维护。"; return; }
        var ok = await _commands.DeletePhraseAsync(item.Id, item.Version);
        if (ok)
        {
            var stored = Phrases.FirstOrDefault(p => p.Id == item.Id);
            if (stored is not null) Phrases.Remove(stored);
            SetSearchResults(SearchResults.Where(p => p.Id != item.Id));
            if (SelectedPhrase?.Id == item.Id) SelectedPhrase = null;
            RebuildVisibleItems();
            StatusMessage = "已删除";
        }
        else
        {
            StatusMessage = "删除失败";
        }
    }

    [RelayCommand]
    private void Edit(PhraseItemViewModel? item)
    {
        if (item is null) return;
        // 企业话术进入同一详情页，由 EditorViewModel 根据 Scope 切换只读状态。
        EditRequested?.Invoke(this, item);
    }

    [RelayCommand]
    private void New() => NewRequested?.Invoke(this, EventArgs.Empty);
}
