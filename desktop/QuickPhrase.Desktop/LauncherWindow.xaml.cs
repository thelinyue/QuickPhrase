using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Views.Shared;
using QuickPhrase.Desktop.Onboarding;

namespace QuickPhrase.Desktop;

/// <summary>
/// WPF Native Launcher。窗口实例会被隐藏后复用，搜索只访问 Core 的内存快照，
/// 不依赖数据库查询或外部页面运行时。历史搜索在空查询时作为输入框下方的内联快捷入口显示，
/// 输入关键词后立即隐藏，避免与匹配话术争夺选择焦点；历史确认只执行搜索，不直接插入话术。
/// </summary>
public partial class LauncherWindow : Window
{
    private readonly ISearchService _search;
    private readonly SearchHistoryCoordinator _searchHistory;
    private readonly IMediaAssetStore? _mediaAssets;
    private IReadOnlyList<SearchResult> _results = [];
    private IReadOnlyList<LauncherPhraseListItem> _items = [];
    private bool _closing;
    private bool _preview;
    private DeliveryTarget? _target;
    private Guid? _preferredPhraseId;
    private LauncherInvocationContext? _invocationContext;
    private bool _canExplicitSend;
    private AdapterCapabilities _targetCapabilities = UnsupportedCapabilities;
    private readonly LauncherSubmissionGuard _submissionGuard = new();
    private const int PageSize = 5;
    // 紧凑空态仍需容纳 36px 搜索框及 Border 上下各 16px 留白；36px 窗口高度会裁切输入框。
    private const double CompactLauncherHeight = 68;
    // 搜索框、历史容器和上下留白完整容纳固定的五条历史标签，禁止自动聚焦时裁切最后一行。
    private const double HistoryLauncherHeight = 120;
    private const double LauncherChromeHeight = 128;
    private const double PhraseRowHeight = 28;

    public LauncherWindow(ISearchService search, SearchHistoryCoordinator searchHistory, bool hideOnDeactivate = true, IMediaAssetStore? mediaAssets = null)
    {
        _search = search;
        _searchHistory = searchHistory;
        _mediaAssets = mediaAssets;
        InitializeComponent();
        SearchHistoryPanel.DataContext = _searchHistory.ViewModel;
        SearchRetryState.ActionCommand = new RelayCommand(RefreshResults);
        ResultsList.SelectedIndex = 0;
        Loaded += (_, _) => FocusSearchBox();
        PreviewKeyDown += OnPreviewKeyDown;
        if (hideOnDeactivate)
            Deactivated += (_, _) => HideLauncher();
        Closing += OnClosing;
        PhraseListActions.SetSendCommand(ResultsList, new AsyncRelayCommand<LauncherPhraseListItem>(SendPhraseAsync));
    }

    public event Action<Phrase, SendMode, DeliveryTarget?, string?, bool>? DeliveryRequested;
    public event Action<string>? CreatePhraseRequested;
    public event Action? Hidden;
    public string SearchErrorText { get; private set; } = "搜索索引初始化失败，请重试。";

    public bool IsLauncherVisible => IsVisible;
    internal LauncherLifecycleState LifecycleState { get; private set; } = LauncherLifecycleState.Created;
    internal void MarkLifecycleFaulted() => LifecycleState = LauncherLifecycleState.Faulted;
    public bool IsPracticeMode => _invocationContext?.Mode == LauncherInvocationMode.Practice;

    public void Open(string initialQuery = "", DeliveryTarget? target = null, Guid? phraseId = null, bool canExplicitSend = false, LauncherInvocationContext? invocationContext = null, AdapterCapabilities? targetCapabilities = null)
    {
        if (_closing) return;
        LifecycleState = LauncherLifecycleState.Activating;
        _target = target;
        _invocationContext = invocationContext;
        _preferredPhraseId = phraseId;
        _submissionGuard.Reset();
        _canExplicitSend = canExplicitSend && !IsPracticeMode && target is not null;
        _targetCapabilities = targetCapabilities ?? CreateFallbackCapabilities(_canExplicitSend);
        PhraseListActions.SetShowSendButton(ResultsList, _canExplicitSend);
        var hasTarget = target is not null;
        InsertHintText.Text = IsPracticeMode
            ? "Enter 选择到练习区"
            : hasTarget ? "Enter 尝试插入，无法验证时安全复制" : "Enter 安全复制";
        SendHintText.Text = IsPracticeMode
            ? "练习模式不发送"
            : _canExplicitSend
                ? "Ctrl+Enter 插入并发送"
                : "Ctrl+Enter 当前目标不支持插入并发送";

        QueryBox.Text = initialQuery;
        _preview = false;
        RefreshResults();
        PositionOnCurrentMonitor();
        if (!IsVisible) Show(); else Activate();
        LifecycleState = LauncherLifecycleState.Visible;
        FocusSearchBox();
    }

