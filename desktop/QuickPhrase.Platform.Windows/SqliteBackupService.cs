using Microsoft.Data.Sqlite;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 使用 SQLite Backup API 创建一致性快照。升级前调用它，避免直接复制 WAL 文件造成半成品备份。
/// </summary>
internal static class SqliteBackupService
{
    public static async Task<string> CreateAsync(QuickPhraseDataOptions options, string reason, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.BackupDirectory);
        var safeReason = new string((reason ?? "backup").Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safeReason)) safeReason = "backup";
        var timestamp = options.TimeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff");
        var destinationPath = Path.Combine(options.BackupDirectory, $"quickphrase-{safeReason}-{timestamp}.db");
        var connections = new SqliteConnectionFactory(options.DatabasePath);
        await using var source = await connections.OpenWriterAsync(cancellationToken);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
        return destinationPath;
    }
}
