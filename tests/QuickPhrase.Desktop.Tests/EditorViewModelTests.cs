using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

public class EditorViewModelTests
{
    private static Phrase MakePhrase(Guid id, string title, string content, Guid categoryId, string colorKey = "default", ShortcutMode shortcutMode = ShortcutMode.None, string? shortcut = null)
        => new(id, title, content, categoryId, false, shortcutMode,
            shortcut is null ? null : new ShortcutValue(shortcut, shortcut),
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, colorKey);

    [Fact]
    public void NewEditor_HasNoUnsavedChanges()
    {
        var vm = new EditorViewModel(new FakeCommandService(), null);
        Assert.True(vm.IsNew);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void EditExisting_LoadsBaseline_AndDiscardRestores()
    {
        var id = Guid.NewGuid();
        var cat = Guid.NewGuid();
        var phrase = MakePhrase(id, "原标题", "原内容", cat, "blue");
        var item = new PhraseItemViewModel(phrase, "分类");
        var vm = new EditorViewModel(new FakeCommandService(), item);

        Assert.Equal("原标题", vm.Title);
        Assert.Equal("blue", vm.ColorKey);
        vm.Title = "改了";
        Assert.True(vm.HasUnsavedChanges);
        vm.DiscardChanges();
        Assert.Equal("原标题", vm.Title);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Save_New_CreatesViaService_AndClearsUnsaved()
    {
        var cat = Guid.NewGuid();
        var fake = new FakeCommandService();
        var vm = new EditorViewModel(fake, null);
        await vm.LoadCategoriesAsync();
        vm.SelectedCategoryId = cat;
        vm.Title = "新话术";
        vm.Content = "内容";
        vm.ColorKey = "orange";

        Phrase? saved = null;
        vm.Saved += (_, p) => saved = p;
        await vm.SaveAsync();

        Assert.NotNull(saved);
        Assert.Equal("新话术", saved!.Title);
        Assert.Equal("orange", saved.ColorKey);
        Assert.Equal(ShortcutMode.None, fake.LastCreatedPhraseCommand!.ShortcutMode);
        Assert.Null(fake.LastCreatedPhraseCommand.Shortcut);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Save_Existing_ClearsLegacyPhraseShortcut()
    {
        var cat = Guid.NewGuid();
        var phrase = MakePhrase(Guid.NewGuid(), "标题", "内容", cat, "red", ShortcutMode.Custom, "Ctrl + 1");
        var fake = new FakeCommandService();
        var vm = new EditorViewModel(fake, new PhraseItemViewModel(phrase, "分类"));
        vm.Title = "修改后";
        await vm.SaveAsync();

        Assert.Equal(ShortcutMode.None, fake.LastUpdatedPhraseCommand!.ShortcutMode);
        Assert.Null(fake.LastUpdatedPhraseCommand.Shortcut);
    }

    [Fact]
    public async Task Save_Failure_SetsErrorMessage()
    {
        var cat = Guid.NewGuid();
        var existing = MakePhrase(Guid.NewGuid(), "标题", "内容", cat);
        var vm = new EditorViewModel(new FakeCommandService(), new PhraseItemViewModel(existing, "分类"));
        vm.Title = "修改后";
        await vm.SaveAsync();
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public void ColorPalette_HasExpectedFixedOptions()
    {
        Assert.Equal(10, EditorViewModel.ColorKeys.Count);
        Assert.Equal(
            new[] { "default", "orange", "blue", "magenta", "purple", "green", "pink", "teal", "tan", "gray" },
            EditorViewModel.ColorKeys.Select(c => c.Key).ToArray());
        Assert.Equal(
            new[] { "#FFFFFF", "#FF8839", "#178BFF", "#FF73FF", "#AF60FF", "#41C028", "#F67E91", "#00A8A8", "#CB9563", "#5C6772" },
            EditorViewModel.ColorKeys.Select(c => c.Hex).ToArray());
    }
}


