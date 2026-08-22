using System;
using System.IO;

namespace QuickPhrase.Desktop.Tests.Theme;

/// <summary>
/// 锁定应用图标在桌面和托盘中的呈现边界，避免标题栏重复显示品牌图标。
/// </summary>
public sealed class ApplicationIconPresentationTests
{
    [Fact]
    public void TrayIcon_UsesTheCurrentSystemSmallIconSize()
    {
        var controller = ReadDesktopFile("ApplicationController.cs");

        Assert.Contains("new Icon(iconStream, Forms.SystemInformation.SmallIconSize)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("icon = new Icon(iconStream);", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleBar_DoesNotRenderTheApplicationBrandIcon()
    {
        var components = ReadDesktopFile("DesignSystem", "Components", "Components.xaml");
        var titleBar = ReadDesktopFile("TitleBar.xaml");
        var sizes = ReadDesktopFile("DesignSystem", "Tokens", "Sizes.xaml");

        Assert.DoesNotContain("Image.Brand.AppIcon", components, StringComparison.Ordinal);
        Assert.DoesNotContain("Image.Brand.AppIcon", titleBar, StringComparison.Ordinal);
        Assert.DoesNotContain("Size.TitleBar.BrandIcon", sizes, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleBarWindows_ContinueToUseTheMultiResolutionApplicationIcon()
    {
        const string iconUri = "Icon=\"pack://application:,,,/QuickPhrase;component/Assets/quickphrase.ico\"";

        foreach (var window in new[] { "MainWindow.xaml", "SettingsWindow.xaml", "NewPhraseWindow.xaml" })
            Assert.Contains(iconUri, ReadDesktopFile(window), StringComparison.Ordinal);
    }

    private static string ReadDesktopFile(params string[] segments)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray());
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到 QuickPhrase.sln，无法定位仓库根目录。");
    }
}
