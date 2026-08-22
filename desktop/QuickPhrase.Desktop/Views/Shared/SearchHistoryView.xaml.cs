using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using QuickPhrase.Core;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Views.Shared;

/// <summary>
/// Launcher 与话术库共用的历史搜索单行视图。
///
/// 持久化层仍保存最近十条历史记录；本控件固定展示最近五条，
/// 让垃圾桶按钮和关键词标签始终保持在同一行。键盘选择只遍历当前可见记录。
/// </summary>
public partial class SearchHistoryView : System.Windows.Controls.UserControl
{
    private const int VisibleEntryCount = 5;

    private bool _suppressSelectionEvent;
    private SearchHistoryViewModel? _viewModel;
    private INotifyCollectionChanged? _entriesCollection;

    public SearchHistoryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
        Loaded += (_, _) => AttachViewModel();
        SizeChanged += (_, _) => RefreshVisibleEntries();
    }

    public event EventHandler<string>? QuerySelected;
    public event EventHandler? ClearRequested;

    public int SelectedIndex
    {
        get => HistoryList.SelectedIndex;
        set => HistoryList.SelectedIndex = value;
    }

    public bool HasSelection => HistoryList.SelectedItem is SearchHistoryEntry;

    public SearchHistoryEntry? SelectedEntry => HistoryList.SelectedItem as SearchHistoryEntry;

    /// <summary>
    /// 将历史标签区域的可用宽度转换为可见记录数。
    /// 闪念固定宽度下只展示五个浅色标签，键盘遍历范围必须与可见项一致。
    /// </summary>
    internal static int CalculateVisibleEntryLimit(double availableWidth)
        => VisibleEntryCount;

    public bool MoveSelection(int delta)
    {
        if (HistoryList.Items.Count == 0) return false;
        var next = HistoryList.SelectedIndex < 0
            ? (delta > 0 ? 0 : HistoryList.Items.Count - 1)
            : Math.Clamp(HistoryList.SelectedIndex + delta, 0, HistoryList.Items.Count - 1);
        _suppressSelectionEvent = true;
        try
        {
            HistoryList.SelectedIndex = next;
        }
        finally
        {
            _suppressSelectionEvent = false;
        }
        HistoryList.ScrollIntoView(HistoryList.SelectedItem);
        return true;
    }

    public void ClearSelection() => HistoryList.SelectedIndex = -1;

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateState();
        if (!_suppressSelectionEvent && HistoryList.SelectedItem is SearchHistoryEntry entry && e.AddedItems.Count > 0)
            QuerySelected?.Invoke(this, entry.Query);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => ClearRequested?.Invoke(this, EventArgs.Empty);

    private void AttachViewModel()
    {
        if (!ReferenceEquals(_viewModel, DataContext))
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;

            _viewModel = DataContext as SearchHistoryViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        AttachCollectionNotifications(_viewModel?.Entries);
        RefreshVisibleEntries();
    }

    private void AttachCollectionNotifications(INotifyCollectionChanged? collection)
    {
        if (ReferenceEquals(_entriesCollection, collection)) return;

        if (_entriesCollection is not null)
            _entriesCollection.CollectionChanged -= Collection_CollectionChanged;

        _entriesCollection = collection;
        if (_entriesCollection is not null)
            _entriesCollection.CollectionChanged += Collection_CollectionChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchHistoryViewModel.Entries) or nameof(SearchHistoryViewModel.HasEntries))
        {
            AttachCollectionNotifications(_viewModel?.Entries);
            RefreshVisibleEntries();
        }
    }

    private void Collection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshVisibleEntries();

    private void RefreshVisibleEntries()
    {
        var selectedEntry = SelectedEntry;
        var visibleLimit = CalculateVisibleEntryLimit(HistoryHost.ActualWidth);
        HistoryList.Tag = visibleLimit;
        var visibleEntries = _viewModel?.Entries.Take(visibleLimit).ToArray() ?? [];

        _suppressSelectionEvent = true;
        try
        {
            HistoryList.ItemsSource = visibleEntries;
            HistoryList.SelectedItem = selectedEntry is null
                ? null
                : visibleEntries.FirstOrDefault(entry => entry == selectedEntry);
        }
        finally
        {
            _suppressSelectionEvent = false;
        }

        UpdateState();
    }

    private void UpdateState()
    {
        var hasEntries = HistoryList.Items.Count > 0;
        HistoryList.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = hasEntries;
        if (!hasEntries) HistoryList.SelectedIndex = -1;
    }
}
