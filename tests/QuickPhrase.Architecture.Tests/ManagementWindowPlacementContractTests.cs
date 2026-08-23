using QuickPhrase.Core;

namespace QuickPhrase.Architecture.Tests;

/// <summary>管理窗口形态必须保持平台无关，Core 只保存语义模式和 DIP 几何。</summary>
public sealed class ManagementWindowPlacementContractTests
{
    [Fact]
    public void DefaultPlacement_IsCompactFloatingWindow()
    {
        var placement = ManagementWindowPlacement.Default;

        Assert.Equal(ManagementWindowMode.Floating, placement.Mode);
        Assert.Null(placement.FloatingLeft);
        Assert.Null(placement.FloatingTop);
        Assert.Equal(520d, placement.FloatingWidth);
        Assert.Equal(680d, placement.FloatingHeight);
    }

    [Fact]
    public void CorePlacement_DoesNotExposeWindowsTypes()
    {
        var propertyTypes = typeof(ManagementWindowPlacement).GetProperties().Select(property => property.PropertyType).ToArray();

        Assert.DoesNotContain(propertyTypes, type => type.FullName?.StartsWith("System.Windows", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(propertyTypes, type => type == typeof(nint));
    }
}
