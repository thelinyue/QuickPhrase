namespace QuickPhrase.Platform.Windows;

/// <summary>本地数据运行时的路径与生命周期配置；默认路径位于当前用户 LocalAppData。</summary>
public sealed class QuickPhraseDataOptions
{
    public QuickPhraseDataOptions(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("数据根目录不能为空。", nameof(rootPath));
        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }
    public string DataDirectory => Path.Combine(RootPath, "Data");
    public string DatabasePath => Path.Combine(DataDirectory, "quickphrase.db");
    public string SecretsDirectory => Path.Combine(DataDirectory, "Secrets");
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public int WriteQueueCapacity { get; init; } = 128;
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public static QuickPhraseDataOptions ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickPhrase"));
}
