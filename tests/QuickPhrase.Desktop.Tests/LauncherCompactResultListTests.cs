using System.Collections.Immutable;
using System.IO;
using System.Windows.Input;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// Launcher 搜索结果与话术库紧凑行的回归约束。
/// 换行正文只能在完整预览中展开，搜索列表必须始终保持固定单行高度。
/// </summary>
public sealed class LauncherCompactResultListTests
{
    [Fact]
    public void LauncherUsesDedicatedCompactRowWhileLibraryKeepsItsSharedTemplate()
    {
        var root = FindRepositoryRoot();
        var launcher = ReadDesktopFile(root, "LauncherWindow.xaml");
        var library = ReadDesktopFile(root, "Views", "LibraryView.xaml");
        var sharedRows = ReadDesktopFile(root, "DesignSystem", "Styles", "Lists.xaml");

        Assert.Contains("LauncherPhraseTemplate", launcher);
        Assert.DoesNotContain("ContentTemplate=\"{StaticResource Template.Phrase.CompactRow}\"", launcher);
        Assert.Contains("Template.Phrase.CompactRow", library);
        Assert.DoesNotContain("Template.Library.CompactPhraseRow", library);
        Assert.Contains("<DataTemplate x:Key=\"Template.Phrase.CompactRow\">", sharedRows);
        Assert.Contains("Height=\"{StaticResource Size.Phrase.Row.Compact}\"", sharedRows);
        Assert.Contains("TextWrapping=\"NoWrap\"", sharedRows);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", sharedRows);
        Assert.Contains("ItemContainerStyle=\"{StaticResource Style.Launcher.ListItem.Phrase}\"", launcher);
        Assert.Contains("x:Key=\"Style.ListItem.Phrase.Compact\"", sharedRows);
        Assert.Contains("Size.Launcher.Row.Height", launcher);
    }

    [Fact]
    public void LauncherSendButtonUsesTheSameSingleSubmissionPathAsCtrlEnter()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var history = new SearchHistoryCoordinator(new EmptySearchHistoryRepository());
            var window = new LauncherWindow(new SingleResultSearchService(), history, hideOnDeactivate: false);
            var target = new DeliveryTarget(
                "wechat-work",
                "Desktop",
                "wechat-work",
                "企业微信",
                "runtime-key",
                DateTimeOffset.UtcNow);
            var deliveries = new List<SendMode>();

            window.DeliveryRequested += (_, mode, _, _, batchConfirmed) => deliveries.Add(mode);
            try
            {
                window.Open("报价", target, canExplicitSend: true);

                Assert.True(PhraseListActions.GetShowSendButton(window.ResultsList));
                window.ResultsList.UpdateLayout();
                var row = Assert.IsType<System.Windows.Controls.ListBoxItem>(
                    window.ResultsList.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Equal(28d, row.ActualHeight);

                var command = Assert.IsAssignableFrom<ICommand>(PhraseListActions.GetSendCommand(window.ResultsList));
                var item = Assert.IsType<LauncherPhraseListItem>(window.ResultsList.SelectedItem);

                command.Execute(item);
                command.Execute(item);

                Assert.Equal([SendMode.InsertAndSend], deliveries);

                window.Open("报价", target, canExplicitSend: false);
                Assert.False(PhraseListActions.GetShowSendButton(window.ResultsList));
            }
            finally
            {
                window.DisposeLauncher();
            }
        });
    }

    private static string ReadDesktopFile(string root, params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { root, "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray()));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln。");
    }

    private sealed class SingleResultSearchService : ISearchService
    {
        private readonly SearchResult _result = new(
            new Phrase(
                Guid.NewGuid(),
                "报价",
                PhraseBody.FromText("第一行正文。\r\n第二行正文。\r\n第三行正文。"),
                Guid.NewGuid(),
                ShortcutMode.None,
                null,
                0,
                null,
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow),
            SearchMatchKind.TitleContains);

        public SearchIndexStatus Status { get; } = new(SearchIndexState.Ready, 1);

        public SearchResponse Search(SearchRequest request) =>
            new(ImmutableArray.Create(_result), Status);
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
