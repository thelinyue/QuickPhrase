using System.IO;

namespace QuickPhrase.Desktop.Tests;

public sealed class ApplicationControllerLibraryDeliveryContractTests
{
    [Fact]
    public void Controller_ComposesTrackerCoordinatorAndPersistsWindowPlacement()
    {
        var controller = Read("ApplicationController.cs");
        var mainWindow = Read("MainWindow.xaml.cs");

        Assert.Contains("new WindowsDockTargetTracker(_targetDetector)", controller, StringComparison.Ordinal);
        Assert.Contains("new LibraryDeliveryCoordinator", controller, StringComparison.Ordinal);
        Assert.Contains("deliveryAuthorized: true", controller, StringComparison.Ordinal);
        Assert.Contains("_management.UpdateDockTarget", controller, StringComparison.Ordinal);
        Assert.Contains("_settings?.ManagementWindow", controller, StringComparison.Ordinal);
        Assert.Contains("ManagementWindow = placement", controller, StringComparison.Ordinal);
        Assert.Contains("_placementSaveTask = SaveManagementPlacementAsync", controller, StringComparison.Ordinal);
        Assert.Contains("await _placementSaveTask", controller, StringComparison.Ordinal);
        var saveStart = controller.IndexOf("private async Task SaveManagementPlacementAsync", StringComparison.Ordinal);
        var saveEnd = controller.IndexOf("private void OnManagementClosed", saveStart, StringComparison.Ordinal);
        Assert.True(saveStart >= 0 && saveEnd > saveStart, "找不到管理窗口位置保存方法。");
        var saveCode = controller[saveStart..saveEnd];
        Assert.Contains("TraceId：", saveCode, StringComparison.Ordinal);
        Assert.Contains("Stopwatch.GetElapsedTime", saveCode, StringComparison.Ordinal);
        Assert.Contains("_dockTargetTracker.Dispose()", controller, StringComparison.Ordinal);
        Assert.Contains("new LibraryView(_commands, _searchHistory, _libraryDelivery)", mainWindow, StringComparison.Ordinal);
    }

    private static string Read(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "desktop", "QuickPhrase.Desktop", file));
    }
}

