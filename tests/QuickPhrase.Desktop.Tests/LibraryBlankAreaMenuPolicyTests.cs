using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

public sealed class LibraryBlankAreaMenuPolicyTests
{
    private static CategoryItem Top(string name = "工作") =>
        new(Guid.NewGuid(), name, null, SortOrder: 0);

    private static CategoryItem Sub(Guid parentId, string name = "跟进") =>
        new(Guid.NewGuid(), name, parentId, SortOrder: 0);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldOpenMenu_OnlyForBlankHit(bool nodeHit)
    {
        Assert.Equal(!nodeHit, LibraryBlankAreaMenuPolicy.ShouldOpenMenu(nodeHit));
    }

    [Fact]
    public void EmptyContext_ReturnsRequiredPrompts()
    {
        var context = LibraryBlankAreaMenuPolicy.CreateContext(null, null);

        Assert.Equal("先新增一级分类，再新增话术", LibraryBlankAreaMenuPolicy.GetNewPhraseUnavailableMessage(context));
        Assert.Equal("先新增一级分类，再新建二级分类", LibraryBlankAreaMenuPolicy.GetNewSubCategoryUnavailableMessage(context));
        Assert.Null(LibraryBlankAreaMenuPolicy.ResolveNewPhraseTarget(context));
        Assert.Null(LibraryBlankAreaMenuPolicy.ResolveNewSubCategoryParent(context));
    }

    [Fact]
    public void TopCategoryContext_UsesTopCategoryForBothActions()
    {
        var top = Top();
        var context = LibraryBlankAreaMenuPolicy.CreateContext(top, top);

        Assert.Null(LibraryBlankAreaMenuPolicy.GetNewPhraseUnavailableMessage(context));
        Assert.Null(LibraryBlankAreaMenuPolicy.GetNewSubCategoryUnavailableMessage(context));
        Assert.Same(top, LibraryBlankAreaMenuPolicy.ResolveNewPhraseTarget(context));
        Assert.Same(top, LibraryBlankAreaMenuPolicy.ResolveNewSubCategoryParent(context));
    }

    [Fact]
    public void SubCategoryContext_CreatesPhraseInSubCategory_AndSiblingUnderParent()
    {
        var top = Top();
        var sub = Sub(top.Id);
        var context = LibraryBlankAreaMenuPolicy.CreateContext(sub, top);

        Assert.Null(LibraryBlankAreaMenuPolicy.GetNewPhraseUnavailableMessage(context));
        Assert.Null(LibraryBlankAreaMenuPolicy.GetNewSubCategoryUnavailableMessage(context));
        Assert.Same(sub, LibraryBlankAreaMenuPolicy.ResolveNewPhraseTarget(context));
        Assert.Same(top, LibraryBlankAreaMenuPolicy.ResolveNewSubCategoryParent(context));
        Assert.NotSame(sub, LibraryBlankAreaMenuPolicy.ResolveNewSubCategoryParent(context));
    }
}
