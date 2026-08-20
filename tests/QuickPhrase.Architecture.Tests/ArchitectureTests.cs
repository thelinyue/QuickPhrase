using System.Xml.Linq;

namespace QuickPhrase.Architecture.Tests;

/// <summary>
/// 纯 WPF 架构边界回归测试：验证正式桌面代码不再携带 Web/IPC 管理链路，
/// 并持续保持 Core → Platform.Windows → Desktop 的职责边界。
/// </summary>
public sealed class ArchitectureTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 工作区根目录。");
        }
    }

    [Fact]
    public void ProjectReferencesFollowFrozenDirection()
    {
        var core = Load("desktop/QuickPhrase.Core/QuickPhrase.Core.csproj");
        var platform = Load("desktop/QuickPhrase.Platform.Windows/QuickPhrase.Platform.Windows.csproj");
        var desktop = Load("desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj");

        Assert.Empty(ProjectReferences(core));
        Assert.Equal(["..\\QuickPhrase.Core\\QuickPhrase.Core.csproj"], ProjectReferences(platform));
        Assert.Equal(
            ["..\\QuickPhrase.Core\\QuickPhrase.Core.csproj", "..\\QuickPhrase.Platform.Windows\\QuickPhrase.Platform.Windows.csproj"],
            ProjectReferences(desktop));
        Assert.Empty(PackageReferences(core));
        Assert.Equal(["Microsoft.Data.Sqlite", "PinyinM.NET"], PackageReferences(platform));
        Assert.Equal(["CommunityToolkit.Mvvm"], PackageReferences(desktop));
    }
    [Fact]
    public void LibrarySearchDoesNotDeclareCtrlKShortcut()
    {
        var libraryXaml = File.ReadAllText(Path.Combine(Root, "desktop/QuickPhrase.Desktop/Views/LibraryView.xaml"));
        var libraryCode = File.ReadAllText(Path.Combine(Root, "desktop/QuickPhrase.Desktop/Views/LibraryView.xaml.cs"));
        var controls = File.ReadAllText(Path.Combine(Root, "desktop/QuickPhrase.Desktop/Themes/Controls.xaml"));

        Assert.DoesNotContain("Ctrl+K", libraryXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("case Key.K when", libraryCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ctrl K", controls, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoreDoesNotContainPlatformLeakage()
    {
        var core = Directory.GetFiles(Path.Combine(Root, "desktop/QuickPhrase.Core"), "*.cs", SearchOption.AllDirectories);
        var content = string.Join('\n', core.Select(File.ReadAllText));

        Assert.DoesNotContain("WebView2", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Windows.UI", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ManagementRequest", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IUiAutomationWorker", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IClipboardTransaction", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IDatabaseWriteQueue", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IHotkeyService", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionDesktopIsPureWpfAndHasNoBridgeSymbols()
    {
        var desktopProject = File.ReadAllText(Path.Combine(Root, "desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj"));
        Assert.Contains("<UseWPF>true</UseWPF>", desktopProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WebView2", desktopProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("React", desktopProject, StringComparison.OrdinalIgnoreCase);

        var sources = Directory.GetFiles(Path.Combine(Root, "desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));
        var content = string.Join('\n', sources.Select(File.ReadAllText));
        Assert.DoesNotContain("ManagementBridge", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ManagementRequest", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ManagementResponse", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("protocolVersion", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requestId", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FinalPublishIsPureWpfAndContainsNoWebAssets()
    {
        var releaseRoot = Path.Combine(Root, "artifacts", "release");
        if (!Directory.Exists(releaseRoot)) return;

        var suspicious = Directory.GetDirectories(releaseRoot, "publish", SearchOption.AllDirectories)
            .SelectMany(publish => Directory.GetFiles(publish, "*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(path).Contains("WebView2", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(path).Contains("React", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(suspicious);
    }

    [Fact]
    public void ReleaseManifestDoesNotDeclareWebRuntimeArtifacts()
    {
        var releaseRoot = Path.Combine(Root, "artifacts", "release");
        if (!Directory.Exists(releaseRoot)) return;

        foreach (var manifestPath in Directory.GetFiles(releaseRoot, "release-manifest.json", SearchOption.AllDirectories))
        {
            var manifest = File.ReadAllText(manifestPath);
            Assert.DoesNotContain("WebView2", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MicrosoftEdgeWebView2", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bootstrapperUrl", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("standaloneX64Url", manifest, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DesktopViewsAndViewModelsDoNotDependOnWindowsShortcutService()
    {
        var desktopRoot = Path.Combine(Root, "desktop/QuickPhrase.Desktop");
        var governedDirectories = new[]
        {
            Path.Combine(desktopRoot, "Views"),
            Path.Combine(desktopRoot, "ViewModels"),
        };

        var offendingFiles = governedDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("WindowsShortcutService", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(Root, path))
            .ToArray();

        Assert.True(
            offendingFiles.Length == 0,
            "Desktop View/ViewModel 只能依赖 IShortcutService，禁止引用 WindowsShortcutService：" +
            string.Join(", ", offendingFiles));
    }

    [Fact]
    public void DesktopViewsDependOnTheInProcessCommandContract()
    {
        var mainWindow = File.ReadAllText(Path.Combine(Root, "desktop/QuickPhrase.Desktop/MainWindow.xaml.cs"));
        var libraryView = File.ReadAllText(Path.Combine(Root, "desktop/QuickPhrase.Desktop/Views/LibraryView.xaml.cs"));

        Assert.Contains("ICommandService", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ICommandService", libraryView, StringComparison.Ordinal);
        Assert.DoesNotContain("ManagementBridge", mainWindow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ManagementBridge", libraryView, StringComparison.OrdinalIgnoreCase);
    }

    private static XElement Load(string relative) => XDocument.Load(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar))).Root!;
    private static string[] ProjectReferences(XElement root) => root.Descendants("ProjectReference").Select(x => x.Attribute("Include")!.Value).ToArray();
    private static string[] PackageReferences(XElement root) => root.Descendants("PackageReference").Select(x => x.Attribute("Include")!.Value).ToArray();
}

