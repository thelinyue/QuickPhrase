using System.IO;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

public sealed class EnterpriseReadOnlyPhraseTests
{
    [Fact]
    public async Task EnterprisePhraseOpensReadOnlyDetailButCannotMoveOrDelete()
    {
        var categoryId = Guid.NewGuid();
        var phrase = new Phrase(Guid.NewGuid(), "企业话术", PhraseBody.FromText("企业正文"), categoryId, ShortcutMode.None, null, 0, null, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Scope: PhraseScope.Enterprise);
        var fake = new FakeCommandService();
        fake.Seed(new[] { new Category(categoryId, null, "企业分类", 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, PhraseScope.Enterprise) });
        fake.Seed(new[] { phrase });
        var vm = new PhraseLibraryViewModel(fake);
        var editRaised = false;
        var moveRaised = false;
        vm.EditRequested += (_, _) => editRaised = true;
        vm.MoveRequested += (_, _) => moveRaised = true;
        await vm.LoadAsync();
        var item = Assert.Single(vm.Phrases);

        Assert.True(item.IsEnterprise);
        Assert.False(item.CanManage);
        vm.EditCommand.Execute(item);
        vm.MoveCommand.Execute(item);
        await vm.DeleteCommand.ExecuteAsync(item);

        Assert.True(editRaised);
        Assert.False(moveRaised);
        Assert.Contains("管理员维护", vm.StatusMessage);
        Assert.NotNull(await fake.GetPhraseAsync(phrase.Id));
    }
    [Fact]
    public async Task EnterpriseEditorKeepsRichDocumentSelectableButRejectsMutation()
    {
        var categoryId = Guid.NewGuid();
        var phrase = new Phrase(Guid.NewGuid(), "企业话术", PhraseBody.FromText("企业正文"), categoryId, ShortcutMode.None, null, 0, null, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Scope: PhraseScope.Enterprise);
        var fake = new FakeCommandService();
        var viewModel = new EditorViewModel(fake, new PhraseItemViewModel(phrase, "企业分类"));

        Assert.True(viewModel.IsReadOnly);
        Assert.False(viewModel.CanDelete);
        Assert.Null(await viewModel.ImportImageItemAsync("不会读取的图片.png"));
        await viewModel.SaveAsync();
        Assert.Null(fake.LastUpdatedPhraseCommand);

        var root = FindRepositoryRoot();
        var markup = File.ReadAllText(Path.Combine(root, "desktop", "QuickPhrase.Desktop", "Views", "EditorView.xaml"));
        Assert.Contains("IsReadOnly=\"{Binding IsReadOnly}\"", markup, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsReadOnly, Converter={StaticResource BoolToVisibility}, ConverterParameter=invert}\"", markup, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase.sln");
    }
}
