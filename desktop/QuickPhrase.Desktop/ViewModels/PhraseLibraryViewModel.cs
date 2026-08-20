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
    private readonly ICommandService _commands;
    private readonly Func<string, Task<bool>>? _recordSearchHistory;
    private Dictionary<Guid, string> _categoryNames = new();

    public PhraseLibraryViewModel(ICommandService commands, Func<string, Task<bool>>? recordSearchHistory = null)
    {
        _commands = commands;
        _recordSearchHistory = recordSearchHistory;
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
        _ = SearchCommand.ExecuteAsync(null);
    }
    [ObservableProperty] private ObservableCollection<PhraseItemViewModel> _phrases = new();
    [ObservableProperty] private PhraseItemViewModel? _selectedPhrase;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasError;

    /// <summary>共享状态呈现器使用的派生状态：没有加载中/错误且当前列表为空。</summary>
    public bool IsEmpty => !IsBusy && !HasError && VisibleItems.Count == 0;
    public string EmptyStateTitle => string.IsNullOrWhiteSpace(SearchQuery) ? "暂无话术" : "没有找到对应关键词";
    public string EmptyStateDescription => string.IsNullOrWhiteSpace(SearchQuery) ? "创建第一条话术，之后可在闪念中快速插入。" : "换个关键词试试，或清空搜索条件。";

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
    public event EventHandler<PhraseItemViewModel>? InsertSendRequested;
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

            // 一级分类（ParentId == null）按 SortOrder 排序
            var topCategories = categories
                .Where(c => c.ParentId == null)
                .OrderBy(c => c.SortOrder)
                .Select(c => new CategoryItem(
                    c.Id, c.Name, c.ParentId, c.SortOrder,
                    counts.TryGetValue(c.Id, out var n) ? n : 0,
                    IsExpanded: false, Version: c.Version))
                .ToList();

            // 二级分类挂在各自一级下
            var topIds = topCategories.Select(c => c.Id).ToHashSet();
            foreach (var top in topCategories.ToList())
            {
                var subs = categories
                    .Where(c => c.ParentId == top.Id)
                    .OrderBy(c => c.SortOrder)
                    .Select(c => new CategoryItem(
                        c.Id, c.Name, c.ParentId, c.SortOrder,
                        counts.TryGetValue(c.Id, out var n) ? n : 0,
                        IsExpanded: true, Version: c.Version));
                // 二级列表作为平铺项追加到 Categories，便于 UI 按需分组
                foreach (var sub in subs) topCategories.Add(sub);
            }
            Categories = new ObservableCollection<CategoryItem>(topCategories);
            // 默认进入第一个真实一级分类；首次安装无分类时保持未选中状态。
            var defaultTop = topCategories.FirstOrDefault(c => c.ParentId == null);
            SelectedCategoryId = defaultTop?.Id;
            IsCategoryFilterActive = defaultTop is not null;
            RefreshTopCategories();

            var allPhrases = phrases.Select(p => new PhraseItemViewModel(p, CategoryNameOf(p.CategoryId)) { Owner = this }).ToList();
            AssignIndicesAndSub(allPhrases, topIds);
            Phrases = new ObservableCollection<PhraseItemViewModel>(allPhrases);
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

    public void RefreshFromPhrase(Phrase phrase)
    {
        var existing = Phrases.FirstOrDefault(p => p.Id == phrase.Id);
        if (existing is not null) existing.Apply(phrase, CategoryNameOf(phrase.CategoryId));
        else Phrases.Add(new PhraseItemViewModel(phrase, CategoryNameOf(phrase.CategoryId)) { Owner = this });
        RebuildVisibleItems();
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
        if (category is not null) NewSubCategoryRequested?.Invoke(this, category);
    }

    /// <summary>在指定分类下新建话术（由一级 chip / 二级标题条右键菜单触发）。</summary>
    [RelayCommand]
    private void NewPhraseInCategory(CategoryItem? category)
    {
        if (category is not null) NewPhraseInCategoryRequested?.Invoke(this, category);
    }

    /// <summary>打开设置（由底部 App Footer 设置按钮触发）。</summary>
    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>重命名分类（由右键菜单触发）。</summary>
    [RelayCommand]
    private void RenameCategory(CategoryItem? category)
    {
        if (category is not null) RenameCategoryRequested?.Invoke(this, category);
    }

    /// <summary>删除分类（由右键菜单触发）。</summary>
    [RelayCommand]
    private void DeleteCategory(CategoryItem? category)
    {
        if (category is not null) DeleteCategoryRequested?.Invoke(this, category);
    }

    /// <summary>
    /// 重建扁平化列表（VisibleItems）：选中一级后，先排该一级直接归属的话术，
    /// 再按 SortOrder 排列其下各二级：每个二级先放 SubHeaderItem（可折叠），
    /// 展开时再插入该二级的话术。未选中时（全部）按一级分组排列。
    /// </summary>
    public void RebuildVisibleItems()
    {
        var tops = Categories.Where(c => c.ParentId == null).OrderBy(c => c.SortOrder).ToList();
        var items = new List<object>();

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            // 有搜索词时，结果已经由 Core 搜索服务筛选并排序；此时必须跨分类直接展示，
            // 否则默认选中的一级分类会把其它分类的匹配结果再次隐藏。
            items.AddRange(Phrases);
        }
        else if (SelectedCategoryId is null)
        {
            // 「全部」：遍历每个一级，一级直挂话术（按 SortOrder） + 其下二级区
            foreach (var top in tops)
            {
                foreach (var p in Phrases.Where(p => p.CategoryId == top.Id).OrderBy(p => p.SortOrder)) items.Add(p);
                foreach (var sub in Categories.Where(c => c.ParentId == top.Id).OrderBy(c => c.SortOrder))
                {
                    var parentName = Categories.FirstOrDefault(c => c.Id == sub.ParentId)?.Name;
                    items.Add(new SubHeaderItem(sub) { ParentName = parentName });
                    if (sub.IsExpanded)
                        foreach (var p in Phrases.Where(p => p.CategoryId == sub.Id).OrderBy(p => p.SortOrder)) items.Add(p);
                }
            }
        }
        else
        {
            var top = tops.FirstOrDefault(c => c.Id == SelectedCategoryId);
            if (top is not null)
            {
                foreach (var p in Phrases.Where(p => p.CategoryId == top.Id).OrderBy(p => p.SortOrder)) items.Add(p);
                foreach (var sub in Categories.Where(c => c.ParentId == top.Id).OrderBy(c => c.SortOrder))
                {
                    var parentName = Categories.FirstOrDefault(c => c.Id == sub.ParentId)?.Name;
                    items.Add(new SubHeaderItem(sub) { ParentName = parentName });
                    if (sub.IsExpanded)
                        foreach (var p in Phrases.Where(p => p.CategoryId == sub.Id).OrderBy(p => p.SortOrder)) items.Add(p);
                }
            }
        }

        // 重新编号 + 二级归属标记（归属二级分类的话术更深缩进）
        var idx = 1;
        foreach (var item in items.OfType<PhraseItemViewModel>())
        {
            item.IndexInCategory = idx++;
            item.IsSubCategory = Categories.Any(c => c.ParentId == item.CategoryId);
        }
        VisibleItems = new ObservableCollection<object>(items);
    }

    /// <summary>
    /// 拖拽排序后持久化：按传入顺序为每个话术分配新 SortOrder 并循环提交。
    /// 排序号以 10 为步长，便于后续插入。失败不会回滚已写入的部分（每条独立事务），但在 UI 上保持乐观更新。
    /// </summary>
    public async Task ReorderPhrasesAsync(Guid categoryId, IList<PhraseItemViewModel> orderedItems)
    {
        if (orderedItems is null || orderedItems.Count == 0) return;
        var anyFailed = false;
        for (var i = 0; i < orderedItems.Count; i++)
        {
            var item = orderedItems[i];
            var newSort = (i + 1) * 10;
            if (item.SortOrder == newSort) continue;
            item.SortOrder = newSort;
            var model = item.ToPhrase();
            var result = await _commands.UpdatePhraseAsync(new UpdatePhraseCommand(
                item.Id, item.Version, model.Title, model.Content, model.CategoryId,
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
        HasError = false;
        OnPropertyChanged(nameof(IsEmpty));
        IsBusy = true;
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var q = (SearchQuery ?? string.Empty).Trim();
            IReadOnlyList<Phrase> phrases = q.Length == 0
                ? await _commands.ListPhrasesAsync()
                : await _commands.SearchPhrasesAsync(q, 50);

            // 旧请求即使已经完成，也不得覆盖最后一次输入对应的列表和状态。
            if (requestVersion != Volatile.Read(ref _searchRequestVersion)) return;

            Phrases = new ObservableCollection<PhraseItemViewModel>(
                phrases.Select(p => new PhraseItemViewModel(p, CategoryNameOf(p.CategoryId)) { Owner = this }));
            RebuildVisibleItems();
            StatusMessage = q.Length == 0 ? $"共 {Phrases.Count} 条话术" : $"“{q}” 匹配 {Phrases.Count} 条";
            HasError = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            if (requestVersion == Volatile.Read(ref _searchRequestVersion))
            {
                HasError = true;
                StatusMessage = $"搜索失败：{ex.Message}";
            }
        }
        finally
        {
            if (requestVersion == Volatile.Read(ref _searchRequestVersion))
            {
                IsBusy = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    [RelayCommand]
    private async Task Insert(PhraseItemViewModel? item)
    {
        if (item is null) return;
        var ok = await _commands.InsertPhraseAsync(item.Id);
        if (!ok)
        {
            StatusMessage = "插入未执行（当前目标窗口不可用）";
            return;
        }

        var historySaved = string.IsNullOrWhiteSpace(SearchQuery) || _recordSearchHistory is null
            || await _recordSearchHistory(SearchQuery.Trim());
        StatusMessage = historySaved
            ? "已请求插入到当前窗口"
            : "已请求插入到当前窗口，但历史搜索保存失败";
    }

    [RelayCommand]
    private void Copy(PhraseItemViewModel? item)
    {
        if (item is null) return;
        System.Windows.Clipboard.SetText(item.Content);
        StatusMessage = "已复制到剪贴板";
    }

    [RelayCommand]
    private void InsertSend(PhraseItemViewModel? item)
    {
        if (item is null) return;
        InsertSendRequested?.Invoke(this, item);
        StatusMessage = "已请求插入并发送";
    }

    [RelayCommand]
    private void Move(PhraseItemViewModel? item)
    {
        if (item is null) return;
        MoveRequested?.Invoke(this, item);
    }

    [RelayCommand]
    private async Task Delete(PhraseItemViewModel? item)
    {
        if (item is null) return;
        var ok = await _commands.DeletePhraseAsync(item.Id, item.Version);
        if (ok)
        {
            Phrases.Remove(item);
            if (ReferenceEquals(SelectedPhrase, item)) SelectedPhrase = null;
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
        if (item is not null) EditRequested?.Invoke(this, item);
    }

    [RelayCommand]
    private void New() => NewRequested?.Invoke(this, EventArgs.Empty);
}
