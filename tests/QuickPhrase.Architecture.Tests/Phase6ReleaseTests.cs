using Microsoft.Win32;
using QuickPhrase.Desktop;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

public sealed class Phase6ReleaseTests
{
    [Fact]
    public void PrimaryUpgradeShutdownExitsBeforeStartingTheApplication()
    {
        var exitCode = -1;

        var handled = App.HandlePrimaryUpgradeShutdown(true, code => exitCode = code);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void StartupRegistrationQuotesExecutablePathAndUsesBackgroundMode()
    {
        var valueName = $"QuickPhrase.Test.{Guid.NewGuid():N}";
        var registration = new WindowsStartupRegistration(valueName);
        try
        {
            registration.SetEnabled(true, @"C:\Users\Test User\QuickPhrase\QuickPhrase.exe");
            using var key = Registry.CurrentUser.OpenSubKey(WindowsStartupRegistration.RunKeyPath);
            Assert.Equal(
                "\"C:\\Users\\Test User\\QuickPhrase\\QuickPhrase.exe\" --background",
                key?.GetValue(valueName) as string);
            Assert.True(registration.IsEnabled());
        }
        finally
        {
            registration.SetEnabled(false, null);
        }
        Assert.False(registration.IsEnabled());
    }
}