    public void HideLauncher()
    {
        _canExplicitSend = false;
        PhraseListActions.SetShowSendButton(ResultsList, false);
        CloseSearchHistory();
        if (!IsVisible)
        {
            if (LifecycleState is not LauncherLifecycleState.Disposed)
                LifecycleState = LauncherLifecycleState.Hidden;
            return;
        }
        LifecycleState = LauncherLifecycleState.Hiding;
        Hide();
        LifecycleState = LauncherLifecycleState.Hidden;
        // Hide 之后再次收起窗口内历史覆盖层，作为排队焦点回调之前的最终关闭屏障。
        CloseSearchHistory();
        Hidden?.Invoke();
    }

    public void DisposeLauncher()
    {
        _closing = true;
        CloseSearchHistory();
        LifecycleState = LauncherLifecycleState.Disposed;
        Close();
    }

    public Task WaitForRenderAsync()
    {
        if (!IsVisible) return Task.CompletedTask;
        return Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render).Task;
    }

    /// <summary>
    /// 等待当前复用窗口完成 Render/Input 调度并获得搜索输入焦点；
    /// smoke 以此作为“热呼出可交互”的统一终点。
    /// </summary>
    internal async Task WaitForInteractiveAsync(CancellationToken cancellationToken)
    {
        await Dispatcher.InvokeAsync(
            () => { },
            System.Windows.Threading.DispatcherPriority.Render).Task.WaitAsync(cancellationToken);
        await Dispatcher.InvokeAsync(
            () => { },
            System.Windows.Threading.DispatcherPriority.Input).Task.WaitAsync(cancellationToken);

        if (!IsVisible || !QueryBox.IsVisible || !QueryBox.IsEnabled || !QueryBox.IsKeyboardFocusWithin)
        {
            throw new InvalidOperationException(
                $"Launcher 未进入可输入状态。Visible={IsVisible}，QueryVisible={QueryBox.IsVisible}，Enabled={QueryBox.IsEnabled}，Focus={QueryBox.IsKeyboardFocusWithin}。");
        }
        LifecycleState = LauncherLifecycleState.Interactive;
    }

    private void QueryBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => OpenSearchHistory();

    private void QueryBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ScheduleSearchHistoryClose();

    private void SearchHistoryHost_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ScheduleSearchHistoryClose();

    private void SearchHistoryPanel_QuerySelected(object? sender, string query)
    {
        QueryBox.Text = query;
        RefreshResults();
        CloseSearchHistory();
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);
    }

    private async void SearchHistoryPanel_ClearRequested(object? sender, EventArgs e)
    {
        if (SearchHistoryPanel.DataContext is not ViewModels.SearchHistoryViewModel vm || !vm.HasEntries) return;
        var answer = System.Windows.MessageBox.Show(this, "确定清除全部历史搜索记录吗？此操作不可撤销。", "清除全部历史搜索",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        await _searchHistory.ClearAsync();
        SearchHistoryPanel.ClearSelection();
        ApplyViewState();
    }

    private void OpenSearchHistory()
    {
        // Launcher 隐藏或进入关闭流程后，排队的 GotKeyboardFocus 回调不得重新显示历史快捷入口。
        if (!IsLoaded || !IsVisible || _closing || !IsSearchQueryEmpty(QueryBox.Text) || !_searchHistory.ViewModel.HasEntries) return;
        SearchHistoryHost.Visibility = Visibility.Visible;
    }

    private void CloseSearchHistory()
    {
        SearchHistoryPanel.ClearSelection();
        SearchHistoryHost.Visibility = Visibility.Collapsed;
    }

    private void ScheduleSearchHistoryClose()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!QueryBox.IsKeyboardFocusWithin && !SearchHistoryHost.IsKeyboardFocusWithin)
                CloseSearchHistory();
        }));
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        RefreshResults();
        // 空查询才提供历史快捷入口；出现关键词后只保留对应的搜索结果，避免分散选择焦点。
        if (IsSearchQueryEmpty(QueryBox.Text)) OpenSearchHistory();
        else CloseSearchHistory();
    }

    private void FocusSearchBox()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible) return;
            Activate();
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
            QueryBox.SelectAll();
        }));
    }

    private void RefreshResults()
    {
        if (!IsInitialized) return;

        var query = QueryBox.Text ?? string.Empty;
        if (IsSearchQueryEmpty(query))
        {
            var response = _search.Search(new SearchRequest(string.Empty, 5));
            _results = response.Items;
            _items = _results.Select((item, index) => LauncherPhraseListItem.FromPhrase(item.Phrase, index + 1)).ToArray();
            _preview = false;
            SearchErrorText = string.Empty;
            ResultsList.ItemsSource = _items;
            ResultsList.SelectedIndex = -1;
            ApplyViewState();
            return;
        }

        try
        {
            var response = _search.Search(new SearchRequest(query, 8));
            _results = response.Items;
            _items = _results
                .Select((item, index) => LauncherPhraseListItem.FromPhrase(item.Phrase, index + 1))
                .ToArray();
            if (_invocationContext is { Mode: LauncherInvocationMode.Practice, SearchHandler: not null } practice &&
                !string.IsNullOrWhiteSpace(QueryBox.Text))
            {
                practice.SearchHandler(QueryBox.Text.Trim(), response.Status);
            }
            SearchErrorText = string.Empty;
            ResultsList.ItemsSource = _items;

            var preferredIndex = -1;
            if (_preferredPhraseId.HasValue)
            {
                for (var index = 0; index < _items.Count; index++)
                {
                    if (_items[index].PhraseId != _preferredPhraseId.Value) continue;
                    preferredIndex = index;
                    break;
                }
            }
            ResultsList.SelectedIndex = preferredIndex >= 0 ? preferredIndex : _items.Count > 0 ? 0 : -1;
        }
        catch (Exception exception)
        {
            _results = [];
            _items = [];
            ResultsList.ItemsSource = null;
            SearchErrorText = $"搜索索引不可用：{exception.Message}";
        }

        ApplyViewState();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_preview) ApplyViewState();
    }


    private static bool IsSearchQueryEmpty(string? query) => string.IsNullOrWhiteSpace(query);

    /// <summary>根据列表项数计算 Launcher 高度，保持共享话术行的固定节奏。</summary>
    internal static double CalculateListHeight(int itemCount)
    {
        var safeCount = Math.Max(0, itemCount);
        return Math.Clamp(LauncherChromeHeight + safeCount * PhraseRowHeight, 260, 520);
    }

    private void ApplyViewState()
    {
        var isSearchQueryEmpty = IsSearchQueryEmpty(QueryBox.Text);
        var hasSearchHistory = _searchHistory.ViewModel.HasEntries;
        var showSearchHistory = isSearchQueryEmpty && hasSearchHistory && IsVisible && !_closing;
        var hasResults = _items.Count > 0;
        var hasSelectedResult = ResultsList.SelectedItem is LauncherPhraseListItem;
        var hasError = !string.IsNullOrWhiteSpace(SearchErrorText);

        SearchHistoryHost.Visibility = showSearchHistory ? Visibility.Visible : Visibility.Collapsed;
        QueryHintText.Visibility = isSearchQueryEmpty ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = (!_preview && hasResults && !hasError) ? Visibility.Visible : Visibility.Collapsed;
        PreviewHost.Visibility = (!isSearchQueryEmpty && _preview && hasSelectedResult && !hasError) ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = (!isSearchQueryEmpty && !_preview && !hasResults && !hasError) ? Visibility.Visible : Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Collapsed;
        SearchRetryState.Description = hasError ? SearchErrorText : "搜索索引初始化失败，请重试。";
        SearchRetryState.Visibility = !isSearchQueryEmpty && hasError ? Visibility.Visible : Visibility.Collapsed;
        KeyboardHints.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        PreviewHintText.Text = _preview ? "Tab 返回列表 · Esc 关闭" : "Tab 预览 · Esc 关闭";

        if (isSearchQueryEmpty && !hasResults && !hasSearchHistory)
        {
            Height = CompactLauncherHeight;
            MaxHeight = CompactLauncherHeight;
        }
        else if (isSearchQueryEmpty)
        {
            Height = CalculateListHeight(_items.Count) + (hasSearchHistory ? 44 : 0);
            MaxHeight = 520;
        }
        else if (_preview && hasSelectedResult)
        {
            Height = CalculateListHeight(1);
            MaxHeight = 520;
        }
        else
        {
            Height = CalculateListHeight(_items.Count);
            MaxHeight = 520;
        }
    }

    private async void OnResultsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_submissionGuard.TrySubmit())
        {
            e.Handled = true;
            return;
        }
        if (ResultsList.SelectedItem is LauncherPhraseListItem item)
        {
            await SelectPhraseAsync(item.Phrase, SendMode.InsertOnly);
        }
        e.Handled = true;
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu) return;
        var items = menu.Items.OfType<System.Windows.Controls.MenuItem>().ToArray();
        if (items.Length < 2) return;
        items[0].Header = IsPracticeMode ? "选择到练习区" : "发送到输入区";
        items[0].InputGestureText = IsPracticeMode ? "Enter" : "双击";
        items[1].Header = IsPracticeMode ? "练习模式不使用剪贴板" : "复制内容到剪贴板";
        items[1].IsEnabled = !IsPracticeMode;
    }

    private async void OnInsertContextMenuClick(object sender, RoutedEventArgs e)
    {
        if (!_submissionGuard.TrySubmit()) return;
        var item = GetContextMenuItem(sender);
        if (item is null) return;
        await SelectPhraseAsync(item.Phrase, SendMode.InsertOnly);
    }

    private void OnCopyContextMenuClick(object sender, RoutedEventArgs e)
    {
        // Practice 只允许把选中正文回调给向导，禁止通过右键菜单绕过安全边界写入剪贴板。
        if (IsPracticeMode) return;
        var item = GetContextMenuItem(sender);
        if (item is not null) System.Windows.Clipboard.SetText(item.Content);
    }

    private static LauncherPhraseListItem? GetContextMenuItem(object sender)
    {
        if (sender is not System.Windows.Controls.MenuItem menuItem) return null;
        if (menuItem.DataContext is LauncherPhraseListItem item) return item;
        return ((menuItem.Parent as System.Windows.Controls.ContextMenu)?.PlacementTarget as FrameworkElement)?.DataContext as LauncherPhraseListItem;
    }

    private async void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (SearchHistoryHost.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Down && SearchHistoryPanel.MoveSelection(1)) { e.Handled = true; return; }
            if (e.Key == Key.Up && SearchHistoryPanel.MoveSelection(-1)) { e.Handled = true; return; }
            if (ShouldSelectSearchHistoryEntry(
                    e.Key,
                    Keyboard.Modifiers,
                    SearchHistoryPanel.SelectedEntry is not null))
            {
                SearchHistoryPanel_QuerySelected(this, SearchHistoryPanel.SelectedEntry!.Query);
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Key.Escape:
                HideLauncher();
                e.Handled = true;
                break;
            case Key.Down:
                ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, Math.Max(0, _items.Count - 1));
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex - 1, 0);
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Tab:
                _preview = !_preview;
                ApplyViewState();
                e.Handled = true;
                break;
            case Key.Home:
                if (_items.Count > 0)
                {
                    ResultsList.SelectedIndex = 0;
                    ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.End:
                if (_items.Count > 0)
                {
                    ResultsList.SelectedIndex = _items.Count - 1;
                    ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.PageUp:
                if (_items.Count > 0)
                {
                    ResultsList.SelectedIndex = Math.Max(0, ResultsList.SelectedIndex - PageSize);
                    ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.PageDown:
                if (_items.Count > 0)
                {
                    ResultsList.SelectedIndex = Math.Min(_items.Count - 1, ResultsList.SelectedIndex + PageSize);
                    ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (ResultsList.SelectedItem is LauncherPhraseListItem item)
                {
                    // Enter 与发送按钮共用提交防抖和投递入口；Practice 模式仍只选择话术。
                    var mode = ResolveSendMode(
                        IsPracticeMode,
                        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
                    await SubmitPhraseAsync(item, mode);
                }
                else if (!string.IsNullOrWhiteSpace(QueryBox.Text))
                {
                    if (!_submissionGuard.TrySubmit())
                    {
                        e.Handled = true;
                        break;
                    }

                    // Practice 无结果时停留在真实闪念中，不得逃逸到正式新建话术流程。
                    if (IsPracticeMode)
                    {
                        e.Handled = true;
                        break;
                    }
                    CreatePhraseRequested?.Invoke(QueryBox.Text.Trim());
                    HideLauncher();
                }
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 历史搜索只消费无修饰键的普通 Enter。Ctrl+Enter 是通用显式发送手势，
    /// 即使 Popup 中存在选中历史项也必须继续进入话术投递分支。
    /// </summary>
    internal static bool ShouldSelectSearchHistoryEntry(Key key, ModifierKeys modifiers, bool hasSelection) =>
        key == Key.Enter && modifiers == ModifierKeys.None && hasSelection;

    /// <summary>
    /// 将 Launcher 内本次 Enter 意图转换为通用投递模式。练习模式永远只选择话术，
    /// 不因 Ctrl 修饰键进入真实发送流程。
    /// </summary>
    internal static SendMode ResolveSendMode(bool isPracticeMode, bool controlPressed) =>
        !isPracticeMode && controlPressed ? SendMode.InsertAndSend : SendMode.InsertOnly;
    /// <summary>
    /// 发送图标与 Ctrl+Enter 复用这一入口：同一窗口会话只允许一次投递，
    /// 后续仍由应用控制器和 Platform.Windows 执行目标重校验与安全发送。
    /// </summary>
    private async Task SubmitPhraseAsync(LauncherPhraseListItem item, SendMode mode)
    {
        if (!_submissionGuard.TrySubmit())
        {
            return;
        }

        await SelectPhraseAsync(item.Phrase, mode);
    }

    /// <summary>
    /// 发送图标只在当前目标允许显式发送时可见。点击语义等同 Ctrl+Enter，
    /// 不提供后台发送、自动重试或跳过目标验证的额外路径。
    /// </summary>
    private async Task SendPhraseAsync(LauncherPhraseListItem? item)
    {
        if (!_canExplicitSend || item is null)
        {
            return;
        }

        await SubmitPhraseAsync(item, SendMode.InsertAndSend);
    }

    private async Task SelectPhraseAsync(Phrase phrase, SendMode mode)
    {
        if (_invocationContext is { Mode: LauncherInvocationMode.Practice, SelectionHandler: not null } practice)
        {
            await practice.SelectionHandler(phrase);
            HideLauncher();
            return;
        }
        if (phrase.Body.RequiresBatchDelivery)
        {
            var confirmation = mode == SendMode.InsertAndSend;
            var preview = new BatchPreviewWindow(phrase, _mediaAssets, confirmation, _targetCapabilities) { Owner = this };
            preview.ShowDialog();
            if (!confirmation || !preview.Confirmed) { _submissionGuard.Reset(); return; }
            HideLauncher();
            DeliveryRequested?.Invoke(phrase, mode, _target, QueryBox.Text?.Trim(), true);
            return;
        }
        HideLauncher();
        DeliveryRequested?.Invoke(phrase, mode, _target, QueryBox.Text?.Trim(), false);
    }


    private static AdapterCapabilities CreateFallbackCapabilities(bool canExplicitSend) =>
        new(
            CapabilityStatus.Unverified,
            CapabilityStatus.Unverified,
            CapabilityStatus.Unsupported,
            CapabilityStatus.Unsupported,
            canExplicitSend ? CapabilityStatus.Verified : CapabilityStatus.Unsupported,
            CapabilityStatus.Unsupported);

    private static AdapterCapabilities UnsupportedCapabilities { get; } = CreateFallbackCapabilities(false);

    private void PositionOnCurrentMonitor()
    {
        var workArea = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 3);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            HideLauncher();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
