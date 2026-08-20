using QuickPhrase.Core;
using QuickPhrase.Desktop.Tests.Fakes;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop.Tests;

public sealed class EnterpriseReadOnlyPhraseTests
{
    [Fact]
    public async Task EnterprisePhraseCanInsertButCannotEditMoveOrDelete()
    {
        var categoryId = Guid.NewGuid();
        var phrase = new Phrase(Guid.NewGuid(), "企业话术", "企业正文", categoryId, ShortcutMode.None, null, 0, null, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Scope: PhraseScope.Enterprise);
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
        await vm.InsertCommand.ExecuteAsync(item);
        vm.EditCommand.Execute(item);
        vm.MoveCommand.Execute(item);
        await vm.DeleteCommand.ExecuteAsync(item);

        Assert.False(editRaised);
        Assert.False(moveRaised);
        Assert.Contains("管理员维护", vm.StatusMessage);
        Assert.NotNull(await fake.GetPhraseAsync(phrase.Id));
    }
}
