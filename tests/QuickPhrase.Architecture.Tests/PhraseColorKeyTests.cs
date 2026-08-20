using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

public sealed class PhraseColorKeyTests
{
    [Fact]
    public async Task ExistingAndNewPhrasesUseDefaultColorKey()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "颜色测试"))).Value!;
        Assert.Empty(await runtime.Phrases.ListAsync());

        var created = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(
            Guid.NewGuid(), "默认颜色", "正文", category.Id, ShortcutMode.None, null));

        Assert.Equal("default", created.Value!.ColorKey);
    }

    [Fact]
    public async Task ReopeningPreservesCurrentPhraseContentAndColor()
    {
        using var temp = new TemporaryDirectory();
        Guid phraseId;
        await using (var first = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path)))
        {
            var category = (await first.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "重启测试"))).Value!;
            phraseId = Guid.NewGuid();
            var created = await first.Phrases.CreateAsync(new CreatePhraseCommand(phraseId, "重启话术", "正文保持不变", category.Id, ShortcutMode.None, null));
            Assert.True(created.IsSuccess);
        }

        await using var reopened = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var phrase = await reopened.Phrases.GetAsync(phraseId);
        Assert.Equal("正文保持不变", phrase!.Content);
        Assert.Equal("default", phrase.ColorKey);
    }

    [Fact]
    public async Task FixedPaletteColorsRoundTripThroughRepository()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "颜色测试"))).Value!;
        var id = Guid.NewGuid();
        var command = new CreatePhraseCommand(id, "粉色话术", "正文", category.Id, ShortcutMode.None, null, "pink");

        var created = await runtime.Phrases.CreateAsync(command);
        Assert.True(created.IsSuccess);
        Assert.Equal("pink", (await runtime.Phrases.GetAsync(id))!.ColorKey);
    }

    [Fact]
    public async Task UnknownColorKeyIsRejectedWithReadableChineseError()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var category = (await runtime.Categories.CreateAsync(new CreateCategoryCommand(Guid.NewGuid(), "颜色测试"))).Value!;

        var result = await runtime.Phrases.CreateAsync(new CreatePhraseCommand(
            Guid.NewGuid(), "未知颜色", "正文", category.Id, ShortcutMode.None, null, "not-a-color"));

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
        Assert.Contains("颜色", result.Error.Message);
        Assert.Contains("not-a-color", result.Error.Message);
    }
}

file sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quickphrase-color-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

