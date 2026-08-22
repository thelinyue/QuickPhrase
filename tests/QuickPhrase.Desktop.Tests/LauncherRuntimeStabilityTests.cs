using System.Collections.Immutable;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
                Assert.Equal(58d, window.Height);
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
    public void EmptyLauncherCentersItsSearchAreaAndPlacesHintAfterTheCaret()
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
                Assert.InRange(queryContentStart, 11.5, 13.5);
                Assert.InRange(Math.Abs(hintContentStart - queryContentStart - 4), 0, 0.5);
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
    public void KeywordSearchHidesHintAndKeepsQueryAtTheSameVerticalPosition()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var window = new LauncherWindow(new RecordingSearchService(), new SearchHistoryCoordinator(new SearchHistoryRepository()));
            try
            {
                window.Open();
                window.UpdateLayout();
                var emptyQueryBoxOrigin = window.QueryBox.TransformToAncestor(window.LauncherSurface).Transform(new Point());

                window.QueryBox.Text = "报价";
                window.UpdateLayout();

                var queryBoxOrigin = window.QueryBox.TransformToAncestor(window.LauncherSurface).Transform(new Point());
                var resultsOrigin = window.ResultsList.TransformToAncestor(window.LauncherSurface).Transform(new Point());

                Assert.Equal(Visibility.Collapsed, window.QueryHintText.Visibility);
                Assert.InRange(Math.Abs(queryBoxOrigin.Y - emptyQueryBoxOrigin.Y), 0, 1);
                Assert.Equal(136d, window.ActualHeight);
                Assert.True(resultsOrigin.Y >= queryBoxOrigin.Y + window.QueryBox.ActualHeight,
                    $"结果区顶部 {resultsOrigin.Y} 应位于搜索框底部 {queryBoxOrigin.Y + window.QueryBox.ActualHeight} 之后。");
                Assert.Equal(136d, window.Height);
                Assert.Equal(HorizontalAlignment.Left, window.QueryBox.HorizontalContentAlignment);
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    [Fact]
    public void NoResultsStateExpandsEnoughToShowItsDescription()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var window = new LauncherWindow(new EmptySearchService(), new SearchHistoryCoordinator(new SearchHistoryRepository()));
            try
            {
                window.Open();
                window.QueryBox.Text = "不存在的话术";
                window.UpdateLayout();

                var emptyStateBottom = window.EmptyState
                    .TransformToAncestor(window.LauncherSurface)
                    .Transform(new Point(0, window.EmptyState.ActualHeight)).Y;
                var description = FindVisualDescendant<TextBlock>(
                    window.EmptyState,
                    text => string.Equals(text.Text, window.EmptyState.Description, StringComparison.Ordinal));
                var descriptionBottom = description
                    .TransformToAncestor(window.LauncherSurface)
                    .Transform(new Point(0, description.ActualHeight)).Y;

                Assert.Equal(Visibility.Visible, window.EmptyState.Visibility);
                Assert.Equal(176d, window.ActualHeight);
                Assert.True(description.ActualHeight > 0, "无结果说明文字必须参与实际布局。");
                Assert.True(emptyStateBottom <= window.LauncherSurface.ActualHeight,
                    $"无结果说明底部 {emptyStateBottom} 不应超过浮层高度 {window.LauncherSurface.ActualHeight}。");
                Assert.True(descriptionBottom <= window.LauncherSurface.ActualHeight,
                    $"无结果说明文字底部 {descriptionBottom} 不应超过浮层高度 {window.LauncherSurface.ActualHeight}。");
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    [Fact]
    public void SearchErrorStateExpandsEnoughToShowItsRetryAction()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var window = new LauncherWindow(new ThrowingSearchService(), new SearchHistoryCoordinator(new SearchHistoryRepository()));
            try
            {
                window.Open();
                window.QueryBox.Text = "触发搜索错误";
                window.UpdateLayout();

                var retryButton = FindVisualDescendant<Button>(
                    window.SearchRetryState,
                    button => string.Equals(button.Content?.ToString(), "重试", StringComparison.Ordinal));
                var retryBottom = retryButton
                    .TransformToAncestor(window.LauncherSurface)
                    .Transform(new Point(0, retryButton.ActualHeight)).Y;

                Assert.Equal(Visibility.Visible, window.SearchRetryState.Visibility);
                Assert.Equal(Visibility.Visible, retryButton.Visibility);
                Assert.Equal(212d, window.ActualHeight);
                Assert.True(retryBottom <= window.LauncherSurface.ActualHeight,
                    $"搜索重试按钮底部 {retryBottom} 不应超过浮层高度 {window.LauncherSurface.ActualHeight}。");
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
                Assert.Equal(142d, window.Height);

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

    private sealed class EmptySearchService : ISearchService
    {
        public SearchIndexStatus Status { get; } = new(SearchIndexState.Ready, 0);

        public SearchResponse Search(SearchRequest request) =>
            new(ImmutableArray<SearchResult>.Empty, Status);
    }

    private sealed class ThrowingSearchService : ISearchService
    {
        public SearchIndexStatus Status { get; } = new(SearchIndexState.Dirty, 0);

        public SearchResponse Search(SearchRequest request) =>
            throw new InvalidOperationException("审计用搜索失败");
    }

    private static T FindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match)) return match;

            var descendant = TryFindVisualDescendant(child, predicate);
            if (descendant is not null) return descendant;
        }

        throw new InvalidOperationException($"找不到 {typeof(T).Name} 视觉子元素。");
    }

    private static T? TryFindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match)) return match;

            var descendant = TryFindVisualDescendant(child, predicate);
            if (descendant is not null) return descendant;
        }

        return null;
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
