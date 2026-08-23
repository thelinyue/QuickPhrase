using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.Tests.Fakes;

namespace QuickPhrase.Desktop.Tests;

/// <summary>窗口定位使用纯 DIP 几何，避免 WPF UI 测试依赖真实显示器或 HWND。</summary>
public sealed class ManagementWindowPlacementTests
{
    [Fact]
    public void DockedRight_AlignsToVisibleTargetRightEdge()
    {
        var snapshot = CreateSnapshot(
            target: new DesktopWindowBounds(100, 50, 1000, 700),
            workArea: new DesktopWindowBounds(0, 0, 1920, 1040));

        var success = ManagementWindowPlacementCalculator.TryCalculateDockedRight(snapshot, out var result);

        Assert.True(success);
        Assert.Equal(new DesktopWindowBounds(720, 50, 380, 700), result);
    }

    [Fact]
    public void DockedRight_UsesVisibleIntersectionWithWorkArea()
    {
        var snapshot = CreateSnapshot(
            target: new DesktopWindowBounds(100, -40, 1000, 900),
            workArea: new DesktopWindowBounds(0, 0, 1920, 800));

        var success = ManagementWindowPlacementCalculator.TryCalculateDockedRight(snapshot, out var result);

        Assert.True(success);
        Assert.Equal(new DesktopWindowBounds(720, 0, 380, 800), result);
    }

    [Fact]
    public void DockedRight_WhenTargetCannotFitMinimumPanel_RemainsFloating()
    {
        var snapshot = CreateSnapshot(
            target: new DesktopWindowBounds(100, 100, 360, 500),
            workArea: new DesktopWindowBounds(0, 0, 1920, 1040));

        Assert.False(ManagementWindowPlacementCalculator.TryCalculateDockedRight(snapshot, out _));
    }

    [Fact]
    public void DefaultMainWindowPlacement_PreservesNullCoordinatesUntilInitialCentering()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var window = new MainWindow(
                new FakeCommandService(),
                new SearchHistoryCoordinator(new EmptySearchHistoryRepository()),
                "library",
                placement: ManagementWindowPlacement.Default);

            Assert.Null(window.CurrentPlacement.FloatingLeft);
            Assert.Null(window.CurrentPlacement.FloatingTop);
        });
    }

    [Fact]
    public void FloatingBounds_AreClampedIntoWorkArea()
    {
        var placement = new ManagementWindowPlacement(ManagementWindowMode.Floating, 1800, 900, 520, 680);
        var workArea = new DesktopWindowBounds(0, 0, 1920, 1040);

        var result = ManagementWindowPlacementCalculator.ClampFloating(placement, workArea);

        Assert.Equal(new DesktopWindowBounds(1400, 360, 520, 680), result);
    }

    private static WindowsDockTargetSnapshot CreateSnapshot(DesktopWindowBounds target, DesktopWindowBounds workArea) =>
        new(
            new DeliveryTarget("wecom", "WindowsDesktopWindow", "wecom", "企业微信", "runtime", DateTimeOffset.UtcNow),
            target,
            workArea,
            IsAvailable: true);

    private sealed class EmptySearchHistoryRepository : ISearchHistoryRepository
    {
        public Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchHistoryEntry>>([]);

        public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<SearchHistoryEntry>.Success(new SearchHistoryEntry(query.Trim(), DateTimeOffset.UtcNow)));

        public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RepositoryResult<bool>.Success(true));
    }
}
