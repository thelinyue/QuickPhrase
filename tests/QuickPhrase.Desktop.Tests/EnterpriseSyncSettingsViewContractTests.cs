using System.IO;
using System.Text.RegularExpressions;

namespace QuickPhrase.Desktop.Tests;

public sealed class EnterpriseSyncSettingsViewContractTests
{
    [Fact]
    public void SettingsViewContainsEnterpriseSyncSectionAndHttpRiskBoundary()
    {
        var xaml = Read("Views", "SettingsView.xaml");
        Assert.Contains("Content=\"企业同步\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EnterpriseSyncSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HttpRiskMessage", xaml, StringComparison.Ordinal);
        Assert.Contains("ConnectCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("SynchronizeCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("DisconnectCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("ClearEnterpriseCache_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("PasswordChanged=\"EnterprisePassword_PasswordChanged\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("https://", RegexOptions.IgnoreCase), xaml);
    }

    [Fact]
    public void EnterprisePhraseRowsExposeBadgeAndDisableManagementActions()
    {
        var library = Read("Views", "LibraryView.xaml");
        var lists = Read("DesignSystem", "Styles", "Lists.xaml");
        Assert.Contains("PlacementTarget.DataContext.CanManage", library, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding IsEnterprise, Converter={StaticResource BoolToVisibility}}\"", lists, StringComparison.Ordinal);
        Assert.Contains("Text=\"企业\"", lists, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationControllerWiresStartupAndNetworkRecoveryWithoutPlatformTypesInViewModel()
    {
        var controller = Read("ApplicationController.cs");
        var viewModel = Read("ViewModels", "EnterpriseSyncSettingsViewModel.cs");
        Assert.Contains("NetworkAvailabilityChanged", controller, StringComparison.Ordinal);
        Assert.Contains("SyncAccounts", controller, StringComparison.Ordinal);
        Assert.Contains("SyncProvider", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickPhrase.Platform.Windows", viewModel, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QuickPhrase.sln"))) current = current.Parent;
        return File.ReadAllText(Path.Combine(new[] { current!.FullName, "desktop", "QuickPhrase.Desktop" }.Concat(segments).ToArray()));
    }
}
