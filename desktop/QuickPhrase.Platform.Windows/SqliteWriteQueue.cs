using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace QuickPhrase.Platform.Windows;

/// <summary>SQLite 单写者队列：所有写事务按入队顺序使用同一连接执行，避免并发写竞争。</summary>
internal sealed class SqliteWriteQueue : IDatabaseWriteQueue, IAsyncDisposable
{
    private readonly Channel<IWorkItem> _channel;
    private readonly SqliteConnectionFactory _connections;
    private readonly TimeSpan _shutdownTimeout;
    private readonly CancellationTokenSource _stop = new();
    private SqliteConnection? _writerConnection;
    private Task? _worker;
    private int _accepting;

    public SqliteWriteQueue(SqliteConnectionFactory connections, int capacity, TimeSpan shutdownTimeout)
    {
        _connections = connections;
        _shutdownTimeout = shutdownTimeout;
        _channel = Channel.CreateBounded<IWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _writerConnection = await _connections.OpenWriterAsync(cancellationToken);
        Interlocked.Exchange(ref _accepting, 1);
        _worker = Task.Run(ProcessAsync, CancellationToken.None);
    }

    public async Task<T> EnqueueAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _accepting) == 0) throw new InvalidOperationException("SQLite 写入队列尚未启动或已经关闭。");
        var item = new WorkItem<T>(operation, cancellationToken);
        await _channel.Writer.WriteAsync(item, cancellationToken);
        return await item.Completion.Task.WaitAsync(cancellationToken);
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_stop.Token))
                await item.ExecuteAsync(_writerConnection!, _stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            while (_channel.Reader.TryRead(out var remaining))
                remaining.Cancel();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SQLite 写入队列异常：{ex.Message}");
            while (_channel.Reader.TryRead(out var remaining))
                remaining.Fail(ex);
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _accepting, 0) == 0) return;
        _channel.Writer.TryComplete();
        if (_worker is null) return;
        var completed = await Task.WhenAny(_worker, Task.Delay(_shutdownTimeout));
        if (completed != _worker)
        {
            Console.Error.WriteLine("SQLite 写入队列在规定时间内未排空，正在取消剩余操作。");
            await _stop.CancelAsync();
            try { await _worker; } catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
        if (_writerConnection is not null) await _writerConnection.DisposeAsync();
    }

    private interface IWorkItem
    {
        Task ExecuteAsync(SqliteConnection connection, CancellationToken stopToken);
        void Cancel();
        void Fail(Exception exception);
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<SqliteConnection, CancellationToken, Task<T>> _operation;
        private readonly CancellationToken _cancellationToken;
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem(Func<SqliteConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            _operation = operation;
            _cancellationToken = cancellationToken;
        }

        public async Task ExecuteAsync(SqliteConnection connection, CancellationToken stopToken)
        {
            if (_cancellationToken.IsCancellationRequested || stopToken.IsCancellationRequested)
            {
                Cancel();
                return;
            }
            try { Completion.TrySetResult(await _operation(connection, _cancellationToken)); }
            catch (OperationCanceledException) { Cancel(); }
            catch (Exception ex) { Fail(ex); }
        }

        public void Cancel() => Completion.TrySetCanceled(_cancellationToken.IsCancellationRequested ? _cancellationToken : new CancellationToken(true));
        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }
}
