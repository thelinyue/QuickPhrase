using QuickPhrase.Core;

namespace QuickPhrase.Architecture.Tests;

public sealed class EnterpriseSyncContractTests
{
    [Fact]
    public void ExistingPhraseAndCategoryConstructorsDefaultToPersonalScope()
    {
        var category = new Category(Guid.NewGuid(), null, "本地分类", 0, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var phrase = new Phrase(Guid.NewGuid(), "本地话术", PhraseBody.FromText("正文"), category.Id, ShortcutMode.None, null, 0, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.Equal(PhraseScope.Personal, category.Scope);
        Assert.Equal(PhraseScope.Personal, phrase.Scope);
    }

    [Fact]
    public void SyncContractsRemainPlatformIndependentAndRedacted()
    {
        Assert.True(typeof(ISyncProvider).IsInterface);
        Assert.True(typeof(ISyncAccountService).IsInterface);
        Assert.True(typeof(IEnterpriseCatalog).IsInterface);
        Assert.DoesNotContain(typeof(SyncResult).GetProperties(), property =>
            property.Name.Contains("Content", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(ISyncProvider).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true
            || reference.Name?.Contains("Presentation", StringComparison.OrdinalIgnoreCase) == true
            || reference.Name?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true);
    }
}
