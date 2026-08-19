using System.Threading.Channels;

namespace QuickPhrase.Desktop;

/// <summary>
/// 使用次数属于非关键统计：投递线程只负责入队，单个后台消费者顺序写库。
/// 写入失败仅输出中文诊断，不允许延迟或回滚已经完成的文本插入。
/// </summary>
internal sealed class UsageUpdateQueue : IAsyncDisposable
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Func<Guid, CancellationToken, Task> _writer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private readonly object _idleGate = new();
    private TaskCompletionSource _idle = CompletedSource();
    private int _pending;

    internal UsageUpdateQueue(Func<Guid, CancellationToken, Task> writer)
    {
        _writer = writer;
        _worker = ConsumeAsync();
    }

    public Task EnqueueAsync(Guid phraseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_idleGate)
        {
            if (_pending == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending++;
            if (_channel.Writer.TryWrite(phraseId))
                return Task.CompletedTask;
            _pending--;
            if (_pending == 0) _idle.TrySetResult();
        }
        Console.Error.WriteLine("使用次数队列已满，本次统计已跳过，不影响话术插入。");
        return Task.CompletedTask;
    }

    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        Task idle;
        lock (_idleGate) idle = _idle.Task;
        return await Task.WhenAny(idle, Task.Delay(timeout)).ConfigureAwait(false) == idle;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        if (!await DrainAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false))
        {
            Console.Error.WriteLine("使用次数队列在 15 秒内未排空，剩余统计已取消。");
            _shutdown.Cancel();
        }
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }

    private async Task ConsumeAsync()
    {
        await foreach (var phraseId in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
        {
            try { await _writer(phraseId, _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception exception) { Console.Error.WriteLine($"使用次数保存失败：{exception.Message}"); }
            finally
            {
                lock (_idleGate)
                {
                    _pending--;
                    if (_pending == 0) _idle.TrySetResult();
                }
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult();
        return source;
    }
}
