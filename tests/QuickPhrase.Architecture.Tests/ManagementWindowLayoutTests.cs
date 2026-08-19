using QuickPhrase.Desktop;

namespace QuickPhrase.Architecture.Tests;

public sealed class ManagementWindowLayoutTests
{
    [Theory]
    [InlineData("library", 1200, 760)]
    [InlineData("editor", 1200, 760)]
    [InlineData("settings", 1200, 760)]
    public void KnownScenesUseTheUnifiedManagementWindowSize(string scene, double width, double height)
    {
        Assert.True(ManagementWindowLayout.TryGet(scene, out var layout));
        Assert.Equal(width, layout.Width);
        Assert.Equal(height, layout.Height);
    }

    [Fact]
    public void UnknownSceneDoesNotProduceAWindowLayout()
    {
        Assert.False(ManagementWindowLayout.TryGet("launcher", out _));
    }

    [Fact]
    public void RepeatedSceneUsesTheSameLayoutWithoutAResizeTargetChange()
    {
        Assert.True(ManagementWindowLayout.TryGet("library", out var library));
        Assert.True(ManagementWindowLayout.TryGet("editor", out var editor));
        Assert.Equal(library, editor);
    }
}
