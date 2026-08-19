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
/// 不依赖数据库查询或外部页面运行时。历史搜索 Popup 与话术搜索共用当前输入框，
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

    public event Action<Phrase, bool, DeliveryTarget?, string?>? DeliveryRequested;
    public event Action<string>? CreatePhraseRequested;
    public event Action? Hidden;
    public string SearchErrorText { get; private set; } = "搜索索引初始化失败，请重试。";

    public bool IsLauncherVisible => IsVisible;
    public bool IsPracticeMode => _invocationContext?.Mode == LauncherInvocationMode.Practice;

    public void Open(string initialQuery = "", DeliveryTarget? target = null, Guid? phraseId = null, AdapterStatusSnapshot? status = null, LauncherInvocationContext? invocationContext = null)
    {
        if (_closing) return;
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
            : $"Profile {status.ProfileVersion ?? "未确认"} · 插入 {CapabilityLabel(status.InsertText)} · 自动发送 {CapabilityLabel(status.SendText)}";
        InsertHintText.Text = IsPracticeMode
            ? "Enter 选择到练习区"
            : hasTarget ? "Enter 插入" : "Enter 安全复制";
        SendHintText.Text = "自动发送不支持";
        SendHintText.Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        QueryBox.Text = initialQuery;
        _preview = false;
        RefreshResults();
        PositionOnCurrentMonitor();
        if (!IsVisible) Show(); else Activate();
        FocusSearchBox();
    }

    public void HideLauncher()
    {
        CloseSearchHistory();
        if (!IsVisible) return;
        Hide();
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
        Close();
    }

    public Task WaitForRenderAsync()
    {
        if (!IsVisible) return Task.CompletedTask;
        return Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render).Task;
    }

    private void QueryBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => OpenSearchHistory();

    private void QueryBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ScheduleSearchHistoryClose();

    private void SearchHistoryPopup_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ScheduleSearchHistoryClose();

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
        if (!IsLoaded) return;
        SearchHistoryPopup.IsOpen = true;
    }

    private void CloseSearchHistory()
    {
        SearchHistoryPanel.ClearSelection();
        SearchHistoryPopup.IsOpen = false;
    }

    private void ScheduleSearchHistoryClose()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!QueryBox.IsKeyboardFocusWithin && !SearchHistoryPopup.IsKeyboardFocusWithin)
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
            await SelectPhraseAsync(item.Phrase);
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
        await SelectPhraseAsync(item.Phrase);
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
        if (SearchHistoryPopup.IsOpen)
        {
            if (e.Key == Key.Down && SearchHistoryPanel.MoveSelection(1)) { e.Handled = true; return; }
            if (e.Key == Key.Up && SearchHistoryPanel.MoveSelection(-1)) { e.Handled = true; return; }
            if (e.Key == Key.Enter && SearchHistoryPanel.SelectedEntry is not null)
            {
                SearchHistoryPanel_QuerySelected(this, SearchHistoryPanel.SelectedEntry.Query);
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
                    await SelectPhraseAsync(item.Phrase);
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

    private async Task SelectPhraseAsync(Phrase phrase)
    {
        if (_invocationContext is { Mode: LauncherInvocationMode.Practice, SelectionHandler: not null } practice)
        {
            await practice.SelectionHandler(phrase);
            HideLauncher();
            return;
        }
        HideLauncher();
        DeliveryRequested?.Invoke(phrase, false, _target, QueryBox.Text?.Trim());
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
