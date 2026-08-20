using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;
using QuickPhrase.Desktop.Views.Shared;
using QuickPhrase.Desktop.Onboarding;

namespace QuickPhrase.Desktop;

/// <summary>
/// WPF Native Launcher。窗口实例会被隐藏后复用，搜索只访问 Core 的内存快照，
/// 不依赖数据库查询或外部页面运行时。历史搜索使用窗口内覆盖层并与话术搜索共用当前输入框，
/// 但历史确认只执行搜索，不直接插入话术。
/// </summary>
public partial class LauncherWindow : Window
{
    private readonly ISearchService _search;
    private readonly SearchHistoryCoordinator _searchHistory;
    private IReadOnlyList<SearchResult> _results = [];
    private IReadOnlyList<LauncherPhraseListItem> _items = [];
    private bool _closing;
    private bool _preview;
    private Phrase? _previewPhrase;
    private DeliveryTarget? _target;
    private Guid? _preferredPhraseId;
    private LauncherInvocationContext? _invocationContext;
    private readonly LauncherSubmissionGuard _submissionGuard = new();
    private const int PageSize = 5;
    private const double LauncherChromeHeight = 128;
    private const double PhraseRowHeight = 32;

    public LauncherWindow(ISearchService search, SearchHistoryCoordinator searchHistory)
    {
        _search = search;
        _searchHistory = searchHistory;
        InitializeComponent();
        SearchHistoryPanel.DataContext = _searchHistory.ViewModel;
        SearchRetryState.ActionCommand = new RelayCommand(RefreshResults);
        ResultsList.SelectedIndex = 0;
        Loaded += (_, _) => FocusSearchBox();
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => HideLauncher();
        Closing += OnClosing;
        UpdateTitleColumnWidth();
    }

    public event Action<Phrase, SendMode, DeliveryTarget?, string?>? DeliveryRequested;
    public event Action<string>? CreatePhraseRequested;
    public event Action? Hidden;
    public string SearchErrorText { get; private set; } = "搜索索引初始化失败，请重试。";

    public bool IsLauncherVisible => IsVisible;
    internal LauncherLifecycleState LifecycleState { get; private set; } = LauncherLifecycleState.Created;
    internal void MarkLifecycleFaulted() => LifecycleState = LauncherLifecycleState.Faulted;
    public bool IsPracticeMode => _invocationContext?.Mode == LauncherInvocationMode.Practice;

