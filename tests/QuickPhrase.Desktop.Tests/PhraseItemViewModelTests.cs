using System.Collections.Immutable;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop.Tests;

public class PhraseItemViewModelTests
{
    private static Phrase MakePhrase(string title, string content)
        => new(Guid.NewGuid(), title, content, Guid.NewGuid(), ShortcutMode.None, null,
            0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "green");

    [Fact]
    public void Snippet_TruncatesLongContent_AndCollapsesNewlines()
    {
        var phrase = MakePhrase("标题", "第一行\n第二行  " + new string('x', 200));
        var vm = new PhraseItemViewModel(phrase, "分类");
        Assert.DoesNotContain("\n", vm.Snippet);
        Assert.True(vm.Snippet.Length <= 91);
        Assert.EndsWith("…", vm.Snippet);
    }

    [Fact]
    public void Apply_UpdatesObservableProperties()
    {
        var phrase = MakePhrase("旧", "旧内容");
        var vm = new PhraseItemViewModel(phrase, "分类");
        var updated = phrase with { Title = "新", Content = "新内容", ColorKey = "pink" };
        vm.Apply(updated, "新分类");
        Assert.Equal("新", vm.Title);
        Assert.Equal("新内容", vm.Content);
        Assert.Equal("pink", vm.ColorKey);
        Assert.Equal("新分类", vm.CategoryName);
    }

    [Fact]
    public void ToPhrase_RoundTripsModel()
    {
        var phrase = MakePhrase("标题", "内容");
        var vm = new PhraseItemViewModel(phrase, "分类");
        Assert.Equal(phrase, vm.ToPhrase());
        Assert.Equal(phrase.Id, vm.Id);
        Assert.Equal("green", vm.ColorKey);
    }
}
