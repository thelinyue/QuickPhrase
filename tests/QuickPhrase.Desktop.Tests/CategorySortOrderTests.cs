using System.Linq;
using System.Threading.Tasks;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

public sealed class CategorySortOrderTests
{
    [Fact]
    public async Task LoadAsync_ShowsAutomaticallyCreatedTopCategoriesInCreationOrder()
    {
        var commands = new FakeCommandService();
        var first = (await commands.CreateCategoryAsync(new CreateCategoryCommand(Guid.NewGuid(), "第一分类"))).Value!;
        var second = (await commands.CreateCategoryAsync(new CreateCategoryCommand(Guid.NewGuid(), "第二分类"))).Value!;
        var third = (await commands.CreateCategoryAsync(new CreateCategoryCommand(Guid.NewGuid(), "第三分类"))).Value!;

        var viewModel = new PhraseLibraryViewModel(commands);
        await viewModel.LoadAsync();

        Assert.Equal([0, 10, 20], viewModel.TopCategories.Select(category => category.SortOrder));
        Assert.Equal([first.Id, second.Id, third.Id], viewModel.TopCategories.Select(category => category.Id));
    }
}