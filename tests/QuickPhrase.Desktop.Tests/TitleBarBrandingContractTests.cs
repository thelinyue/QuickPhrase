using System.IO;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 验证主窗口的品牌和设置入口位于标题栏，避免话术库重新出现重复的底部应用栏。
/// </summary>
public sealed class TitleBarBrandingContractTests
{
    [Fact]
    public void MainWindow_TitleBar_ShowsBrandAndSettingsBeforeWindowControls()
    {
        var titleBar = ReadDesktopFile("TitleBar.xaml");
        var mainWindow = ReadDesktopFile("MainWindow.xaml");

        Assert.Contains("Source=\"{StaticResource Image.Brand.AppIcon}\"", titleBar, StringComparison.Ordinal);
        Assert.Contains("Width=\"{StaticResource Size.TitleBar.BrandIcon}\"", titleBar, StringComparison.Ordinal);
        Assert.Contains("Height=\"{StaticResource Size.TitleBar.BrandIcon}\"", titleBar, StringComparison.Ordinal);
        Assert.Contains("Text=\"闪语 · \"", titleBar, StringComparison.Ordinal);
        Assert.Contains("Margin=\"{StaticResource Thickness.Gap.Inline.SM}\"", titleBar, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsButton\"", titleBar, StringComparison.Ordinal);
        Assert.True(
            titleBar.IndexOf("x:Name=\"SettingsButton\"", StringComparison.Ordinal) <
            titleBar.IndexOf("x:Name=\"MinButton\"", StringComparison.Ordinal),
            "设置按钮必须位于最小化按钮左侧。");
        Assert.Contains("ShowSettingsButton=\"True\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("PageTitle=\"话术库\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("SettingsRequested=\"TitleBar_SettingsRequested\"", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsButton_IsMainWindowOnlyAndForwardsToExistingSettingsCoordinator()
    {
        var titleBarCode = ReadDesktopFile("TitleBar.xaml.cs");
        var mainWindowCode = ReadDesktopFile("MainWindow.xaml.cs");
        var applicationController = ReadDesktopFile("ApplicationController.cs");
        var settingsWindow = ReadDesktopFile("SettingsWindow.xaml");
        var newPhraseWindow = ReadDesktopFile("NewPhraseWindow.xaml");

        Assert.Contains("new PropertyMetadata(false)", titleBarCode, StringComparison.Ordinal);
        Assert.Contains("SettingsRequested?.Invoke(this, e);", titleBarCode, StringComparison.Ordinal);
        Assert.Contains("SettingsRequested?.Invoke(this, EventArgs.Empty);", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("_management.SettingsRequested += (_, _) => OpenSettings();", applicationController, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowSettingsButton", settingsWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowSettingsButton", newPhraseWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleBarBrandIcon_UsesSixteenPixelSemanticToken()
    {
        var sizes = ReadDesktopFile("DesignSystem", "Tokens", "Sizes.xaml");

        Assert.Contains("x:Key=\"Size.TitleBar.BrandIcon\">16</sys:Double>", sizes, StringComparison.Ordinal);
        Assert.DoesNotContain("Size.Library.BrandIcon", sizes, StringComparison.Ordinal);
        Assert.DoesNotContain("Size.Library.SettingsIcon", sizes, StringComparison.Ordinal);
        Assert.DoesNotContain("Size.Library.Footer.Height", sizes, StringComparison.Ordinal);
        Assert.DoesNotContain("Thickness.Library.Footer", ReadDesktopFile("DesignSystem", "Tokens", "Thickness.xaml"), StringComparison.Ordinal);
    }

    private static string ReadDesktopFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
        return File.ReadAllText(Path.Combine(new[] { root, "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray()));
    }
}
