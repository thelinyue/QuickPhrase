using System.IO;
using System.Collections.Immutable;
using System.Reflection;
using System.Windows.Input;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop.Tests;

public sealed class LauncherRuntimeStabilityTests
{
    [Fact]
    public void LauncherHistoryIsRenderedInsideWindowInsteadOfIndependentPopupHwnd()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "desktop",
            "QuickPhrase.Desktop",
            "LauncherWindow.xaml"));

        Assert.DoesNotContain("<Popup x:Name=\"SearchHistoryPopup\"", xaml);
        Assert.Contains("<Border x:Name=\"SearchHistoryHost\"", xaml);
    }
    [Fact]
    public void CtrlEnterBypassesSearchHistorySelectionAndReachesPhraseSubmission()
    {
        Assert.True(LauncherWindow.ShouldSelectSearchHistoryEntry(Key.Enter, ModifierKeys.None, hasSelection: true));
        Assert.False(LauncherWindow.ShouldSelectSearchHistoryEntry(Key.Enter, ModifierKeys.Control, hasSelection: true));
        Assert.False(LauncherWindow.ShouldSelectSearchHistoryEntry(Key.Enter, ModifierKeys.Control | ModifierKeys.Shift, hasSelection: true));
    }
    [Fact]
    public void LauncherShowsHistoryForEmptyQueryAndHidesItAfterKeywordEntry()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var search = new RecordingSearchService();
            var history = new SearchHistoryCoordinator(new SearchHistoryRepository("回访"));
            history.InitializeAsync().GetAwaiter().GetResult();
            var window = new LauncherWindow(search, history);
            try
            {
                window.Open();

                Assert.Single(search.Requests);
                Assert.Equal(string.Empty, search.Requests[0].Query);
                Assert.Equal(5, search.Requests[0].Limit);
                Assert.Equal(System.Windows.Visibility.Visible, window.QueryHintText.Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, window.ResultsList.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.PreviewHost.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.EmptyState.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.SearchRetryState.Visibility);
                Assert.Equal(-1, window.ResultsList.SelectedIndex);

                window.QueryBox.Focus();
                Keyboard.Focus(window.QueryBox);
                Assert.Equal(System.Windows.Visibility.Visible, window.SearchHistoryHost.Visibility);

                window.QueryBox.Text = "报价";
                Assert.Equal(2, search.Requests.Count);
                Assert.Equal("报价", search.Requests[1].Query);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.QueryHintText.Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, window.ResultsList.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.SearchHistoryHost.Visibility);
                Assert.Equal(0, window.ResultsList.SelectedIndex);

                window.QueryBox.Text = "   ";
                Assert.Equal(3, search.Requests.Count);
                Assert.Equal(string.Empty, search.Requests[2].Query);
                Assert.Equal(5, search.Requests[2].Limit);
                Assert.Equal(System.Windows.Visibility.Visible, window.QueryHintText.Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, window.ResultsList.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.PreviewHost.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.EmptyState.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.SearchRetryState.Visibility);
                Assert.Equal(-1, window.ResultsList.SelectedIndex);
                Assert.Equal(System.Windows.Visibility.Visible, window.SearchHistoryHost.Visibility);
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }
    [Fact]
    public void LauncherHidesHistoryAndKeepsFlatSurfacePaddingWhenNoHistoryExists()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var search = new EmptySearchService();
            var window = new LauncherWindow(
                search,
                new SearchHistoryCoordinator(new EmptySearchHistoryRepository()));
            try
            {
                window.Open();
                window.QueryBox.Focus();
                Keyboard.Focus(window.QueryBox);

                Assert.Equal(System.Windows.Visibility.Collapsed, window.SearchHistoryHost.Visibility);
                Assert.Single(search.Requests);
                Assert.Equal(string.Empty, search.Requests[0].Query);
                Assert.Equal(5, search.Requests[0].Limit);
                Assert.Equal(-1, window.ResultsList.SelectedIndex);
                Assert.Equal(68d, window.Height);
                Assert.Null(window.LauncherSurface.Style);
                Assert.Equal(new System.Windows.Thickness(16), window.LauncherSurface.Padding);
                Assert.Null(window.LauncherSurface.Effect);
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }
    [Fact]
    public void LauncherHidesHistoryForPrefilledKeyword()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var search = new RecordingSearchService();
            var history = new SearchHistoryCoordinator(new SearchHistoryRepository("回访"));
            history.InitializeAsync().GetAwaiter().GetResult();
            var window = new LauncherWindow(search, history);
            try
            {
                window.Open("报价");

                Assert.NotEmpty(search.Requests);
                Assert.All(search.Requests, request => Assert.Equal("报价", request.Query));
                Assert.Equal(System.Windows.Visibility.Collapsed, window.SearchHistoryHost.Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, window.ResultsList.Visibility);
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }
    [Fact]
    public void HiddenLauncherCannotReopenSearchHistoryHostFromQueuedFocusCallback()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var history = new SearchHistoryCoordinator(new EmptySearchHistoryRepository());
            var window = new LauncherWindow(new EmptySearchService(), history);
            try
            {
                window.Show();
                window.HideLauncher();

                var openSearchHistory = typeof(LauncherWindow).GetMethod(
                    "OpenSearchHistory",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(openSearchHistory);
                openSearchHistory.Invoke(window, null);

                Assert.False(window.IsVisible);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.SearchHistoryHost.Visibility);
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
    private sealed class RecordingSearchService : ISearchService
    {
        private readonly SearchResult _result = new(
            new Phrase(
                Guid.NewGuid(),
                "报价",
                PhraseBody.FromText("您好，报价已准备好。"),
                Guid.NewGuid(),
                ShortcutMode.None,
                null,
                0,
                null,
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            SearchMatchKind.TitleContains);

        public List<SearchRequest> Requests { get; } = [];
        public SearchIndexStatus Status { get; } = new(SearchIndexState.Ready, 1);

        public SearchResponse Search(SearchRequest request)
        {
            Requests.Add(request);
            return new SearchResponse(ImmutableArray.Create(_result), Status);
        }
    }
    private sealed class EmptySearchService : ISearchService
    {
        public List<SearchRequest> Requests { get; } = [];
        public SearchIndexStatus Status { get; } = new(SearchIndexState.Ready, 0);

        public SearchResponse Search(SearchRequest request)
        {
            Requests.Add(request);
            return new SearchResponse(ImmutableArray<SearchResult>.Empty, Status);
        }
    }

    private sealed class SearchHistoryRepository(params string[] queries) : ISearchHistoryRepository
    {
        private readonly IReadOnlyList<SearchHistoryEntry> _entries = queries
            .Select(query => new SearchHistoryEntry(query, DateTimeOffset.UtcNow))
            .ToArray();

        public Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries);

        public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(
            string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<SearchHistoryEntry>.Success(
                new SearchHistoryEntry(query.Trim(), DateTimeOffset.UtcNow)));

        public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<bool>.Success(true));
    }
    private sealed class EmptySearchHistoryRepository : ISearchHistoryRepository
    {
        public Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchHistoryEntry>>([]);

        public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(
            string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<SearchHistoryEntry>.Success(
                new SearchHistoryEntry(query.Trim(), DateTimeOffset.UtcNow)));

        public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<bool>.Success(true));
    }
}
