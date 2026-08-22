using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop;
using QuickPhrase.Desktop.Views.Shared;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 闪念历史记录的固定视区回归测试。
/// 该测试覆盖自动聚焦后的初始布局，确保窗口不会裁切历史行，键盘也不会选择未展示的第六项。
/// </summary>
public sealed class LauncherHistoryViewportTests
{
    [Fact]
    public void EmptyLauncher_OpenKeepsSearchBoxInsideCompactWindow()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var history = new SearchHistoryCoordinator(new EmptySearchHistoryRepository());
            var window = new LauncherWindow(new EmptySearchService(), history, hideOnDeactivate: false);
            try
            {
                window.Open();
                window.UpdateLayout();

                var searchBoxBottom = window.QueryBox
                    .TransformToAncestor(window)
                    .Transform(new Point(0, window.QueryBox.ActualHeight)).Y;

                Assert.Equal(70d, window.ActualHeight);
                Assert.True(searchBoxBottom <= window.ActualHeight,
                    $"搜索框底部 {searchBoxBottom} 不应超过 Launcher 高度 {window.ActualHeight}。");
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(480)]
    [InlineData(760)]
    [InlineData(1200)]
    public void HistoryVisibleEntryLimit_IsAlwaysFive(double availableWidth)
    {
        Assert.Equal(5, SearchHistoryView.CalculateVisibleEntryLimit(availableWidth));
    }

    [Fact]
    public void LauncherAutoFocus_ShowsFiveHistoryEntriesWithoutClippingOrSelectingTheSixth()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var history = new SearchHistoryCoordinator(new SixEntrySearchHistoryRepository());
            history.InitializeAsync().GetAwaiter().GetResult();
            var window = new LauncherWindow(new EmptySearchService(), history);
            try
            {
                window.Show();
                window.QueryBox.Focus();
                Keyboard.Focus(window.QueryBox);
                window.UpdateLayout();

                var historyList = Assert.IsType<ListBox>(window.SearchHistoryPanel.FindName("HistoryList"));
                Assert.Equal(Visibility.Visible, window.SearchHistoryHost.Visibility);
                Assert.Equal(5, historyList.Items.Count);

                for (var index = 0; index < 6; index++)
                    Assert.True(window.SearchHistoryPanel.MoveSelection(1));

                Assert.Equal("历史关键词 5", window.SearchHistoryPanel.SelectedEntry?.Query);

                var historyBottom = window.SearchHistoryHost
                    .TransformToAncestor(window)
                    .Transform(new Point(0, window.SearchHistoryHost.ActualHeight)).Y;
                Assert.True(historyBottom <= window.ActualHeight,
                    $"历史区域底部 {historyBottom} 不应超过 Launcher 高度 {window.ActualHeight}。");
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    private sealed class EmptySearchService : ISearchService
    {
        public SearchIndexStatus Status { get; } = new(SearchIndexState.Ready, 0);

        public SearchResponse Search(SearchRequest request) =>
            new(ImmutableArray<SearchResult>.Empty, Status);
    }

    private sealed class EmptySearchHistoryRepository : ISearchHistoryRepository
    {
        public Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchHistoryEntry>>([]);

        public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<SearchHistoryEntry>.Success(
                new SearchHistoryEntry(query.Trim(), DateTimeOffset.UtcNow)));

        public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<bool>.Success(true));
    }

    private sealed class SixEntrySearchHistoryRepository : ISearchHistoryRepository
    {
        public Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchHistoryEntry>>(
                Enumerable.Range(1, 6)
                    .Select(index => new SearchHistoryEntry($"历史关键词 {index}", DateTimeOffset.UtcNow.AddMinutes(-index)))
                    .ToArray());

        public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<SearchHistoryEntry>.Success(
                new SearchHistoryEntry(query.Trim(), DateTimeOffset.UtcNow)));

        public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<bool>.Success(true));
    }
}
