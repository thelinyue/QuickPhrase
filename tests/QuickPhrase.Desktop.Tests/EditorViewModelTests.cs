using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using QuickPhrase.Core;
using QuickPhrase.Desktop.DesignSystem.Components;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

public class EditorViewModelTests
{
    private static Phrase MakePhrase(Guid id, string title, string content, Guid categoryId, string colorKey = "default", ShortcutMode shortcutMode = ShortcutMode.None, string? shortcut = null)
        => new(id, title, PhraseBody.FromText(content), categoryId, shortcutMode,
            shortcut is null ? null : new ShortcutValue(shortcut, shortcut),
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, colorKey);

    private static Category MakeCategory(Guid id, string name, Guid? parentId = null, int sortOrder = 0)
        => new(id, parentId, name, sortOrder, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static void ApplySegments(EditorViewModel viewModel, params PhraseSegment[] segments)
    {
        var immutable = segments.ToImmutableArray();
        viewModel.ApplyDocumentDraft(new PhraseRichDocumentDraft(
            immutable,
            immutable.Where(segment => segment.Kind == PhraseSegmentKind.Text).Sum(segment => segment.Text?.Length ?? 0),
            immutable.Count(segment => segment.Kind == PhraseSegmentKind.Image),
            null,
            null));
    }

    [Fact]
    public async Task InvalidDocument_IsUnsavedAndCannotPersistLastValidProjection()
    {
        var fake = new FakeCommandService();
        var vm = new EditorViewModel(fake, null) { Title = "非法正文", SelectedCategoryId = Guid.NewGuid() };
        vm.ApplyDocumentDraft(PhraseRichDocumentDraft.Failure("EMPTY_TEXT_SEGMENT", "分隔符会产生空文字段。"));

        Assert.True(vm.HasUnsavedChanges);
        await vm.SaveAsync();

        Assert.Null(fake.LastCreatedPhraseCommand);
        Assert.Contains("空文字段", vm.VisibleErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void NewEditor_HasNoUnsavedChanges()
    {
        var vm = new EditorViewModel(new FakeCommandService(), null);
        Assert.True(vm.IsNew);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task LoadCategories_SelectsFirstPrimary_AndKeepsSecondaryOptional()
    {
        var primaryId = Guid.NewGuid();
        var secondaryId = Guid.NewGuid();
        var fake = new FakeCommandService();
        fake.Seed(new[]
        {
            MakeCategory(primaryId, "常用"),
            MakeCategory(secondaryId, "跟进", primaryId),
        });

        var vm = new EditorViewModel(fake, null);
        await vm.LoadCategoriesAsync();

        Assert.Single(vm.PrimaryCategories);
        Assert.Equal(primaryId, vm.SelectedPrimaryCategory!.Id);
        Assert.True(vm.HasSecondaryCategories);
        Assert.Equal(2, vm.SecondaryCategoryOptions.Count);
        Assert.Null(vm.SelectedSecondaryCategory!.CategoryId);
        Assert.Equal(primaryId, vm.SelectedCategoryId);
    }

    [Fact]
    public async Task LoadCategories_DefaultSecondary_SelectsItsParentAndSecondary()
    {
        var primaryId = Guid.NewGuid();
        var secondaryId = Guid.NewGuid();
        var fake = new FakeCommandService();
        fake.Seed(new[]
        {
            MakeCategory(primaryId, "常用"),
            MakeCategory(secondaryId, "跟进", primaryId),
        });

        var vm = new EditorViewModel(fake, null, secondaryId);
        await vm.LoadCategoriesAsync();

        Assert.Equal(primaryId, vm.SelectedPrimaryCategory!.Id);
        Assert.Equal(secondaryId, vm.SelectedSecondaryCategory!.CategoryId);
        Assert.Equal(secondaryId, vm.SelectedCategoryId);
    }

    [Fact]
    public async Task ChangingPrimary_FiltersSecondaryOptions_AndClearsOldSelection()
    {
        var firstPrimaryId = Guid.NewGuid();
        var firstSecondaryId = Guid.NewGuid();
        var secondPrimaryId = Guid.NewGuid();
        var secondSecondaryId = Guid.NewGuid();
        var fake = new FakeCommandService();
        fake.Seed(new[]
        {
            MakeCategory(firstPrimaryId, "常用", sortOrder: 0),
            MakeCategory(firstSecondaryId, "跟进", firstPrimaryId),
            MakeCategory(secondPrimaryId, "售后", sortOrder: 10),
            MakeCategory(secondSecondaryId, "退款", secondPrimaryId),
        });

        var vm = new EditorViewModel(fake, null);
        await vm.LoadCategoriesAsync();
        vm.SelectedSecondaryCategory = vm.SecondaryCategoryOptions.Single(option => option.CategoryId == firstSecondaryId);

        vm.SelectedPrimaryCategory = vm.PrimaryCategories.Single(category => category.Id == secondPrimaryId);

        Assert.Equal(new Guid?[] { null, secondSecondaryId }, vm.SecondaryCategoryOptions.Select(option => option.CategoryId).ToArray());
        Assert.Null(vm.SelectedSecondaryCategory!.CategoryId);
        Assert.Equal(secondPrimaryId, vm.SelectedCategoryId);
    }

    [Fact]
    public async Task SelectingSecondary_UsesSecondaryIdWhenSaving()
    {
        var primaryId = Guid.NewGuid();
        var secondaryId = Guid.NewGuid();
        var fake = new FakeCommandService();
        fake.Seed(new[]
        {
            MakeCategory(primaryId, "常用"),
            MakeCategory(secondaryId, "跟进", primaryId),
        });
        var vm = new EditorViewModel(fake, null);
        await vm.LoadCategoriesAsync();
        vm.SelectedSecondaryCategory = vm.SecondaryCategoryOptions.Single(option => option.CategoryId == secondaryId);
        vm.Title = "新话术";
        ApplySegments(vm, PhraseSegment.CreateText("内容"));

        await vm.SaveAsync();

        Assert.Equal(secondaryId, fake.LastCreatedPhraseCommand!.CategoryId);
    }

    [Fact]
    public async Task SettingSelectedCategoryId_SynchronizesPrimaryAndSecondarySelectors()
    {
        var primaryId = Guid.NewGuid();
        var secondaryId = Guid.NewGuid();
        var fake = new FakeCommandService();
        fake.Seed(new[]
        {
            MakeCategory(primaryId, "常用"),
            MakeCategory(secondaryId, "跟进", primaryId),
        });
        var vm = new EditorViewModel(fake, null);
        await vm.LoadCategoriesAsync();

        vm.SelectedCategoryId = secondaryId;

        Assert.Equal(primaryId, vm.SelectedPrimaryCategory!.Id);
        Assert.Equal(secondaryId, vm.SelectedSecondaryCategory!.CategoryId);
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
    public async Task TrimmedSeparatorPreviewAndSavedBodyUseTheSameSegmentsAndCanonicalValue()
    {
        var categoryId = Guid.NewGuid();
        var fake = new FakeCommandService();
        var vm = new EditorViewModel(fake, null);
        vm.SelectedCategoryId = categoryId;
        vm.Title = "拆分话术";
        var preview = PhraseBodyParser.SplitText("第一段\n  ---  \n第二段");
        Assert.True(preview.IsSuccess);

        ApplySegments(vm, preview.Segments.Select(PhraseSegment.CreateText).ToArray());

        await vm.SaveAsync();

        var saved = Assert.IsType<CreatePhraseCommand>(fake.LastCreatedPhraseCommand);
        Assert.Equal(preview.Segments, saved.Body.Segments.Select(segment => segment.Text!).ToArray());
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
        ApplySegments(vm, PhraseSegment.CreateText("内容"));
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
    public async Task Save_New_AllowsEmptyTitleAndPersistsItAsEmpty()
    {
        var fake = new FakeCommandService();
        var vm = new EditorViewModel(fake, null)
        {
            Title = "   ",
            SelectedCategoryId = Guid.NewGuid(),
        };
        ApplySegments(vm, PhraseSegment.CreateText("内容"));

        await vm.SaveAsync();

        Assert.Equal(string.Empty, fake.LastCreatedPhraseCommand!.Title);
    }

    [Fact]
    public async Task Save_Existing_AllowsEmptyTitleAndPersistsItAsEmpty()
    {
        var categoryId = Guid.NewGuid();
        var existing = MakePhrase(Guid.NewGuid(), "原标题", "内容", categoryId);
        var fake = new FakeCommandService();
        var vm = new EditorViewModel(fake, new PhraseItemViewModel(existing, "分类"))
        {
            Title = "  ",
        };

        await vm.SaveAsync();

        Assert.Equal(string.Empty, fake.LastUpdatedPhraseCommand!.Title);
    }

    [Fact]
    public async Task Save_Existing_AlwaysClearsPhraseShortcut()
    {
        var cat = Guid.NewGuid();
        var phrase = MakePhrase(Guid.NewGuid(), "标题", "内容", cat, "pink", ShortcutMode.Custom, "Ctrl + 1");
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
    public async Task DiscardChanges_ReleasesImagesImportedByCurrentEditSession()
    {
        var image = new PhraseImageReference(Guid.NewGuid(), "image/png", 68, 1, 1);
        var fake = new FakeCommandService { NextMediaImportResult = MediaImportResult.Success(image) };
        var vm = new EditorViewModel(fake, null);
        var item = await vm.ImportImageItemAsync("不会进入日志的测试路径.png");
        Assert.NotNull(item);
        ApplySegments(vm, item!.ToModel());

        vm.DiscardChanges();

        Assert.Contains(image.AssetId, fake.ReleasedMediaAssetIds);
        Assert.DoesNotContain(vm.Segments, segment => segment.Image?.AssetId == image.AssetId);
    }

    [Fact]
    public async Task RemovingUnsavedImage_KeepsAssetUntilSaveOrCancelSoUndoCanRestoreIt()
    {
        var image = new PhraseImageReference(Guid.NewGuid(), "image/png", 68, 1, 1);
        var fake = new FakeCommandService { NextMediaImportResult = MediaImportResult.Success(image) };
        var vm = new EditorViewModel(fake, null);
        var item = await vm.ImportImageItemAsync("测试图片.png");
        Assert.NotNull(item);
        ApplySegments(vm, item!.ToModel());

        ApplySegments(vm);

        Assert.DoesNotContain(image.AssetId, fake.ReleasedMediaAssetIds);
        vm.DiscardChanges();
        Assert.Contains(image.AssetId, fake.ReleasedMediaAssetIds);
    }

    [Fact]
    public async Task FailedSaveThenDiscard_ReleasesNewImageButNotExistingMedia()
    {
        var existingImage = new PhraseImageReference(Guid.NewGuid(), "image/png", 68, 1, 1);
        var newImage = new PhraseImageReference(Guid.NewGuid(), "image/png", 68, 1, 1);
        var categoryId = Guid.NewGuid();
        var existing = new Phrase(Guid.NewGuid(), "原话术", new PhraseBody([PhraseSegment.CreateImage(existingImage)]),
            categoryId, ShortcutMode.None, null, 0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var fake = new FakeCommandService
        {
            NextMediaImportResult = MediaImportResult.Success(newImage),
            NextPhraseSaveError = new DataError("DATABASE_BUSY", "保存失败。"),
        };
        fake.Seed([existing]);
        var vm = new EditorViewModel(fake, new PhraseItemViewModel(existing, "分类"));
        var imported = await vm.ImportImageItemAsync("新图片.png");
        Assert.NotNull(imported);
        ApplySegments(vm, PhraseSegment.CreateImage(existingImage), imported!.ToModel());

        await vm.SaveAsync();
        Assert.Empty(fake.ReleasedMediaAssetIds);

        vm.DiscardChanges();

        Assert.Contains(newImage.AssetId, fake.ReleasedMediaAssetIds);
        Assert.DoesNotContain(existingImage.AssetId, fake.ReleasedMediaAssetIds);
    }

    [Fact]
    public async Task SuccessfulSaveKeepsReferencedSessionImageAndReleasesRemovedSessionImage()
    {
        var kept = new PhraseImageReference(Guid.NewGuid(), "image/png", 68, 1, 1);
        var removed = new PhraseImageReference(Guid.NewGuid(), "image/png", 68, 1, 1);
        var fake = new FakeCommandService();
        var vm = new EditorViewModel(fake, null) { Title = "图文话术", SelectedCategoryId = Guid.NewGuid() };
        fake.NextMediaImportResult = MediaImportResult.Success(kept);
        var keptItem = await vm.ImportImageItemAsync("保留.png");
        fake.NextMediaImportResult = MediaImportResult.Success(removed);
        var removedItem = await vm.ImportImageItemAsync("删除.png");
        Assert.NotNull(keptItem);
        Assert.NotNull(removedItem);
        ApplySegments(vm, keptItem!.ToModel());

        await vm.SaveAsync();

        Assert.DoesNotContain(kept.AssetId, fake.ReleasedMediaAssetIds);
        Assert.Contains(removed.AssetId, fake.ReleasedMediaAssetIds);
        Assert.Contains(fake.LastCreatedPhraseCommand!.Body.Segments, segment => segment.Image?.AssetId == kept.AssetId);
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
