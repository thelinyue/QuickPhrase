using Microsoft.Data.Sqlite;

namespace QuickPhrase.Platform.Windows;

/// <summary>集中配置 SQLite 连接，防止读写连接遗漏外键或 busy timeout。</summary>
internal sealed class SqliteConnectionFactory
{
    private readonly string _databasePath;

    public SqliteConnectionFactory(string databasePath) => _databasePath = databasePath;

    public async Task<SqliteConnection> OpenWriterAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.DefaultTimeout = 5;
        await connection.OpenAsync(cancellationToken);
        try
        {
            await ExecutePragmasAsync(connection, writer: true, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<SqliteConnection> OpenReadAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.DefaultTimeout = 5;
        await connection.OpenAsync(cancellationToken);
        try
        {
            await ExecutePragmasAsync(connection, writer: false, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecutePragmasAsync(SqliteConnection connection, bool writer, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = writer
            ? "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"
            : "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
