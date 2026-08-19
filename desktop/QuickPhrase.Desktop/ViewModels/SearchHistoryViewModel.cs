using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>搜索历史展示状态。两处搜索框共享同一实例，但各自维护 Popup 的键盘高亮索引。</summary>
public partial class SearchHistoryViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<SearchHistoryEntry> _entries = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    public bool HasEntries => Entries.Count > 0;

    internal void Replace(IEnumerable<SearchHistoryEntry> entries)
    {
        Entries = new ObservableCollection<SearchHistoryEntry>(entries.Take(10));
        OnPropertyChanged(nameof(HasEntries));
    }

    internal void SetLoading(bool value) => IsLoading = value;

    internal void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        HasError = isError;
    }
}
