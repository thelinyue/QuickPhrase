using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using QuickPhrase.Core;
using QuickPhrase.Desktop;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.Onboarding;
using QuickPhrase.Desktop.ViewModels;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 一次性校验所有窗口/视图的 XAML 能否正确解析与渲染。
/// 这是“双击无法打开”类问题（资源未定义、Color 当 Brush、非法枚举值、同文件前向引用等）
/// 的回归防护：任何模板错误都会在 Measure/ApplyTemplate 时抛出。
/// </summary>
public class XamlParseValidationTests
{
    [Fact]
    public void EditorAndLibraryViewsDoNotExposePhraseShortcutControls()
    {
        var root = FindRepoRoot();
        var editor = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "EditorView.xaml"));
        var library = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "LibraryView.xaml"));

        Assert.DoesNotContain("快捷键模式", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"快捷键\"", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("自定义快捷键", library, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource=\"{Binding ColorKeys}\"", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindow_LoadedAnimationDoesNotApplyTransformToWindow()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "SettingsWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "SettingsWindow.xaml.cs"));

        Assert.Contains("x:Name=\"WindowRoot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WindowRoot.RenderTransform = transform;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("        RenderTransform = transform;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AllWindowsAndViewsRenderWithoutXamlErrors()
    {
        var errors = new List<string>();
        Exception? setupError = null;

        var t = new Thread(() =>
        {
            try
            {
                var app = new Application();
                // 与真实 App.xaml 完全一致地加载主题（从程序集内嵌 BAML，走 pack URI）。
                // 这样 clr-namespace 与依赖属性的解析上下文与正式运行完全一致，
                // 避免松散文件加载对转换器/属性解析产生的误报。
                foreach (var rel in new[] { "Themes/QuickPhraseTheme.xaml", "Themes/QuickPhraseTheme.Dark.xaml", "Themes/Converters.xaml", "Themes/Controls.xaml", "Themes/PhraseListResources.xaml" })
                {
                    // 相对 pack URI 跨程序集引用 QuickPhrase 内嵌的 BAML（Application.LoadComponent
                    // 不接受绝对 pack URI）。这与正式运行 App.xaml 的加载上下文一致。
                    var uri = new Uri($"/QuickPhrase;component/{rel}", UriKind.Relative);
                    app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(uri));
                }
                var fake = new FakeCommandService();
                var phrase = new Phrase(
                    Guid.NewGuid(), "示例标题", "示例正文", Guid.NewGuid(),
                    false, ShortcutMode.None, null,
                    0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "default");
                var pvm = new PhraseItemViewModel(phrase, "示例分类");

                var history = new SearchHistoryCoordinator(new EmptySearchHistoryRepository());
                var package = new PhrasePackageDocument(
                    new PhrasePackageManifest(
                        PhrasePackageFormat.Format,
                        PhrasePackageFormat.Version,
                        Guid.NewGuid(),
                        "示例话术包",
                        DateTimeOffset.UtcNow,
                        0,
                        0),
                    Array.Empty<PhrasePackageCategory>(),
                    Array.Empty<PhrasePackagePhrase>());
                var packageSnapshot = new PhrasePackageLocalSnapshot(
                    Array.Empty<Category>(),
                    Array.Empty<Phrase>());
                var importVm = new ImportPhrasePackageViewModel(fake, package, packageSnapshot);
                var exportVm = new ExportPhrasePackageViewModel(packageSnapshot);
                TryRender("OnboardingWindow", () => new OnboardingWindow(new OnboardingViewModel(fake, new AppSettings(1, false, false, true, "Alt + Space", "alt+space", false, true))), errors);
                TryRender("NavigationConfirmDialog", () => new NavigationConfirmDialog(), errors);
                TryRender("HotkeyCaptureDialog", () => new HotkeyCaptureDialog("Alt + Space"), errors);
                TryRender("CategoryDialog", () => new CategoryDialog(fake), errors);
                TryRender("ImportPhrasePackageDialog", () => new ImportPhrasePackageDialog(importVm), errors);
                TryRender("ExportPhrasePackageDialog", () => new ExportPhrasePackageDialog(exportVm), errors);
                TryRender("PhraseMoveDialog", () => new PhraseMoveDialog(fake, pvm), errors);
                TryRender("LibraryView", () => new LibraryView(fake, history), errors);
                TryRender("SettingsView", () => new SettingsView(fake), errors);
                TryRender("SettingsWindow", () => new SettingsWindow(fake), errors);
                TryRender("EditorView", () => new EditorView(fake, pvm), errors);
                TryRender("LauncherWindow", () => new LauncherWindow(null!, history), errors);
                TryRender("MainWindow", () => new MainWindow(fake, history, "library"), errors);

                app.Shutdown();
            }
            catch (Exception ex)
            {
                setupError = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        if (setupError is not null)
            throw new Exception("XAML 校验设置阶段异常:\n" + setupError);

        if (errors.Count > 0)
            throw new Exception("以下窗口/视图存在 XAML 渲染错误:\n" + string.Join("\n----\n", errors));
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QuickPhrase.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }

    private sealed class EmptySearchHistoryRepository : ISearchHistoryRepository
    {
        public Task<IReadOnlyList<SearchHistoryEntry>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SearchHistoryEntry>>([]);

        public Task<RepositoryResult<SearchHistoryEntry>> RecordAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult(RepositoryResult<SearchHistoryEntry>.Success(new SearchHistoryEntry(query.Trim(), DateTimeOffset.Now)));

        public Task<RepositoryResult<bool>> ClearAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(RepositoryResult<bool>.Success(true));
    }

    private static void TryRender(string name, Func<FrameworkElement> factory, List<string> errors)
    {
        try
        {
            var el = factory();
            if (el is Window w)
            {
                w.ApplyTemplate();
                w.Measure(new Size(1200, 800));
            }
            else
            {
                var grid = new Grid();
                grid.Children.Add(el);
                grid.Measure(new Size(1200, 800));
                grid.Arrange(new Rect(0, 0, 1200, 800));
            }
        }
        catch (Exception ex)
        {
            errors.Add($"[{name}] {ex.GetType().Name}:\n{ex}");
        }
    }
}

