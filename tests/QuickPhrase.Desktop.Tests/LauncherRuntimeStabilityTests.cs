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

        public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(
            string query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<SearchHistoryEntry>.Success(
                new SearchHistoryEntry(query.Trim(), DateTimeOffset.UtcNow)));

        public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<bool>.Success(true));
    }
}