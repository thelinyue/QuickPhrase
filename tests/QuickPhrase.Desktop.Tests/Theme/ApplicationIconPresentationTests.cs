using System;
using System.IO;

namespace QuickPhrase.Desktop.Tests.Theme;

/// <summary>
/// 锁定应用图标在桌面、托盘与 WPF 品牌位的尺寸选择，避免低分辨率 ICO 图层被放大后变得模糊。
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
    public void BrandIcon_Uses48PixelSourceForIts16DipTitleBarPresentation()
    {
        var components = ReadDesktopFile("DesignSystem", "Components", "Components.xaml");
        var titleBar = ReadDesktopFile("TitleBar.xaml");
        var sizes = ReadDesktopFile("DesignSystem", "Tokens", "Sizes.xaml");

        Assert.Contains("DecodePixelWidth=\"48\"", components, StringComparison.Ordinal);
        Assert.Contains("DecodePixelHeight=\"48\"", components, StringComparison.Ordinal);
        Assert.Contains("Size.TitleBar.BrandIcon\">16<", sizes, StringComparison.Ordinal);
        Assert.Contains("Width=\"{StaticResource Size.TitleBar.BrandIcon}\"", titleBar, StringComparison.Ordinal);
        Assert.Contains("Height=\"{StaticResource Size.TitleBar.BrandIcon}\"", titleBar, StringComparison.Ordinal);
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
