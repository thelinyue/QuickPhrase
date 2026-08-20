using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SettingsMigrationDiagnosticsCollection
{
    public const string Name = "Settings migration diagnostics";
}

[Collection(SettingsMigrationDiagnosticsCollection.Name)]
public sealed class SettingsSchemaMigrationTests
{
    private static readonly ShortcutChord DefaultShortcut = new(ShortcutModifiers.Alt, ShortcutKey.Space);

    [Fact]
    public void AppSettings_UsesStructuredLauncherShortcut()
    {
        var settings = CreateSettings(new ShortcutChord(ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, ShortcutKey.F12));

        Assert.Equal(ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, settings.LauncherShortcut.Modifiers);
        Assert.Equal(ShortcutKey.F12, settings.LauncherShortcut.Key);
        Assert.Null(typeof(AppSettings).GetProperty("LauncherShortcutDisplay"));
        Assert.Null(typeof(AppSettings).GetProperty("LauncherShortcutNormalized"));
        Assert.DoesNotContain(
            typeof(AppSettings).GetConstructors(),
            constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)));
    }

    [Fact]
    public async Task FreshDatabase_SeedsSchemaVersion3WithoutLegacySendFields()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));

        var row = await ReadSettingsRowAsync(runtime.DatabasePath);
        using var json = JsonDocument.Parse(row.Json);
        var root = json.RootElement;
        var flashLauncher = root.GetProperty("shortcuts").GetProperty("flashLauncher");

        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal((int)ShortcutModifiers.Alt, flashLauncher.GetProperty("modifiers").GetInt32());
        Assert.Equal((int)ShortcutKey.Space, flashLauncher.GetProperty("keyCode").GetInt32());
        Assert.False(root.TryGetProperty("launcherShortcutDisplay", out _));
        Assert.False(root.TryGetProperty("launcherShortcutNormalized", out _));
        Assert.False(root.GetProperty("quickSendWithoutConfirmation").GetBoolean());
        Assert.False(root.TryGetProperty("autoSend", out _));
    }

    [Fact]
    public async Task Save_WritesSchemaVersion3AndPersistsQuickSendRiskChoice()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var current = await runtime.Settings.LoadAsync();
        var requested = new AppSettings(
            current.Version,
            LaunchOnStartup: true,
            StartMinimized: true,
            StayInTrayOnClose: false,
            new ShortcutChord(ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, ShortcutKey.F12),
            QuickSendWithoutConfirmation: true,
            ClipboardCompatibilityMode: false,
            HasCompletedOnboarding: true,
            OnboardingVersion: 3)
        {
            LauncherEnabledAdapters = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["WXWork"] = false,
                ["CustomAdapter"] = true,
            },
        };

        var result = await runtime.Settings.SaveAsync(requested, current.Version);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(current.Version + 1, result.Value.Version);
        Assert.True(result.Value.QuickSendWithoutConfirmation);
        var row = await ReadSettingsRowAsync(runtime.DatabasePath);
        Assert.Equal(current.Version + 1, row.Version);
        using var json = JsonDocument.Parse(row.Json);
        var root = json.RootElement;
        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        var flashLauncher = root.GetProperty("shortcuts").GetProperty("flashLauncher");
        Assert.Equal((int)(ShortcutModifiers.Ctrl | ShortcutModifiers.Shift), flashLauncher.GetProperty("modifiers").GetInt32());
        Assert.Equal((int)ShortcutKey.F12, flashLauncher.GetProperty("keyCode").GetInt32());
        Assert.False(root.TryGetProperty("launcherShortcutDisplay", out _));
        Assert.False(root.TryGetProperty("launcherShortcutNormalized", out _));
        Assert.True(root.GetProperty("launchOnStartup").GetBoolean());
        Assert.True(root.GetProperty("startMinimized").GetBoolean());
        Assert.False(root.GetProperty("stayInTrayOnClose").GetBoolean());
        Assert.True(root.GetProperty("quickSendWithoutConfirmation").GetBoolean());
        Assert.False(root.TryGetProperty("autoSend", out _));
        Assert.False(root.GetProperty("clipboardCompatibilityMode").GetBoolean());
        Assert.True(root.GetProperty("hasCompletedOnboarding").GetBoolean());
        Assert.Equal(3, root.GetProperty("onboardingVersion").GetInt32());
        Assert.False(root.GetProperty("launcherEnabledAdapters").GetProperty("WXWork").GetBoolean());
        Assert.True(root.GetProperty("launcherEnabledAdapters").GetProperty("CustomAdapter").GetBoolean());

        var reloaded = await runtime.Settings.LoadAsync();
        Assert.True(reloaded.QuickSendWithoutConfirmation);
    }

    [Theory]
    [InlineData("Alt + Space", "Alt+Space", ShortcutModifiers.Alt, ShortcutKey.Space)]
    [InlineData("Ctrl + Space", "Ctrl+Space", ShortcutModifiers.Ctrl, ShortcutKey.Space)]
    [InlineData("Ctrl + Shift + F12", "Ctrl+Shift+F12", ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, ShortcutKey.F12)]
    public async Task Load_MigratesSchemaVersion1WithoutEnablingQuickSendAndRewritesVersion3(
        string display,
        string normalized,
        ShortcutModifiers expectedModifiers,
        ShortcutKey expectedKey)
    {
        using var temp = new TemporaryDirectory();
        await using (var bootstrap = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path)))
        {
        }
        const long rowVersion = 7;
        var legacyJson = $$"""
        {
          "launchOnStartup": true,
          "startMinimized": true,
          "stayInTrayOnClose": false,
          "launcherShortcutDisplay": "{{display}}",
          "launcherShortcutNormalized": "{{normalized}}",
          "autoSend": true,
          "clipboardCompatibilityMode": false,
          "hasCompletedOnboarding": true,
          "onboardingVersion": 4,
          "launcherEnabledAdapters": {
            "WXWork": false,
            "LegacyAdapter": true
          }
        }
        """;
        await ReplaceSettingsRowAsync(new QuickPhraseDataOptions(temp.Path).DatabasePath, legacyJson, rowVersion);

        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var loaded = await runtime.Settings.LoadAsync();

        Assert.Equal(rowVersion, loaded.Version);
        Assert.Equal(new ShortcutChord(expectedModifiers, expectedKey), loaded.LauncherShortcut);
        Assert.True(loaded.LaunchOnStartup);
        Assert.True(loaded.StartMinimized);
        Assert.False(loaded.StayInTrayOnClose);
        Assert.False(loaded.QuickSendWithoutConfirmation);
        Assert.False(loaded.ClipboardCompatibilityMode);
        Assert.True(loaded.HasCompletedOnboarding);
        Assert.Equal(4, loaded.OnboardingVersion);
        Assert.False(loaded.LauncherEnabledAdapters["WXWork"]);
        Assert.True(loaded.LauncherEnabledAdapters["LegacyAdapter"]);

        var migrated = await ReadSettingsRowAsync(runtime.DatabasePath);
        Assert.Equal(rowVersion, migrated.Version);
        using var json = JsonDocument.Parse(migrated.Json);
        Assert.Equal(3, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var flashLauncher = json.RootElement.GetProperty("shortcuts").GetProperty("flashLauncher");
        Assert.Equal((int)expectedModifiers, flashLauncher.GetProperty("modifiers").GetInt32());
        Assert.Equal((int)expectedKey, flashLauncher.GetProperty("keyCode").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("launcherShortcutDisplay", out _));
        Assert.False(json.RootElement.TryGetProperty("launcherShortcutNormalized", out _));
        Assert.False(json.RootElement.GetProperty("quickSendWithoutConfirmation").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("autoSend", out _));
    }

    [Fact]
    public async Task Load_InvalidLegacyShortcutFallsBackAndWritesSanitizedChineseTraceLog()
    {
        using var temp = new TemporaryDirectory();
        await using (var bootstrap = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path)))
        {
        }
        const string sensitiveInput = "Ctrl+SecretKey-DO_NOT_LOG";
        var legacyJson = $$"""
        {
          "launchOnStartup": true,
          "launcherShortcutDisplay": "{{sensitiveInput}}",
          "launcherShortcutNormalized": "{{sensitiveInput}}",
          "launcherEnabledAdapters": { "WXWork": false }
        }
        """;
        var databasePath = new QuickPhraseDataOptions(temp.Path).DatabasePath;
        await ReplaceSettingsRowAsync(databasePath, legacyJson, 5);
        var originalError = Console.Error;
        using var diagnostics = new StringWriter();
        Console.SetError(diagnostics);
        try
        {
            await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
            var loaded = await runtime.Settings.LoadAsync();

            Assert.Equal(DefaultShortcut, loaded.LauncherShortcut);
            Assert.True(loaded.LaunchOnStartup);
            Assert.False(loaded.LauncherEnabledAdapters["WXWork"]);
            Assert.Equal(5, loaded.Version);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var log = diagnostics.ToString();
        Assert.Contains("快捷键迁移", log, StringComparison.Ordinal);
        Assert.Contains("TraceId=", log, StringComparison.Ordinal);
        Assert.Contains("结果码=", log, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveInput, log, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretKey", log, StringComparison.Ordinal);

        var repaired = await ReadSettingsRowAsync(databasePath);
        Assert.Equal(5, repaired.Version);
        using var json = JsonDocument.Parse(repaired.Json);
        var shortcut = json.RootElement.GetProperty("shortcuts").GetProperty("flashLauncher");
        Assert.Equal((int)ShortcutModifiers.Alt, shortcut.GetProperty("modifiers").GetInt32());
        Assert.Equal((int)ShortcutKey.Space, shortcut.GetProperty("keyCode").GetInt32());
    }

    [Fact]
    public async Task Save_RejectsInvalidStructuredShortcutWithoutChangingStoredRow()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var current = await runtime.Settings.LoadAsync();
        var before = await ReadSettingsRowAsync(runtime.DatabasePath);
        var invalid = current with { LauncherShortcut = new ShortcutChord(ShortcutModifiers.None, ShortcutKey.Space) };

        var result = await runtime.Settings.SaveAsync(invalid, current.Version);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_FAILED", result.Error?.Code);
        Assert.Contains("快捷键", result.Error?.Message ?? string.Empty, StringComparison.Ordinal);
        var after = await ReadSettingsRowAsync(runtime.DatabasePath);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Json, after.Json);
    }

    [Fact]
    public async Task Load_MigratesSchemaVersion2WithoutEnablingQuickSend()
    {
        using var temp = new TemporaryDirectory();
        await using (var bootstrap = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path)))
        {
        }
        const string json = """
        {
          "schemaVersion": 2,
          "shortcuts": {
            "flashLauncher": {
              "modifiers": 9,
              "keyCode": 28
            }
          },
          "launchOnStartup": false,
          "startMinimized": true,
          "stayInTrayOnClose": true,
          "autoSend": true,
          "clipboardCompatibilityMode": true,
          "hasCompletedOnboarding": true,
          "onboardingVersion": 2,
          "launcherEnabledAdapters": { "WXWork": true }
        }
        """;
        var databasePath = new QuickPhraseDataOptions(temp.Path).DatabasePath;
        await ReplaceSettingsRowAsync(databasePath, json, 9);

        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));
        var loaded = await runtime.Settings.LoadAsync();

        Assert.Equal(9, loaded.Version);
        Assert.Equal(new ShortcutChord(ShortcutModifiers.Ctrl | ShortcutModifiers.Win, ShortcutKey.Digit0), loaded.LauncherShortcut);
        Assert.True(loaded.StartMinimized);
        Assert.True(loaded.HasCompletedOnboarding);
        Assert.Equal(2, loaded.OnboardingVersion);
        Assert.False(loaded.QuickSendWithoutConfirmation);

        var migrated = await ReadSettingsRowAsync(runtime.DatabasePath);
        using var migratedJson = JsonDocument.Parse(migrated.Json);
        Assert.Equal(3, migratedJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(migratedJson.RootElement.GetProperty("quickSendWithoutConfirmation").GetBoolean());
        Assert.False(migratedJson.RootElement.TryGetProperty("autoSend", out _));
    }

    private static AppSettings CreateSettings(ShortcutChord shortcut) =>
        new(1, false, false, true, shortcut, false, true)
        {
            LauncherEnabledAdapters = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["WXWork"] = true },
        };

    private static async Task ReplaceSettingsRowAsync(string databasePath, string json, long version)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE settings SET value_json=$json, version=$version WHERE key='app.settings';";
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$version", version);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<(string Json, long Version)> ReadSettingsRowAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json, version FROM settings WHERE key='app.settings';";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-SettingsSchema-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
