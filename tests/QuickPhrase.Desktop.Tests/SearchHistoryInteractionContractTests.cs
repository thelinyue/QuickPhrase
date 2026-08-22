using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickPhrase.Core;
using QuickPhrase.Desktop;
using QuickPhrase.Desktop.Tests.Fakes;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 搜索历史的显示与保存时机契约：话术库输入过程展示候选历史，
/// 闪念仅在空查询时展示历史；只有话术库确认搜索或闪念成功插入后才持久化关键词。
/// </summary>
public sealed class SearchHistoryInteractionContractTests
{
    [Fact]
    public void LibrarySearchHistory_OpensOnFocusAndInput_AndPersistsOnlyConfirmedQueries()
    {
        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "LibraryView.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "LibraryView.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "ViewModels", "PhraseLibraryViewModel.cs"));

        Assert.Contains("TextChanged=\"SearchBox_TextChanged\"", markup, StringComparison.Ordinal);
        Assert.Contains("private void SearchBox_TextChanged", code, StringComparison.Ordinal);
        Assert.Contains("OpenSearchHistory();", ExtractMethod(code, "private void SearchBox_TextChanged", "private void SearchBox_LostKeyboardFocus"), StringComparison.Ordinal);
        Assert.Contains("await RecordConfirmedSearchAsync(query);", ExtractMethod(code, "private async void SearchHistoryPanel_QuerySelected", "private async void SearchHistoryPanel_ClearRequested"), StringComparison.Ordinal);
        Assert.Contains("await RecordConfirmedSearchAsync(_viewModel.SearchQuery);", ExtractMethod(code, "private async void SearchBox_KeyDown", "// ============"), StringComparison.Ordinal);
        Assert.DoesNotContain("_recordSearchHistory", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("历史搜索保存失败", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void LibrarySearchHistoryPopup_RemainsOpenWhileSearchBoxHasFocus_AndClosesAfterFocusMovesAway()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var history = new SearchHistoryCoordinator(new RecordingSearchHistoryRepository());
            history.InitializeAsync().GetAwaiter().GetResult();
            var view = new LibraryView(new FakeCommandService(), history);
            var window = new Window { Content = view, Width = 900, Height = 560 };

            try
            {
                window.Show();
                view.SearchBox.Focus();
                Keyboard.Focus(view.SearchBox);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.True(view.SearchHistoryPopup.StaysOpen);
                Assert.True(view.SearchHistoryPopup.IsOpen);

                Keyboard.Focus(view.RootLayout);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.False(view.SearchHistoryPopup.IsOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LauncherHistory_IsBelowSearchBox_AndUsesResultsViewportWhenQueryEntered()
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "desktop", "QuickPhrase.Desktop", "LauncherWindow.xaml"));

        var historyStart = markup.IndexOf("<Border x:Name=\"SearchHistoryHost\"", StringComparison.Ordinal);
        var queryStart = markup.IndexOf("<TextBox x:Name=\"QueryBox\"", StringComparison.Ordinal);
        var resultsStart = markup.IndexOf("<ListBox x:Name=\"ResultsList\"", StringComparison.Ordinal);

        Assert.True(queryStart >= 0 && queryStart < historyStart && historyStart < resultsStart);
        Assert.Contains("Grid.Row=\"0\"", markup[queryStart..historyStart], StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"1\"", markup[historyStart..resultsStart], StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"2\"", markup[resultsStart..], StringComparison.Ordinal);
        Assert.Contains("<RowDefinition Height=\"*\" />", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<Popup x:Name=\"SearchHistoryPopup\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherHistory_ShowsForEmptyQueryAndHidesWhenKeywordIsEntered()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var history = new SearchHistoryCoordinator(new RecordingSearchHistoryRepository());
            history.InitializeAsync().GetAwaiter().GetResult();
            var window = new LauncherWindow(new EmptySearchService(), history);
            try
            {
                window.Show();
                window.QueryBox.Focus();
                Keyboard.Focus(window.QueryBox);
                Assert.Equal(Visibility.Visible, window.SearchHistoryHost.Visibility);

                window.QueryBox.Text = "报价";
                Assert.Equal(Visibility.Collapsed, window.SearchHistoryHost.Visibility);

                window.HideLauncher();
                Assert.Equal(Visibility.Collapsed, window.SearchHistoryHost.Visibility);
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    [Fact]
    public void LauncherHistoryPill_MouseClickSearchesWithSelectedQuery()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var search = new RecordingSearchService();
            var history = new SearchHistoryCoordinator(new RecordingSearchHistoryRepository());
            history.InitializeAsync().GetAwaiter().GetResult();
            var window = new LauncherWindow(search, history, hideOnDeactivate: false);

            try
            {
                window.Show();
                window.QueryBox.Focus();
                Keyboard.Focus(window.QueryBox);
                window.UpdateLayout();

                var historyList = Assert.IsType<ListBox>(window.SearchHistoryPanel.FindName("HistoryList"));
                var pill = Assert.IsType<ListBoxItem>(historyList.ItemContainerGenerator.ContainerFromIndex(0));
                var mouseDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = ListBoxItem.MouseLeftButtonDownEvent,
                };

                pill.RaiseEvent(mouseDown);

                Assert.Equal("回访", window.QueryBox.Text);
                Assert.Contains(search.Requests, request => request.Query == "回访");
                Assert.Equal(Visibility.Collapsed, window.SearchHistoryHost.Visibility);
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    [Fact]
    public void LauncherHistory_PersistsOnlySuccessfulInsertedDeliveryQueries()
    {
        var controller = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "desktop", "QuickPhrase.Desktop", "ApplicationController.cs"));
        var method = ExtractMethod(controller, "private async Task RecordSearchHistoryIfSuccessfulAsync", "private void ShowDeliveryNotification");

        Assert.Contains("!result.IsSuccess || !result.Inserted || string.IsNullOrWhiteSpace(query)", method, StringComparison.Ordinal);
        Assert.Contains("await _searchHistory.RecordAsync(query);", method, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到方法标记：{startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"找不到方法结束边界：{endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }

    private sealed class EmptySearchService : ISearchService
    {
        public SearchIndexStatus Status { get; } = new(SearchIndexState.Ready, 0);

        public SearchResponse Search(SearchRequest request) =>
            new(ImmutableArray<SearchResult>.Empty, Status);
    }

    private sealed class RecordingSearchService : ISearchService
    {
        public List<SearchRequest> Requests { get; } = [];
        public SearchIndexStatus Status { get; } = new(SearchIndexState.Ready, 1);

        public SearchResponse Search(SearchRequest request)
        {
            Requests.Add(request);
            return new SearchResponse(ImmutableArray<SearchResult>.Empty, Status);
        }
    }

    private sealed class RecordingSearchHistoryRepository : ISearchHistoryRepository
    {
        public Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchHistoryEntry>>([new SearchHistoryEntry("回访", DateTimeOffset.UtcNow)]);

        public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<SearchHistoryEntry>.Success(
                new SearchHistoryEntry(query.Trim(), DateTimeOffset.UtcNow)));

        public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<bool>.Success(true));
    }
}
