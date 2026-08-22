using System.IO;
using System.Reflection;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

/// <summary>图片预览失败必须被转换为固定几何内的图标和中文错误，异步异常不能逃逸到 Dispatcher。</summary>
public sealed class ImagePreviewFailureTests
{
    [Fact]
    public void EditorPreview_ShowsChineseImageErrorStates()
    {
        var editor = ReadDesktopFile("DesignSystem", "Components", "PhraseRichTextEditor.xaml.cs");

        Assert.Contains("AutomationProperties.SetName(status, \"图片加载错误\")", editor, StringComparison.Ordinal);
        Assert.Contains("nameof(PhraseSegmentItemViewModel.LoadError)", editor, StringComparison.Ordinal);
        Assert.Contains("nameof(PhraseSegmentItemViewModel.HasLoadError)", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorPreview_ConvertsAsyncReadExceptionToChineseFieldState()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var image = new PhraseImageReference(Guid.NewGuid(), "image/png", 100, 10, 10);
            var phrase = new Phrase(
                Guid.NewGuid(), "图片话术", new PhraseBody([PhraseSegment.CreateImage(image)]),
                Guid.NewGuid(), ShortcutMode.None, null, 0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var fake = new FakeCommandService { ReadMediaException = new IOException("不应逃逸") };
            var vm = new EditorViewModel(fake, new PhraseItemViewModel(phrase, "分类"));
            var method = typeof(EditorViewModel).GetMethod("LoadSegmentPreviewsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var task = Assert.IsAssignableFrom<Task>(method.Invoke(vm, null));
            task.GetAwaiter().GetResult();

            var item = Assert.Single(vm.Segments);
            var hasError = Assert.IsType<bool>(item.GetType().GetProperty("HasLoadError")!.GetValue(item));
            Assert.True(hasError);
            Assert.Contains("图片加载失败", item.LoadError, StringComparison.Ordinal);
        });
    }

    private static string ReadDesktopFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
        return File.ReadAllText(Path.Combine(new[] { root, "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray()));
    }
}
