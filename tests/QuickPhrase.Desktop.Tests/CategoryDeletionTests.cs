using System.Threading.Tasks;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

public class CategoryDeletionTests
{
    [Fact]
    public async Task CascadeCategoryDelete_ReloadsOnlyAfterDeleteSucceeds()
    {
        var root = new Category(Guid.NewGuid(), null, "通用话术", 7, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var child = new Category(Guid.NewGuid(), root.Id, "售前", 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var rootPhrase = new Phrase(Guid.NewGuid(), "欢迎语", "您好", root.Id, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var childPhrase = new Phrase(Guid.NewGuid(), "售前说明", "请问有什么可以帮您", child.Id, ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var fake = new FakeCommandService();
        fake.Seed(new[] { root, child });
        fake.Seed(new[] { rootPhrase, childPhrase });
        var reloadCount = 0;

        var result = await MainWindow.DeleteCategoryAndReloadAsync(
            fake,
            new CategoryItem(root.Id, root.Name, root.ParentId, root.SortOrder, 0, false, false, root.Version),
            () => { reloadCount++; return Task.CompletedTask; });

        Assert.True(result.IsSuccess);
        Assert.True(result.Value?.Deleted);
        Assert.Equal(1, fake.DeleteCategoryCalls);
        Assert.Equal(1, reloadCount);
        Assert.Empty(await fake.ListCategoriesAsync());
        Assert.Empty(await fake.ListPhrasesAsync());
    }

    [Fact]
    public async Task FirstConfirmationCancelled_DoesNotCallDelete()
    {
        var category = Category("需要确认");
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        var reloadCount = 0;

        var result = await MainWindow.ConfirmAndDeleteCategoryAsync(
            fake,
            new CategoryItem(category.Id, category.Name, category.ParentId, category.SortOrder, 0, false, false, category.Version),
            () => false,
            () => true,
            () => { reloadCount++; return Task.CompletedTask; });

        Assert.False(result.Value?.Deleted);
        Assert.Equal(0, fake.DeleteCategoryCalls);
        Assert.Equal(0, reloadCount);
        Assert.Single(await fake.ListCategoriesAsync());
    }

    [Fact]
    public async Task SecondConfirmationCancelled_DoesNotCallDelete()
    {
        var category = Category("需要二次确认");
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });
        var reloadCount = 0;

        var result = await MainWindow.ConfirmAndDeleteCategoryAsync(
            fake,
            new CategoryItem(category.Id, category.Name, category.ParentId, category.SortOrder, 0, false, false, category.Version),
            () => true,
            () => false,
            () => { reloadCount++; return Task.CompletedTask; });

        Assert.False(result.Value?.Deleted);
        Assert.Equal(0, fake.DeleteCategoryCalls);
        Assert.Equal(0, reloadCount);
        Assert.Single(await fake.ListCategoriesAsync());
    }

    [Fact]
    public async Task BothConfirmationsAccepted_CallsDeleteOnce()
    {
        var category = Category("确认删除");
        var fake = new FakeCommandService();
        fake.Seed(new[] { category });

        var result = await MainWindow.ConfirmAndDeleteCategoryAsync(
            fake,
            new CategoryItem(category.Id, category.Name, category.ParentId, category.SortOrder, 0, false, false, category.Version),
            () => true,
            () => true,
            () => Task.CompletedTask);

        Assert.True(result.Value?.Deleted);
        Assert.Equal(1, fake.DeleteCategoryCalls);
        Assert.Empty(await fake.ListCategoriesAsync());
    }

    private static Category Category(string name) =>
        new(Guid.NewGuid(), null, name, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}