    public void Open(string initialQuery = "", DeliveryTarget? target = null, Guid? phraseId = null, AdapterStatusSnapshot? status = null, LauncherInvocationContext? invocationContext = null)
    {
        if (_closing) return;
        LifecycleState = LauncherLifecycleState.Activating;
        _target = target;
        _invocationContext = invocationContext;
        _preferredPhraseId = phraseId;
        _submissionGuard.Reset();
        var hasTarget = target is not null;
        TargetText.Text = hasTarget
            ? $"已捕获目标 · {target!.DisplayName} · 动作前会再次验证"
            : "未捕获目标 · 仅支持预览或安全复制";
        CapabilityText.Text = status is null
            ? "无目标 · 插入/发送不可用"
            : $"Profile {status.ProfileVersion ?? "未确认"} · 插入 {CapabilityLabel(status.InsertText)} · 显式发送 {CapabilityLabel(status.SendText)}";
        InsertHintText.Text = IsPracticeMode
            ? "Enter 选择到练习区"
            : hasTarget ? "Enter 插入" : "Enter 安全复制";
        SendHintText.Text = IsPracticeMode
            ? "练习模式不发送"
            : status?.SendText == CapabilityStatus.Verified
                ? "Ctrl+Enter 显式发送"
                : "Ctrl+Enter 当前目标不支持发送";

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

    internal void SetQueueStatus(DeliveryQueueStatus status)
    {
        QueueText.Text = status.IsProcessing ? $"处理中 · 等待 {status.WaitingCount}" : string.Empty;
        QueueText.Visibility = status.IsProcessing ? Visibility.Visible : Visibility.Collapsed;
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
        OpenSearchHistory();
    }

    private void OpenSearchHistory()
    {
        // Launcher 隐藏或进入关闭流程后，排队的 GotKeyboardFocus 回调不得重新显示历史覆盖层。
        if (!IsLoaded || !IsVisible || _closing) return;
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

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => RefreshResults();

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

        try
        {
            var response = _search.Search(new SearchRequest(QueryBox.Text ?? string.Empty, 8));
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
            UpdatePreviewPhrase();
        }
        catch (Exception exception)
        {
            _results = [];
            _items = [];
            ResultsList.ItemsSource = null;
            SearchErrorText = $"搜索索引不可用：{exception.Message}";
            _previewPhrase = null;
        }

        ApplyViewState();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePreviewPhrase();
        if (_preview) ApplyViewState();
    }

    private void UpdatePreviewPhrase()
    {
        _previewPhrase = (ResultsList.SelectedItem as LauncherPhraseListItem)?.Phrase;
        if (_previewPhrase is null) return;
        PreviewTitle.Text = _previewPhrase.Title;
        PreviewCategory.Text = "话术预览";
        PreviewContent.Text = _previewPhrase.Content;
    }

    /// <summary>根据列表项数计算 Launcher 高度，保持共享话术行的固定节奏。</summary>
    internal static double CalculateListHeight(int itemCount)
    {
        var safeCount = Math.Max(0, itemCount);
        return Math.Clamp(LauncherChromeHeight + safeCount * PhraseRowHeight, 260, 520);
    }

    private void ApplyViewState()
    {
        var hasResults = _items.Count > 0;
        var hasError = !string.IsNullOrWhiteSpace(SearchErrorText);
        ResultsList.Visibility = (!_preview && hasResults && !hasError) ? Visibility.Visible : Visibility.Collapsed;
        PreviewHost.Visibility = (_preview && hasResults && !hasError) ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = (!_preview && !hasResults && !hasError) ? Visibility.Visible : Visibility.Collapsed;
        LoadingState.Visibility = Visibility.Collapsed;
        SearchRetryState.Description = hasError ? SearchErrorText : "搜索索引初始化失败，请重试。";
        SearchRetryState.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        PreviewHintText.Text = _preview ? "Tab 返回列表 · Esc 关闭" : "Tab 预览 · Esc 关闭";
        if (_preview)
        {
            var contentLength = _previewPhrase?.Content.Length ?? 0;
            Height = Math.Clamp(300 + (contentLength / 35) * 16, 360, 640);
            MaxHeight = 640;
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
                if (!_submissionGuard.TrySubmit())
                {
                    e.Handled = true;
                    break;
                }
                if (ResultsList.SelectedItem is LauncherPhraseListItem item)
                {
                    // Enter 统一走选择入口；Practice 模式只回调向导。
                    var mode = ResolveSendMode(
                        IsPracticeMode,
                        (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control);
                    await SelectPhraseAsync(item.Phrase, mode);
                }
                else if (!string.IsNullOrWhiteSpace(QueryBox.Text))
                {
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
    private async Task SelectPhraseAsync(Phrase phrase, SendMode mode)
    {
        if (_invocationContext is { Mode: LauncherInvocationMode.Practice, SelectionHandler: not null } practice)
        {
            await practice.SelectionHandler(phrase);
            HideLauncher();
            return;
        }
        HideLauncher();
        DeliveryRequested?.Invoke(phrase, mode, _target, QueryBox.Text?.Trim());
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleColumnWidth();

    private void UpdateTitleColumnWidth()
    {
        if (!IsInitialized) return;
        var width = ResultsList.ActualWidth > 0 ? ResultsList.ActualWidth : Width - 32;
        PhraseListActions.SetTitleColumnWidth(ResultsList, width switch
        {
            >= 1024 => new GridLength(160),
            >= 768 => new GridLength(130),
            _ => new GridLength(100),
        });
    }

    private void PositionOnCurrentMonitor()
    {
        var workArea = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 3);
    }

    private static string CapabilityLabel(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Verified => "已验证",
        CapabilityStatus.Unverified => "未确认",
        CapabilityStatus.Unsupported => "不支持",
        _ => "未知",
    };

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
