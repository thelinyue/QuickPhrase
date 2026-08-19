using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Views.Shared;

/// <summary>
/// 锟斤拷锟斤拷锟斤拷史锟斤拷签锟斤拷濉ｏ拷锟斤拷只锟斤拷锟斤拷锟斤拷趾锟窖★拷瘢锟斤拷锟斤拷锟斤拷锟斤拷锟斤拷锟斤拷锟斤拷锟斤拷锟诫，
/// 锟斤拷锟斤拷锟斤拷锟斤拷锟节撅拷锟斤拷锟斤拷锟斤拷锟角╋拷锟斤拷锟斤拷锟斤拷锟斤拷锟藉，锟接讹拷锟斤拷证锟斤拷锟斤拷锟节猴拷 Launcher 锟斤拷为一锟铰★拷
/// </summary>
public partial class SearchHistoryView : System.Windows.Controls.UserControl
{
    private bool _suppressSelectionEvent;
    public SearchHistoryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachCollectionNotifications();
        Loaded += (_, _) => AttachCollectionNotifications();
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

    private void AttachCollectionNotifications()
    {
        if (DataContext is not SearchHistoryViewModel viewModel) return;
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        if (viewModel.Entries is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= Collection_CollectionChanged;
            collection.CollectionChanged += Collection_CollectionChanged;
        }
        _suppressSelectionEvent = true;
        try
        {
            HistoryList.ItemsSource = viewModel.Entries;
        }
        finally
        {
            _suppressSelectionEvent = false;
        }
        UpdateState();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchHistoryViewModel.Entries) or nameof(SearchHistoryViewModel.HasEntries))
        {
            if (sender is SearchHistoryViewModel viewModel)
            {
                _suppressSelectionEvent = true;
                try
                {
                    HistoryList.ItemsSource = viewModel.Entries;
                }
                finally
                {
                    _suppressSelectionEvent = false;
                }
                UpdateState();
            }
        }
    }

    private void Collection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        var hasEntries = HistoryList.Items.Count > 0;
        HistoryList.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = hasEntries;
        if (!hasEntries) HistoryList.SelectedIndex = -1;
    }
}


