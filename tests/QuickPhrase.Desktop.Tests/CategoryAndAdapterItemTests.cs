using QuickPhrase.Desktop.ViewModels;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

public class CategoryItemTests
{
    [Fact]
    public void CategoryItem_CarriesFields()
    {
        var item = new CategoryItem(Guid.NewGuid(), "工作", null);
        Assert.Equal("工作", item.Name);
        Assert.Null(item.ParentId);
    }

}
