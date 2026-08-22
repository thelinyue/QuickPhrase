using System.Collections.Immutable;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop.Tests;

/// <summary>Launcher 空查询、历史入口与键盘安全语义的运行时回归测试。</summary>
public sealed class LauncherRuntimeStabilityTests
{
    [Fact]
    public void CtrlEnterBypassesSearchHistorySelectionAndReachesPhraseSubmission()
    {
        Assert.True(LauncherWindow.ShouldSelectSearchHistoryEntry(Key.Enter, ModifierKeys.None, hasSelection: true));
        Assert.False(LauncherWindow.ShouldSelectSearchHistoryEntry(Key.Enter, ModifierKeys.Control, hasSelection: true));
    }

    [Fact]
    public void EmptyLauncherDoesNotSearchAndStaysCompactWithoutHistory()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var search = new RecordingSearchService();
            var window = new LauncherWindow(search, new SearchHistoryCoordinator(new SearchHistoryRepository()));
            try
            {
                window.Open();

                Assert.Empty(search.Requests);
                Assert.Equal(System.Windows.Visibility.Visible, window.QueryHintText.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.ResultsList.Visibility);
                Assert.Equal(70d, window.Height);
                Assert.NotNull(window.LauncherSurface.Style);

                window.QueryBox.Text = "报价";
                Assert.Single(search.Requests);
                Assert.Equal(System.Windows.Visibility.Visible, window.ResultsList.Visibility);
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    [Fact]
    public void EmptyLauncherCentersItsSearchAreaAndPlacesHintAtTheInputContentStart()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var window = new LauncherWindow(new RecordingSearchService(), new SearchHistoryCoordinator(new SearchHistoryRepository()));
            try
            {
                window.Open();
                window.UpdateLayout();

                var queryBoxOrigin = window.QueryBox.TransformToAncestor(window.LauncherSurface).Transform(new Point());
                var hintOrigin = window.QueryHintText.TransformToAncestor(window.LauncherSurface).Transform(new Point());
                var surfaceCenterY = window.LauncherSurface.ActualHeight / 2;
                var queryContentStart = queryBoxOrigin.X + window.QueryBox.Padding.Left;
                var hintContentStart = hintOrigin.X + window.QueryHintText.Padding.Left;

                Assert.InRange(Math.Abs(queryBoxOrigin.Y + window.QueryBox.ActualHeight / 2 - surfaceCenterY), 0, 1);
                Assert.InRange(Math.Abs(hintContentStart - queryContentStart), 0, 1);
                Assert.Equal(HorizontalAlignment.Left, window.QueryHintText.HorizontalAlignment);
                Assert.True(System.Windows.Controls.Panel.GetZIndex(window.QueryBox) > System.Windows.Controls.Panel.GetZIndex(window.QueryHintText));
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    [Fact]
    public void KeywordSearchHidesCenteredHintAndKeepsQueryAtTheTopLeft()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var window = new LauncherWindow(new RecordingSearchService(), new SearchHistoryCoordinator(new SearchHistoryRepository()));
            try
            {
                window.Open();
                window.QueryBox.Text = "报价";
                window.UpdateLayout();

                var queryBoxOrigin = window.QueryBox.TransformToAncestor(window.LauncherSurface).Transform(new Point());

                Assert.Equal(Visibility.Collapsed, window.QueryHintText.Visibility);
                Assert.InRange(queryBoxOrigin.Y, 0, 1);
                Assert.Equal(HorizontalAlignment.Left, window.QueryBox.HorizontalContentAlignment);
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    [Fact]
    public void EmptyLauncherShowsLoadedHistoryAndHidesItForKeywords()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var history = new SearchHistoryCoordinator(new SearchHistoryRepository("回访"));
            history.InitializeAsync().GetAwaiter().GetResult();
            var window = new LauncherWindow(new RecordingSearchService(), history);
            try
            {
                window.Open();
                window.QueryBox.Focus();
                Keyboard.Focus(window.QueryBox);

                Assert.Equal(System.Windows.Visibility.Visible, window.SearchHistoryHost.Visibility);
                Assert.Equal(128d, window.Height);

                window.QueryBox.Text = "报价";
                Assert.Equal(System.Windows.Visibility.Collapsed, window.SearchHistoryHost.Visibility);
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
            var window = new LauncherWindow(new RecordingSearchService(), new SearchHistoryCoordinator(new SearchHistoryRepository()));
            try
            {
                window.Show();
                window.HideLauncher();
                var openSearchHistory = typeof(LauncherWindow).GetMethod("OpenSearchHistory", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(openSearchHistory);
                openSearchHistory.Invoke(window, null);
                Assert.Equal(System.Windows.Visibility.Collapsed, window.SearchHistoryHost.Visibility);
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    private sealed class RecordingSearchService : ISearchService
    {
        private readonly SearchResult _result = new(
            new Phrase(Guid.NewGuid(), "报价", PhraseBody.FromText("您好，报价已准备好。"), Guid.NewGuid(), ShortcutMode.None, null,
                0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            SearchMatchKind.TitleContains,
            "未分类");

        public List<SearchRequest> Requests { get; } = [];
        public SearchIndexStatus Status { get; } = new(SearchIndexState.Ready, 1);

        public SearchResponse Search(SearchRequest request)
        {
            Requests.Add(request);
            return new SearchResponse(ImmutableArray.Create(_result), Status);
        }
    }

    private sealed class SearchHistoryRepository(params string[] queries) : ISearchHistoryRepository
    {
        public Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchHistoryEntry>>(queries.Select(query => new SearchHistoryEntry(query, DateTimeOffset.UtcNow)).ToArray());

        public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<SearchHistoryEntry>.Success(new SearchHistoryEntry(query.Trim(), DateTimeOffset.UtcNow)));

        public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<bool>.Success(true));
    }
}
