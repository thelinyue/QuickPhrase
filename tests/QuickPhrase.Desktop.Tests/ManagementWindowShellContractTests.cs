using System.IO;

namespace QuickPhrase.Desktop.Tests;

/// <summary>主窗口贴边标题栏与系统行为的静态契约，防止最大化入口回归。</summary>
public sealed class ManagementWindowShellContractTests
{
    [Fact]
    public void TitleBar_ReplacesMaximizeWithDockToggleAndAccessibleVectorStates()
    {
        var xaml = Read("TitleBar.xaml");
        var code = Read("TitleBar.xaml.cs");

        Assert.DoesNotContain("MaxButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxButton_Click", code, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DockButton\"", xaml, StringComparison.Ordinal);
        Assert.True(xaml.IndexOf("x:Name=\"MinButton\"", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"DockButton\"", StringComparison.Ordinal));
        Assert.True(xaml.IndexOf("x:Name=\"DockButton\"", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"CloseButton\"", StringComparison.Ordinal));
        Assert.Contains("IsDocked", code, StringComparison.Ordinal);
        Assert.Contains("DockToggleRequested", code, StringComparison.Ordinal);
        Assert.Contains("贴到当前业务窗口右侧", xaml, StringComparison.Ordinal);
        Assert.Contains("解除贴边并恢复悬浮", xaml, StringComparison.Ordinal);
        Assert.True(xaml.Split("<Path", StringSplitOptions.None).Length >= 4, "贴边按钮至少包含窗口外框、边界线和状态箭头 Path。");
    }

    [Fact]
    public void MainWindow_IsTopmostCompactAndBlocksMaximizeSystemCommands()
    {
        var xaml = Read("MainWindow.xaml");
        var code = Read("MainWindow.xaml.cs");

        Assert.Contains("Topmost=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowDockButton=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DockToggleRequested=\"TitleBar_DockToggleRequested\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ManagementWindowMode.DockedRight", code, StringComparison.Ordinal);
        Assert.Contains("ManagementWindowPlacementCalculator.TryCalculateDockedRight", code, StringComparison.Ordinal);
        Assert.Contains("ResizeMode.NoResize", code, StringComparison.Ordinal);
        Assert.Contains("SystemCommandMaximize", code, StringComparison.Ordinal);
        Assert.Contains("WindowsNativeMethods.DisableMaximize", code, StringComparison.Ordinal);
        Assert.Contains("TraceId：", code, StringComparison.Ordinal);
        Assert.Contains("Stopwatch.GetElapsedTime", code, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowState.Maximized", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DockToggle_UsesRequestedModeSoWaitingDockCanBeCancelled()
    {
        var code = Read("MainWindow.xaml.cs");
        var toggleStart = code.IndexOf("private void TitleBar_DockToggleRequested", StringComparison.Ordinal);
        var toggleEnd = code.IndexOf("public void UpdateDockTarget", toggleStart, StringComparison.Ordinal);
        var floatingStart = code.IndexOf("private void ApplyFloatingBounds", StringComparison.Ordinal);
        var floatingEnd = code.IndexOf("private void CaptureFloatingBounds", floatingStart, StringComparison.Ordinal);

        Assert.True(toggleStart >= 0 && toggleEnd > toggleStart, "找不到贴边切换方法。");
        Assert.True(floatingStart >= 0 && floatingEnd > floatingStart, "找不到悬浮恢复方法。");
        Assert.Contains("if (_placement.Mode == ManagementWindowMode.DockedRight)", code[toggleStart..toggleEnd], StringComparison.Ordinal);
        Assert.Contains("if (!_isDocked) CaptureFloatingBounds();", code[toggleStart..toggleEnd], StringComparison.Ordinal);
        Assert.Contains("TitleBarControl.IsDocked = _placement.Mode == ManagementWindowMode.DockedRight", code[floatingStart..floatingEnd], StringComparison.Ordinal);
    }
    [Fact]
    public void LibraryDeliverySubscription_SurvivesTemporaryUnloadAndIsDisposedWhenWindowCloses()
    {
        var libraryCode = Read("Views", "LibraryView.xaml.cs");
        var mainWindowCode = Read("MainWindow.xaml.cs");
        var unloadStart = libraryCode.IndexOf("private void OnUnloaded", StringComparison.Ordinal);
        var unloadEnd = libraryCode.IndexOf("public void Dispose", unloadStart, StringComparison.Ordinal);

        Assert.True(unloadStart >= 0 && unloadEnd > unloadStart, "找不到话术库卸载处理方法。");
        Assert.DoesNotContain("_viewModel.Dispose()", libraryCode[unloadStart..unloadEnd], StringComparison.Ordinal);
        Assert.Contains("_libraryView?.Dispose()", mainWindowCode, StringComparison.Ordinal);
    }
    private static string Read(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName, "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray()));
    }
}
