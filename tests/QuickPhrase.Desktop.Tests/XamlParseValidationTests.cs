using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuickPhrase.Core;
using QuickPhrase.Desktop;
using QuickPhrase.Desktop.DesignSystem.Components;
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
    public void NewPhraseWindow_ShowsWithoutDeferredTemplateResourceErrors()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var window = new NewPhraseWindow(new FakeCommandService());
            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.True(window.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

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
    public void EditorView_UsesUnifiedPhraseFieldLabels()
    {
        var root = FindRepoRoot();
        var editor = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "EditorView.xaml"));

        Assert.Contains("Text=\"话术标题（可选，最多 80 字）\"", editor, StringComparison.Ordinal);
        Assert.Contains("Text=\"话术内容（必填，文字最多 4000 字；最多 20 段、10 张图片）\"", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"标题（必填，最多 80 字）\"", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"正文（必填，文字最多 4000 字；最多 20 段、10 张图片）\"", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsView_UsesUnifiedCsvHeaders()
    {
        var root = FindRepoRoot();
        var settings = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "SettingsView.xaml"));

        Assert.Contains("列顺序固定为一级分类、二级分类、话术标题、话术内容；", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("列顺序固定为一级分类、二级分类、标题、正文；", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void OnboardingWindow_UsesUnifiedWizardContract()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "OnboardingWindow.xaml"));

        Assert.DoesNotContain("已打开：False", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("已搜索：False", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("已插入：False", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"保存到\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"话术标题\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"话术内容\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource Style.Select.Default}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding StepNumber, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ContentRegion\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RowDefinition Height=\"{StaticResource Size.Onboarding.Footer.GridLength}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ScrollViewer", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\" Height=\"{StaticResource Size.Onboarding.PhraseBody.Height}\"", xaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "Content=\"修改快捷键\""));
        Assert.Contains("xmlns:designSystem=\"clr-namespace:QuickPhrase.Desktop.DesignSystem.Components\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<designSystem:SettingItem Title=\"开机启动\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Description=\"登录 Windows 后自动启动 QuickPhrase\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding LaunchOnStartup}\"", xaml, StringComparison.Ordinal);

        var completeSectionStart = xaml.IndexOf("x:Name=\"CompleteStepPanel\"", StringComparison.Ordinal);
        var footerStart = xaml.IndexOf("<!-- Footer", completeSectionStart, StringComparison.Ordinal);
        Assert.True(completeSectionStart >= 0 && footerStart > completeSectionStart);
        Assert.DoesNotContain("跳过", xaml[completeSectionStart..footerStart], StringComparison.Ordinal);
    }

    [Fact]
    public void OnboardingHotkey_UsesViewModelPracticeCommand()
    {
        var root = FindRepoRoot();
        var controller = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "ApplicationController.cs"));

        var onboardingBranchStart = controller.IndexOf("if (_onboarding?.ViewModel is { CurrentStep: OnboardingStep.Practice }", StringComparison.Ordinal);
        Assert.True(onboardingBranchStart >= 0);
        var nextProductionBranch = controller.IndexOf("OpenLauncher(target: _targetDetector.CaptureForeground(), captureTarget: false);", onboardingBranchStart, StringComparison.Ordinal);
        Assert.True(nextProductionBranch > onboardingBranchStart);

        var branch = controller[onboardingBranchStart..nextProductionBranch];
        Assert.Contains("BeginPracticeCommand.ExecuteAsync(null)", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("StartOnboardingPracticeAsync(onboardingViewModel)", branch, StringComparison.Ordinal);
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
    public void LightThemeProvidesFocusBrushToControlTemplates()
    {
        WpfTestApplicationHost.Invoke(app =>
        {
            var textBox = new TextBox
            {
                Style = (Style)app.Resources["Style.Input.Default"],
                Width = 200,
                Height = (double)app.Resources["Size.Control.Default"],
            };

            Assert.NotNull(app.Resources["Brush.Border.Focus"]);
            textBox.ApplyTemplate();
            textBox.Measure(new Size(textBox.Width, textBox.Height));
        });
    }

    [Fact]
    public void LibrarySubCategoryHeaderTemplate_ResolvesItsLocalHorizontalMarginResource()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var view = new LibraryView(
                new FakeCommandService(),
                new SearchHistoryCoordinator(new EmptySearchHistoryRepository()));
            var header = new ToggleButton
            {
                Content = "示例二级分类",
                Style = Assert.IsType<Style>(view.Resources["Style.Library.SubHeaderButton"]),
            };
            var host = new Grid();

            try
            {
                host.Children.Add(header);
                host.Measure(new Size(300, 40));
                host.Arrange(new Rect(0, 0, 300, 40));
                header.ApplyTemplate();

                Assert.NotNull(header.Template.FindName("Root", header));
            }
            finally
            {
                host.Children.Remove(header);
            }
        });
    }

    [Fact]
    public void HotkeyCaptureDialog_CaptureOnlyUpdatesCandidateWithoutApplying()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var current = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
            var candidate = new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space);
            var applyCount = 0;
            var dialog = new HotkeyCaptureDialog(
                current,
                (chord, _) =>
                {
                    applyCount++;
                    return Task.FromResult(RepositoryResult<AppSettings>.Success(CreateSettings(chord)));
                });
            var shortcutInput = (ShortcutInput)dialog.FindName("CapturedShortcut");

            Assert.Equal(current, dialog.CandidateChord);
            shortcutInput.IsCapturing = true;
            shortcutInput.ProcessKeyInput(Key.Space, ModifierKeys.Control, isRepeat: false);

            Assert.Equal(candidate, dialog.CandidateChord);
            Assert.Equal(0, applyCount);
            dialog.Close();
        });
    }

    [Fact]
    public void HotkeyCaptureDialog_ApplyFailureKeepsDialogOpenAndPreservesActiveShortcut()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var active = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
            var dialog = new HotkeyCaptureDialog(
                active,
                (_, _) => Task.FromResult(RepositoryResult<AppSettings>.Failure(
                    new DataError("HOTKEY_CONFLICT", "快捷键冲突，请选择其他组合。"))));
            var shortcutInput = (ShortcutInput)dialog.FindName("CapturedShortcut");
            var saveButton = (Button)dialog.FindName("SaveButton");

            dialog.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    shortcutInput.IsCapturing = true;
                    shortcutInput.ProcessKeyInput(Key.Space, ModifierKeys.Control, isRepeat: false);
                    saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                    Assert.True(dialog.IsVisible);
                    Assert.Equal("快捷键冲突，请选择其他组合。", dialog.CaptureErrorMessage);
                    Assert.Equal(new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space), dialog.CandidateChord);
                    Assert.Equal(new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space), active);
                }
                finally
                {
                    if (dialog.IsVisible)
                        dialog.Close();
                }
            }, DispatcherPriority.Loaded);

            Assert.False(dialog.ShowDialog());
        });
    }

    [Fact]
    public void HotkeyCaptureDialog_ApplySuccessClosesOnlyAfterPersistedResult()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var active = new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space);
            var applyCount = 0;
            var dialog = new HotkeyCaptureDialog(
                active,
                (chord, _) =>
                {
                    applyCount++;
                    active = chord;
                    return Task.FromResult(RepositoryResult<AppSettings>.Success(CreateSettings(chord)));
                });
            var shortcutInput = (ShortcutInput)dialog.FindName("CapturedShortcut");
            var saveButton = (Button)dialog.FindName("SaveButton");

            dialog.Dispatcher.BeginInvoke(() =>
            {
                shortcutInput.IsCapturing = true;
                shortcutInput.ProcessKeyInput(Key.Space, ModifierKeys.Control, isRepeat: false);
                saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.Loaded);

            Assert.True(dialog.ShowDialog());
            Assert.Equal(1, applyCount);
            Assert.Equal(new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space), active);
        });
    }

    [Fact]
    public void HotkeyCaptureDialog_CancelAndCaptureEscapeDoNotApplyCandidate()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var applyCount = 0;
            var dialog = new HotkeyCaptureDialog(
                new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space),
                (chord, _) =>
                {
                    applyCount++;
                    return Task.FromResult(RepositoryResult<AppSettings>.Success(CreateSettings(chord)));
                });
            var shortcutInput = (ShortcutInput)dialog.FindName("CapturedShortcut");

            dialog.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    shortcutInput.IsCapturing = true;
                    shortcutInput.ProcessKeyInput(Key.Escape, ModifierKeys.None, isRepeat: false);

                    Assert.False(shortcutInput.IsCapturing);
                    Assert.True(dialog.IsVisible);
                    Assert.Equal(0, applyCount);
                    var cancelButton = Assert.IsType<Button>(dialog.FindName("CancelButton"));
                    cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally
                {
                    if (dialog.IsVisible)
                        dialog.Close();
                }
            }, DispatcherPriority.Loaded);

            Assert.False(dialog.ShowDialog());
            Assert.Equal(0, applyCount);
        });
    }

    [Fact]
    public void HotkeyCaptureDialog_UsesSizeTokensAndDoesNotLogShortcutValues()
    {
        var root = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "Dialogs", "HotkeyCaptureDialog.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "Dialogs", "HotkeyCaptureDialog.xaml.cs"));

        Assert.Contains("Width=\"{StaticResource Size.Dialog.Shortcut.Width}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"{StaticResource Size.Dialog.Shortcut.Height}\"", xaml, StringComparison.Ordinal);

        var logStart = code.IndexOf("System.Diagnostics.Trace.TraceError(", StringComparison.Ordinal);
        var logEnd = code.IndexOf("CaptureErrorMessage =", logStart, StringComparison.Ordinal);
        Assert.True(logStart >= 0 && logEnd > logStart, "快捷键保存失败分支必须保留中文结构化日志。");
        var logBlock = code[logStart..logEnd];
        Assert.DoesNotContain("CandidateChord", logBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("ShortcutKey", logBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualKey", logBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void AllWindowsAndViewsRenderWithoutXamlErrors()
    {
        var errors = new List<string>();

        WpfTestApplicationHost.Invoke(_ =>
        {
            var fake = new FakeCommandService();
            var phrase = new Phrase(
                Guid.NewGuid(), "示例标题", PhraseBody.FromText("示例正文"), Guid.NewGuid(), ShortcutMode.None, null,
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
                    0,
                    0),
                Array.Empty<PhrasePackageCategory>(),
                Array.Empty<PhrasePackagePhrase>(),
                Array.Empty<PhrasePackageMedia>());
            var packageSnapshot = new PhrasePackageLocalSnapshot(
                Array.Empty<Category>(),
                Array.Empty<Phrase>());
            var importVm = new ImportPhrasePackageViewModel(fake, package, packageSnapshot);
            var exportVm = new ExportPhrasePackageViewModel(packageSnapshot);
            TryRender("OnboardingWindow", () => new OnboardingWindow(new OnboardingViewModel(fake, new AppSettings(1, false, false, true, new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space), false, true))), errors);
            TryRender("NavigationConfirmDialog", () => new NavigationConfirmDialog(), errors);
            TryRender(
                "HotkeyCaptureDialog",
                () => new HotkeyCaptureDialog(
                    new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space),
                    (chord, _) => Task.FromResult(RepositoryResult<AppSettings>.Success(
                        new AppSettings(1, false, false, true, chord, false, true)))),
                errors);
            TryRender("CategoryDialog", () => new CategoryDialog(fake), errors);
            TryRender("ImportPhrasePackageDialog", () => new ImportPhrasePackageDialog(importVm), errors);
            TryRender("ExportPhrasePackageDialog", () => new ExportPhrasePackageDialog(exportVm), errors);
            TryRender("QuickSendGuideDialog", () => new QuickSendGuideDialog(), errors);
            TryRender("PhraseMoveDialog", () => new PhraseMoveDialog(fake, pvm), errors);
            TryRender("LibraryView", () => new LibraryView(fake, history), errors);
            TryRender("SettingsView", () => new SettingsView(fake), errors);
            TryRender("SettingsWindow", () => new SettingsWindow(fake), errors);
            TryRender("EditorView", () => new EditorView(fake, pvm), errors);
            TryRender("NewPhraseWindow", () => new NewPhraseWindow(fake), errors);
            TryRender("LauncherWindow", () => new LauncherWindow(null!, history), errors);
            TryRender("MainWindow", () => new MainWindow(fake, history, "library"), errors);
        });

        if (errors.Count > 0)
            throw new Exception("以下窗口/视图存在 XAML 渲染错误:\n" + string.Join("\n----\n", errors));
    }

    private static AppSettings CreateSettings(ShortcutChord chord) =>
        new(1, false, false, true, chord, false, true);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
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
        FrameworkElement? element = null;
        Grid? host = null;
        try
        {
            element = factory();
            if (element is Window window)
            {
                window.ApplyTemplate();
                window.Measure(new Size(1200, 800));
            }
            else
            {
                host = new Grid();
                host.Children.Add(element);
                host.Measure(new Size(1200, 800));
                host.Arrange(new Rect(0, 0, 1200, 800));
            }
        }
        catch (Exception ex)
        {
            errors.Add($"[{name}] {ex.GetType().Name}:\n{ex}");
        }
        finally
        {
            // 渲染测试共用单一 Application；每个样本结束后主动断开视觉树和绑定，
            // 避免上一个窗口的延迟布局/绑定任务污染后续模态窗口测试。
            if (host is not null && element is not null)
                host.Children.Remove(element);

            if (element is Window window)
                window.Content = null;

            if (element is not null)
                element.DataContext = null;
        }
    }
}
