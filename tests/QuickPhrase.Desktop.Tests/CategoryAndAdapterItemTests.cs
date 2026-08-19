using QuickPhrase.Desktop.ViewModels;
using Xunit;

namespace QuickPhrase.Desktop.Tests;

public class CategoryAndAdapterItemTests
{
    [Fact]
    public void CategoryItem_CarriesFields()
    {
        var item = new CategoryItem(Guid.NewGuid(), "工作", null);
        Assert.Equal("工作", item.Name);
        Assert.Null(item.ParentId);
    }

    [Fact]
    public void AdapterToggleItem_MapsKnownDisplayName()
    {
        var wx = new AdapterToggleItem("WXWork", true);
        Assert.Equal("企业微信", wx.DisplayName);
        Assert.True(wx.Enabled);

        var other = new AdapterToggleItem("SomethingElse", false);
        Assert.Equal("SomethingElse", other.DisplayName);
        Assert.False(other.Enabled);
    }
}
