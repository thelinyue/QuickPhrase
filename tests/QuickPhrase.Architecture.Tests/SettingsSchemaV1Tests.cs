using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SettingsDocumentDiagnosticsCollection
{
    public const string Name = "Settings document diagnostics";
}

[Collection(SettingsDocumentDiagnosticsCollection.Name)]
public sealed class SettingsSchemaV1Tests
{
    private static readonly ShortcutChord DefaultShortcut = new(ShortcutModifiers.Alt, ShortcutKey.Space);
    private static readonly string[] CurrentProperties =
    [
        "schemaVersion",
        "shortcuts",
        "launchOnStartup",
        "startMinimized",
        "stayInTrayOnClose",
        "quickSendWithoutConfirmation",
        "clipboardCompatibilityMode",
        "hasCompletedOnboarding",
        "onboardingVersion",
    ];

    [Fact]
    public void AppSettingsUsesStructuredLauncherShortcut()
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
    public async Task FreshDatabaseSeedsOnlyCurrentV1SettingsFields()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(new QuickPhraseDataOptions(temp.Path));

        var row = await ReadSettingsRowAsync(runtime.DatabasePath);
        using var json = JsonDocument.Parse(row.Json);
        var root = json.RootElement;
        var flashLauncher = root.GetProperty("shortcuts").GetProperty("flashLauncher");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        AssertExactProperties(root, CurrentProperties);
        AssertExactProperties(root.GetProperty("shortcuts"), "flashLauncher");
        AssertExactProperties(flashLauncher, "modifiers", "keyCode");
        Assert.Equal((int)ShortcutModifiers.Alt, flashLauncher.GetProperty("modifiers").GetInt32());
        Assert.Equal((int)ShortcutKey.Space, flashLauncher.GetProperty("keyCode").GetInt32());
        Assert.False(root.GetProperty("quickSendWithoutConfirmation").GetBoolean());
    }

    [Fact]
    public async Task SaveWritesCurrentV1SettingsDocument()
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
            OnboardingVersion: 1);

        var result = await runtime.Settings.SaveAsync(requested, current.Version);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(current.Version + 1, result.Value.Version);
        var row = await ReadSettingsRowAsync(runtime.DatabasePath);
        Assert.Equal(current.Version + 1, row.Version);
        using var json = JsonDocument.Parse(row.Json);
        var root = json.RootElement;
        var flashLauncher = root.GetProperty("shortcuts").GetProperty("flashLauncher");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        AssertExactProperties(root, CurrentProperties);
        Assert.Equal((int)(ShortcutModifiers.Ctrl | ShortcutModifiers.Shift), flashLauncher.GetProperty("modifiers").GetInt32());
        Assert.Equal((int)ShortcutKey.F12, flashLauncher.GetProperty("keyCode").GetInt32());
        Assert.True(root.GetProperty("quickSendWithoutConfirmation").GetBoolean());
        Assert.False(root.GetProperty("clipboardCompatibilityMode").GetBoolean());
        Assert.True(root.GetProperty("hasCompletedOnboarding").GetBoolean());
        Assert.Equal(1, root.GetProperty("onboardingVersion").GetInt32());
    }

    [Fact]
    public async Task UnsupportedSettingsDocumentIsResetToDefaultV1()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var bootstrap = await QuickPhraseDataRuntime.OpenAsync(options))
        {
        }

        await ReplaceSettingsRowAsync(options.DatabasePath, """{"schemaVersion":99,"autoSend":true}""", 9);

        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(options);
        var loaded = await runtime.Settings.LoadAsync();

        Assert.Equal(9, loaded.Version);
        Assert.Equal(DefaultShortcut, loaded.LauncherShortcut);
        Assert.False(loaded.LaunchOnStartup);
        Assert.False(loaded.StartMinimized);
        Assert.True(loaded.StayInTrayOnClose);
        Assert.False(loaded.QuickSendWithoutConfirmation);
        Assert.True(loaded.ClipboardCompatibilityMode);
        Assert.False(loaded.HasCompletedOnboarding);
        Assert.Equal(0, loaded.OnboardingVersion);

        var reset = await ReadSettingsRowAsync(options.DatabasePath);
        Assert.Equal(9, reset.Version);
        using var document = JsonDocument.Parse(reset.Json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        AssertExactProperties(document.RootElement, CurrentProperties);
    }

    [Fact]
    public async Task InvalidSettingsJsonIsResetToDefaultV1()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var bootstrap = await QuickPhraseDataRuntime.OpenAsync(options))
        {
        }

        await ReplaceSettingsRowAsync(options.DatabasePath, "{ this is not valid json", 7);

        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(options);
        var loaded = await runtime.Settings.LoadAsync();

        Assert.Equal(7, loaded.Version);
        Assert.Equal(DefaultShortcut, loaded.LauncherShortcut);
        var reset = await ReadSettingsRowAsync(options.DatabasePath);
        Assert.Equal(7, reset.Version);
        using var document = JsonDocument.Parse(reset.Json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        AssertExactProperties(document.RootElement, CurrentProperties);
    }

    [Fact]
    public async Task V1DocumentWithRemovedFieldIsResetToDefaultV1()
    {
        using var temp = new TemporaryDirectory();
        var options = new QuickPhraseDataOptions(temp.Path);
        await using (var bootstrap = await QuickPhraseDataRuntime.OpenAsync(options))
        {
        }

        const string json = """
        {
          "schemaVersion": 1,
          "shortcuts": { "flashLauncher": { "modifiers": 2, "keyCode": 1 } },
          "launchOnStartup": true,
          "startMinimized": true,
          "stayInTrayOnClose": false,
          "quickSendWithoutConfirmation": true,
          "clipboardCompatibilityMode": false,
          "hasCompletedOnboarding": true,
          "onboardingVersion": 1,
          "launcherEnabledAdapters": { "WXWork": true }
        }
        """;
        await ReplaceSettingsRowAsync(options.DatabasePath, json, 4);

        await using var runtime = await QuickPhraseDataRuntime.OpenAsync(options);
        var loaded = await runtime.Settings.LoadAsync();

        Assert.Equal(4, loaded.Version);
        Assert.Equal(DefaultShortcut, loaded.LauncherShortcut);
        Assert.False(loaded.QuickSendWithoutConfirmation);
        var reset = await ReadSettingsRowAsync(options.DatabasePath);
        using var document = JsonDocument.Parse(reset.Json);
        AssertExactProperties(document.RootElement, CurrentProperties);
        Assert.False(document.RootElement.TryGetProperty("launcherEnabledAdapters", out _));
    }

    [Fact]
    public async Task SaveRejectsInvalidStructuredShortcutWithoutChangingStoredRow()
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

    private static AppSettings CreateSettings(ShortcutChord shortcut) =>
        new(1, false, false, true, shortcut, false, true);

    private static void AssertExactProperties(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(expected.Order(), actual);
    }

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
        public string Path { get; } = Directory.CreateTempSubdirectory("QuickPhrase-SettingsV1-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
