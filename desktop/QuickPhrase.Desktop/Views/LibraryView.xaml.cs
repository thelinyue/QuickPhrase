using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WpfPoint = System.Windows.Point;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.ViewModels;
using QuickPhrase.Desktop.Views.Shared;

namespace QuickPhrase.Desktop;

/// <summary>
/// 话术库视图（闪语原型·第七版）：单栏纵向�?
/// Content Header �?一级分�?chips �?内联嵌套树（二级 SubHeader + 话术行）�?底部搜索 �?品牌区�?
/// 交互：单击选中，双击或 Enter 打开编辑/只读详情，Delete 删除个人话术。
/// </summary>
public partial class LibraryView : System.Windows.Controls.UserControl
{
    private readonly PhraseLibraryViewModel _viewModel;
    private readonly SearchHistoryCoordinator _searchHistory;

    // 空白区域菜单只在当前视图生命周期内存活；上下文关闭后必须清空，避免复用旧分类。
    private readonly System.Windows.Controls.ContextMenu _blankAreaContextMenu;
    private LibraryBlankAreaMenuContext? _blankAreaMenuContext;
    private Window? _ownerWindow;
    private bool _libraryEventsAttached;
    private bool _suppressNextSearchHistoryOpen;

    public LibraryView(ICommandService commands, SearchHistoryCoordinator searchHistory)
    {
        InitializeComponent();
        _blankAreaContextMenu = (System.Windows.Controls.ContextMenu)FindResource("BlankAreaContextMenu");
        _searchHistory = searchHistory;
        _viewModel = new PhraseLibraryViewModel(commands);
        DataContext = _viewModel;
        SearchHistoryPanel.DataContext = _searchHistory.ViewModel;
        // Popup 脱离 UserControl 的可视树，显式绑定到库 ViewModel，避免结果列表在弹出窗口中丢失数据上下文。
        SearchResultsPopup.DataContext = _viewModel;
// 把库级事件转发出去，�?MainWindow / ApplicationController 接入编辑器与投递�?
        _viewModel.EditRequested += (_, item) => RequestEdit?.Invoke(this, item);
        _viewModel.NewRequested += (_, _) => RequestNew?.Invoke(this, EventArgs.Empty);
        _viewModel.MoveRequested += (_, item) => RequestMove?.Invoke(this, item);
        _viewModel.NewCategoryRequested += (_, _) => RequestNewCategory?.Invoke(this, EventArgs.Empty);
        _viewModel.NewSubCategoryRequested += (_, c) => RequestNewSubCategory?.Invoke(this, c);
        _viewModel.NewPhraseInCategoryRequested += (_, c) => RequestNewPhraseInCategory?.Invoke(this, c);
        _viewModel.RenameCategoryRequested += (_, c) => RequestRenameCategory?.Invoke(this, c);
        _viewModel.DeleteCategoryRequested += (_, c) => RequestDeleteCategory?.Invoke(this, c);

        PhraseList.PreviewKeyDown += OnListKeyDown;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>视图尺寸变化时关闭空白区域菜单，避免菜单继续锚定到已经变化的列表位置。</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => CloseBlankAreaMenu();

    /// <summary>
    /// 菜单只在视图可见期间绑定滚动和窗口事件。使用 AddHandler 监听 ListBox 内部
    /// ScrollViewer 的路由事件，既覆盖滚动条，也覆盖触控板/鼠标滚轮滚动，并且只绑定一次。
    /// </summary>
    private void AttachLibraryEvents()
    {
        if (_libraryEventsAttached) return;

        PhraseList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnLibraryScrollChanged));
        CategoryChipsList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnLibraryScrollChanged));

        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.SizeChanged += OwnerWindow_SizeChanged;
            _ownerWindow.Closed += OwnerWindow_Closed;
        }

        _libraryEventsAttached = true;
    }

    /// <summary>卸载时解除所有路由/窗口事件，避免视图被导航替换后继续持有窗口引用。</summary>
    private void DetachLibraryEvents()
    {
        CloseSearchResults();
        if (!_libraryEventsAttached)
        {
            CloseBlankAreaMenu();
            return;
        }

        PhraseList.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnLibraryScrollChanged));
        CategoryChipsList.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnLibraryScrollChanged));

        if (_ownerWindow is not null)
        {
            _ownerWindow.SizeChanged -= OwnerWindow_SizeChanged;
            _ownerWindow.Closed -= OwnerWindow_Closed;
        }

        _ownerWindow = null;
        _libraryEventsAttached = false;
        CloseBlankAreaMenu();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachLibraryEvents();

    private void OwnerWindow_SizeChanged(object sender, SizeChangedEventArgs e) => CloseBlankAreaMenu();

    private void OwnerWindow_Closed(object? sender, EventArgs e) => CloseBlankAreaMenu();

    private void OnLibraryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.VerticalChange != 0 || e.HorizontalChange != 0)
            CloseBlankAreaMenu();
    }

    private void BlankAreaContextMenu_Closed(object? sender, RoutedEventArgs e)
    {
        // ContextMenu 关闭可能来自外部点击、Esc、滚动或窗口销毁；所有路径都在这里统一清理。
        _blankAreaMenuContext = null;
        _blankAreaContextMenu.PlacementTarget = null;
    }

    private void BlankAreaContextMenu_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseBlankAreaMenu();
    }

    private void BlankAreaNewPhrase_Click(object sender, RoutedEventArgs e)
    {
        var context = _blankAreaMenuContext;
        CloseBlankAreaMenu();
        if (context is null) return;

        var message = LibraryBlankAreaMenuPolicy.GetNewPhraseUnavailableMessage(context);
        if (message is not null)
        {
            // 没有一级分类时统一走主窗口的新建话术流程，由它提供“取消/新建分类”分支。
            RequestNew?.Invoke(this, EventArgs.Empty);
            return;
        }

        var target = LibraryBlankAreaMenuPolicy.ResolveNewPhraseTarget(context);
        if (target is not null) RequestNewPhraseInCategory?.Invoke(this, target);
    }

    private void BlankAreaNewSubCategory_Click(object sender, RoutedEventArgs e)
    {
        var context = _blankAreaMenuContext;
        CloseBlankAreaMenu();
        if (context is null) return;

        var message = LibraryBlankAreaMenuPolicy.GetNewSubCategoryUnavailableMessage(context);
        if (message is not null)
        {
            ShowBlankAreaHint(message);
            return;
        }

        var parent = LibraryBlankAreaMenuPolicy.ResolveNewSubCategoryParent(context);
        if (parent is not null) RequestNewSubCategory?.Invoke(this, parent);
    }

    private void ShowBlankAreaHint(string message)
    {
        var owner = Window.GetWindow(this);
        System.Windows.MessageBox.Show(owner, message, "闪语", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 空白区域点击只负责收回搜索输入焦点；节点和其他交互控件保留其原有焦点行为。
    /// </summary>
    private void RootLayout_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (IsLibraryNodeHit(source) || IsNonBlankInteractiveControl(source)) return;

        Keyboard.Focus(RootLayout);
    }

    /// <summary>
    /// 右键预览阶段先判定节点命中，再处理空白菜单。命中话术行/二级标题/一级 chip
    /// 时不标记事件，交由现有节点 ContextMenu 继续处理；只有真正的空白区域才拦截并打开本菜单。
    /// </summary>
    private void RootLayout_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var nodeHit = IsLibraryNodeHit(source);
        if (nodeHit || IsNonBlankInteractiveControl(source)) return;
        if (!LibraryBlankAreaMenuPolicy.ShouldOpenMenu(nodeHit)) return;

        var context = ResolveBlankAreaContext(source, e);

        _blankAreaMenuContext = context;
        ConfigureBlankAreaMenu(context);
        _blankAreaContextMenu.PlacementTarget = this;
        _blankAreaContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void RootLayout_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 话术库是内容管理页面，只有用户明确按下 Ctrl+F 时才主动进入搜索输入。
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _blankAreaContextMenu.IsOpen)
        {
            e.Handled = true;
            CloseBlankAreaMenu();
        }
    }

    private void ConfigureBlankAreaMenu(LibraryBlankAreaMenuContext context)
    {
        var newPhraseItem = (System.Windows.Controls.MenuItem)_blankAreaContextMenu.Items[0];
        var newSubCategoryItem = (System.Windows.Controls.MenuItem)_blankAreaContextMenu.Items[1];
        newPhraseItem.ToolTip = context.HasTopCategory
            ? context.IsSubCategory ? "在当前二级分类下新建一条空白话术" : "在当前一级分类下新建一条空白话术"
            : "先新增一级分类，再新增话术";
        newSubCategoryItem.ToolTip = context.HasTopCategory
            ? context.IsSubCategory ? "在当前二级分类所属一级分类下新建同级二级分类" : "在当前一级分类下新建二级分类"
            : "先新增一级分类，再新建二级分类";
    }

    private void CloseBlankAreaMenu()
    {
        if (_blankAreaContextMenu.IsOpen) _blankAreaContextMenu.IsOpen = false;
        _blankAreaMenuContext = null;
    }

    private LibraryBlankAreaMenuContext ResolveBlankAreaContext(DependencyObject source, MouseButtonEventArgs e)
    {
        if (TryGetSubCategoryFromSource(source, out var sourceSub))
            return CreateCategoryContext(sourceSub);

        if (FindAncestor<System.Windows.Controls.ListBox>(source) == PhraseList &&
            FindSubCategoryAtPoint(e.GetPosition(PhraseList)) is CategoryItem pointSub)
            return CreateCategoryContext(pointSub);

        var selectedTop = _viewModel.Categories.FirstOrDefault(c =>
            c.ParentId is null && c.Id == _viewModel.SelectedCategoryId);
        return LibraryBlankAreaMenuPolicy.CreateContext(selectedTop, selectedTop);
    }

    private LibraryBlankAreaMenuContext CreateCategoryContext(CategoryItem active)
    {
        var top = active.ParentId is null
            ? active
            : _viewModel.Categories.FirstOrDefault(c => c.Id == active.ParentId.Value && c.ParentId is null);
        return LibraryBlankAreaMenuPolicy.CreateContext(active, top);
    }

    private bool IsLibraryNodeHit(DependencyObject source)
    {
        if (IsContextMenuItem(source)) return true;

        var listItem = FindAncestor<ListBoxItem>(source);
        if (listItem?.DataContext is PhraseItemViewModel or SubHeaderItem or CategoryItem) return true;

        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ToggleButton toggle && toggle.DataContext is CategoryItem or SubHeaderItem) return true;
        }

        return false;
    }

    private static bool IsNonBlankInteractiveControl(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.TextBox or System.Windows.Controls.Button or System.Windows.Controls.ComboBox) return true;
            if (current is System.Windows.Controls.ContextMenu or System.Windows.Controls.MenuItem) return true;
        }

        return false;
    }

    private bool TryGetSubCategoryFromSource(DependencyObject source, out CategoryItem category)
    {
        var item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is SubHeaderItem header)
        {
            category = header.Category;
            return true;
        }

        category = null!;
        return false;
    }

    /// <summary>
    /// 通过可见 ListBoxItem 的视口位置确定二级分类内容块。它只读取 VisibleItems
    /// 和已生成容器，不改变扁平化列表，也不依赖局部坐标换算来定位菜单。
    /// </summary>
    private CategoryItem? FindSubCategoryAtPoint(WpfPoint point)
    {
        var visibleItems = _viewModel.VisibleItems;
        for (var index = 0; index < visibleItems.Count; index++)
        {
            if (visibleItems[index] is not SubHeaderItem header) continue;
            if (PhraseList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container) continue;

            try
            {
                var top = container.TransformToAncestor(PhraseList).Transform(new WpfPoint(0, 0)).Y;
                var bottom = PhraseList.ActualHeight;
                for (var next = index + 1; next < visibleItems.Count; next++)
                {
                    if (visibleItems[next] is not SubHeaderItem) continue;
                    if (PhraseList.ItemContainerGenerator.ContainerFromIndex(next) is FrameworkElement nextContainer)
                        bottom = nextContainer.TransformToAncestor(PhraseList).Transform(new WpfPoint(0, 0)).Y;
                    break;
                }

                if (point.Y >= top && point.Y < bottom) return header.Category;
            }
            catch (InvalidOperationException)
            {
                // 容器正在虚拟化/回收时，本次右键按默认一级分类处理。
            }
        }

        return null;
    }

    public event EventHandler<PhraseItemViewModel>? RequestEdit;
    public event EventHandler? RequestNew;
    public event EventHandler<PhraseItemViewModel>? RequestMove;
    public event EventHandler? RequestNewCategory;
    public event EventHandler<CategoryItem>? RequestNewSubCategory;
    public event EventHandler<CategoryItem>? RequestNewPhraseInCategory;
    public event EventHandler<CategoryItem>? RequestRenameCategory;
    public event EventHandler<CategoryItem>? RequestDeleteCategory;

    /// <summary>编辑器保存或移动分类后，就地刷新对应话术行（保持列表滚动/选中状态）�?/summary>
    public void RefreshPhrase(Phrase phrase) => _viewModel.RefreshFromPhrase(phrase);

    /// <summary>话术移动成功后用最新持久化结果刷新列表，并显示明确的成功反馈。</summary>
    public void RefreshMovedPhrase(Phrase phrase) => _viewModel.RefreshMovedPhrase(phrase);

    /// <summary>整体重载（新�?删除分类后调用）�?/summary>
    public Task ReloadAsync() => _viewModel.LoadAsync();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachLibraryEvents();
        await _viewModel.LoadAsync();
        if (_viewModel.IsSearchResultVisible)
        {
            // 视图重新挂载时恢复仍在输入中的搜索状态，避免遮罩已显示但 Popup 尚未重新打开。
            OpenSearchResults();
            await _viewModel.SearchCommand.ExecuteAsync(null);
        }
    }

    private void PhraseList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source)?.DataContext is not PhraseItemViewModel item) return;

        // 双击必须以实际命中的话术行为准，避免空白区域复用旧的 SelectedItem。
        _viewModel.EditCommand.Execute(item);
    }

    private void OnListKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F3:
                SearchBox.Focus();
                e.Handled = true;
                return;
            case Key.Escape:
                if (SearchBox.IsKeyboardFocused)
                {
                    PhraseList.Focus();
                    e.Handled = true;
                }
                return;
        }

        if (_viewModel.SelectedPhrase is null) return;
        switch (e.Key)
        {
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Control) == 0:
                _viewModel.EditCommand.Execute(_viewModel.SelectedPhrase);
                e.Handled = true;
                break;
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                _viewModel.EditCommand.Execute(_viewModel.SelectedPhrase);
                e.Handled = true;
                break;
            case Key.Delete:
                _viewModel.DeleteCommand.Execute(_viewModel.SelectedPhrase);
                e.Handled = true;
                break;
        }
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
        {
            CloseSearchResults();
            OpenSearchHistory();
        }
        else
        {
            CloseSearchHistory();
            OpenSearchResults();
        }
    }

    /// <summary>
    /// 输入时保持历史记录可见，但不在逐字搜索阶段写库；历史只在用户明确确认搜索时保存，
    /// 避免把输入过程中的半成品关键词写入本机 SQLite。
    /// </summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressNextSearchHistoryOpen)
        {
            _suppressNextSearchHistoryOpen = false;
            CloseSearchResults();
            return;
        }

        if (SearchBox.IsKeyboardFocusWithin && string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
        {
            CloseSearchResults();
            OpenSearchHistory();
        }
        else
        {
            CloseSearchHistory();
            if (string.IsNullOrWhiteSpace(_viewModel.SearchQuery)) CloseSearchResults();
            else OpenSearchResults();
        }
    }

    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ScheduleSearchHistoryClose();

    private void SearchHistoryPopup_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ScheduleSearchHistoryClose();

    private async void SearchHistoryPanel_QuerySelected(object? sender, string query)
    {
        _viewModel.SearchQuery = query;
        CloseSearchHistory();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        await RecordConfirmedSearchAsync(query);
    }

    private async void SearchHistoryPanel_ClearRequested(object? sender, EventArgs e)
    {
        if (!_searchHistory.ViewModel.HasEntries) return;
        var answer = System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            "确定清除全部历史搜索记录吗？此操作不可撤销。",
            "清除全部历史搜索",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        await _searchHistory.ClearAsync();
        SearchHistoryPanel.ClearSelection();
        OpenSearchHistory();
    }

    private void OpenSearchHistory()
    {
        if (!IsLoaded || !string.IsNullOrWhiteSpace(_viewModel.SearchQuery)) return;
        CloseSearchResults();
        SearchHistoryPopup.IsOpen = true;
    }

    private void CloseSearchHistory()
    {
        SearchHistoryPanel.ClearSelection();
        SearchHistoryPopup.IsOpen = false;
    }

    /// <summary>
    /// 搜索结果 Popup 的 IsOpen 由代码后台显式管理。
    /// WPF 在未加载的视图中处理 Popup.IsOpen 绑定时可能创建悬空弹出窗口，导致后续窗口测试等待 Dispatcher；
    /// 显式开关既保留结果浮层行为，也让视图卸载时可以确定性关闭它。
    /// </summary>
    private void OpenSearchResults()
    {
        if (IsLoaded && !string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
            SearchResultsPopup.IsOpen = true;
    }

    private void CloseSearchResults() => SearchResultsPopup.IsOpen = false;

    private void ScheduleSearchHistoryClose()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!SearchBox.IsKeyboardFocusWithin && !SearchHistoryPopup.IsKeyboardFocusWithin)
                CloseSearchHistory();
        }));
    }

    private async void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearSearch();
            e.Handled = true;
            return;
        }

        if (SearchHistoryPopup.IsOpen)
        {
            if (e.Key == Key.Down && SearchHistoryPanel.MoveSelection(1))
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up && SearchHistoryPanel.MoveSelection(-1))
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && SearchHistoryPanel.SelectedEntry is not null)
            {
                SearchHistoryPanel_QuerySelected(this, SearchHistoryPanel.SelectedEntry.Query);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter)
        {
            // 先消费按键，再异步写入历史，避免 SQLite 写入等待期间 Enter 继续冒泡。
            e.Handled = true;
            await RecordConfirmedSearchAsync(_viewModel.SearchQuery);
        }
    }

    /// <summary>关闭搜索结果浮层并恢复当前分类列表；关闭后焦点仍留在搜索框便于继续输入。</summary>
    private void ClearSearch_Click(object sender, RoutedEventArgs e) => ClearSearch();

    private void ClearSearch()
    {
        _suppressNextSearchHistoryOpen = true;
        CloseSearchResults();
        _viewModel.SearchQuery = string.Empty;
        CloseSearchHistory();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void SearchResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source)?.DataContext is not PhraseItemViewModel item) return;

        _viewModel.EditCommand.Execute(item);
        e.Handled = true;
    }

    private void SearchResultsList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearSearch();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && SearchResultsList.SelectedItem is PhraseItemViewModel item)
        {
            _viewModel.EditCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 话术库采用输入即搜；此处只补充“用户已确认”的历史保存语义，
    /// 不重新触发搜索，避免与 ViewModel 的输入搜索并发重复执行。
    /// </summary>
    private Task RecordConfirmedSearchAsync(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? Task.CompletedTask
            : _searchHistory.RecordAsync(query.Trim());

    // ============
    //  拖拽排序：一级分类 chips 与话术行（持久化 SortOrder）
    // ============================================================
    // ============================================================
    private const double DragThreshold = 8.0;

    private object? _dragCategorySource;
    private System.Windows.Point _dragCategoryOrigin;
    private PhraseItemViewModel? _dragPhraseSource;
    private System.Windows.Point _dragPhraseOrigin;

    private static bool PassedThreshold(System.Windows.Point a, System.Windows.Point b) =>
        Math.Abs(a.X - b.X) >= DragThreshold || Math.Abs(a.Y - b.Y) >= DragThreshold;

    // ---- 一级分�?chip 拖拽 ----
    private void CategoryChip_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.ContextMenu>(d) is not null) return;
        if (e.OriginalSource is DependencyObject d2 && IsContextMenuItem(d2)) return;
        if (sender is ListBoxItem item && item.DataContext is CategoryItem cat && cat.CanManage)
        {
            _dragCategorySource = cat;
            _dragCategoryOrigin = e.GetPosition(null);
        }
    }

    private void CategoryChip_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragCategorySource is null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (!PassedThreshold(_dragCategoryOrigin, e.GetPosition(null))) return;
        var data = _dragCategorySource;
        _dragCategorySource = null;
        try { System.Windows.DragDrop.DoDragDrop((DependencyObject)sender, data, System.Windows.DragDropEffects.Move); }
        catch { /* drag aborted */ }
    }

    private void CategoryChip_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(CategoryItem)) is CategoryItem source && source.CanManage && sender is ListBoxItem target && target.DataContext is CategoryItem destination && destination.CanManage ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void CategoryChip_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not ListBoxItem targetItem || targetItem.DataContext is not CategoryItem targetCat) return;
        if (e.Data.GetData(typeof(CategoryItem)) is not CategoryItem sourceCat) return;
        if (!sourceCat.CanManage || !targetCat.CanManage || ReferenceEquals(sourceCat, targetCat)) return;

        var list = _viewModel.TopCategories;
        var srcIdx = list.IndexOf(sourceCat);
        var tgtIdx = list.IndexOf(targetCat);
        if (srcIdx < 0 || tgtIdx < 0) return;

        list.Move(srcIdx, tgtIdx);
        e.Handled = true;
        var snapshot = list.ToList();
        await _viewModel.ReorderCategoriesAsync(snapshot);
    }

    // ---- 话术行拖�?----
    private void PhraseItem_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsContextMenuItem(d)) return;
        if (sender is ListBoxItem item && item.DataContext is PhraseItemViewModel phrase && phrase.CanManage)
        {
            _dragPhraseSource = phrase;
            _dragPhraseOrigin = e.GetPosition(null);
        }
    }

    private void PhraseItem_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragPhraseSource is null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (!PassedThreshold(_dragPhraseOrigin, e.GetPosition(null))) return;
        var data = _dragPhraseSource;
        _dragPhraseSource = null;
        try { System.Windows.DragDrop.DoDragDrop((DependencyObject)sender, data, System.Windows.DragDropEffects.Move); }
        catch { /* drag aborted */ }
    }

    private void PhraseItem_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PhraseItemViewModel)) is PhraseItemViewModel src && sender is ListBoxItem item && item.DataContext is PhraseItemViewModel tgt && src.CanManage && tgt.CanManage && src.CategoryId == tgt.CategoryId)
            e.Effects = System.Windows.DragDropEffects.Move;
        else
            e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void PhraseItem_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not ListBoxItem targetItem || targetItem.DataContext is not PhraseItemViewModel targetPhrase) return;
        if (e.Data.GetData(typeof(PhraseItemViewModel)) is not PhraseItemViewModel sourcePhrase) return;
        if (!sourcePhrase.CanManage || !targetPhrase.CanManage || ReferenceEquals(sourcePhrase, targetPhrase)) return;
        if (sourcePhrase.CategoryId != targetPhrase.CategoryId) return; // 仅允许同分类内重�?

        var ordered = _viewModel.VisibleItems.OfType<PhraseItemViewModel>()
            .Where(p => p.CategoryId == sourcePhrase.CategoryId)
            .ToList();
        var srcIdx = ordered.IndexOf(sourcePhrase);
        var tgtIdx = ordered.IndexOf(targetPhrase);
        if (srcIdx < 0 || tgtIdx < 0) return;

        // 本地先按拖拽结果重排（仅调整内存顺序，暂不改 SortOrder）；
        // 真正�?SortOrder 重排与持久化交给 ReorderPhrasesAsync，它在写库后会统一重建列表�?
        var moved = ordered[srcIdx];
        ordered.RemoveAt(srcIdx);
        ordered.Insert(tgtIdx, moved);
        e.Handled = true;
        await _viewModel.ReorderPhrasesAsync(sourcePhrase.CategoryId, ordered);
    }

    // ---- 小工具：避免在右键菜单项上误触发拖拽 ----
    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null and not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private static bool IsContextMenuItem(DependencyObject d)
    {
        for (var cur = d; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (cur is System.Windows.Controls.ContextMenu || cur is System.Windows.Controls.MenuItem) return true;
        return false;
    }
}
