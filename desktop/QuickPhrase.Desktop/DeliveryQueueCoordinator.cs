using QuickPhrase.Core;

namespace QuickPhrase.Desktop;

internal sealed record DeliveryQueueStatus(bool IsProcessing, int WaitingCount);

internal sealed record DeliveryQueueTicket(
    bool Accepted,
    string Code,
    int WaitingCount,
    Task<DeliveryResult>? Completion);

internal sealed record DeliveryBatchSummary(int CompletedCount, int FailedCount, int CancelledCount);

/// <summary>
/// 连续投递队列按 FIFO 串行执行话术。入队操作只更新内存状态，实际投递在线程池启动，
/// 避免目标激活、焦点等待等 Win32 操作阻塞 WPF UI 线程；失败结果不会自动重试。
/// </summary>
internal sealed class DeliveryQueueCoordinator : IAsyncDisposable
{
    private readonly ITextDeliveryStateMachine _delivery;
    private readonly int _maxPending;
    private readonly object _gate = new();
    private readonly Queue<QueuedDelivery> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task _worker = Task.CompletedTask;
    private bool _processing;
    private bool _accepting = true;
    private int _batchCompleted;
    private int _batchFailed;
    private int _batchCancelled;

    internal DeliveryQueueCoordinator(ITextDeliveryStateMachine delivery, int maxPending = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPending);
        _delivery = delivery;
        _maxPending = maxPending;
    }

    public event Action<DeliveryQueueStatus>? StatusChanged;
    public event Action<DeliveryResult>? ItemFailed;
    public event Action<DeliveryResult, string?>? ItemCompleted;
   public event Action<DeliveryBatchSummary>? BatchCompleted;

    public DeliveryQueueStatus Status
    {
        get { lock (_gate) return new(_processing, _pending.Count); }
    }

    public DeliveryQueueTicket TryEnqueue(DeliveryRequest request, Guid queueSessionId, string? query = null)
    {
        QueuedDelivery item;
        DeliveryQueueStatus status;
        TaskCompletionSource? workerStart = null;
        lock (_gate)
        {
            if (!_accepting)
                return Rejected("DELIVERY_QUEUE_CANCELLED", _pending.Count);
            if (_processing && _pending.Count >= _maxPending)
                return Rejected("DELIVERY_QUEUE_FULL", _pending.Count);

            item = new(request, queueSessionId, query);
            if (!_processing)
            {
                _processing = true;
                ResetBatchCounters();
                var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _worker = Task.Run(async () =>
                {
                    await startSignal.Task.ConfigureAwait(false);
                    await ProcessLoopAsync(item).ConfigureAwait(false);
                });
                workerStart = startSignal;
            }
            else
            {
                _pending.Enqueue(item);
            }
            status = new(true, _pending.Count);
        }

        try
        {
            StatusChanged?.Invoke(status);
        }
        finally
        {
            // 先发布“处理中”状态，再放行后台工作，避免极快失败时 UI 收到倒序状态。
            workerStart?.TrySetResult();
        }
        return new(true, "DELIVERY_QUEUED", status.WaitingCount, item.Completion.Task);
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        List<QueuedDelivery> cancelled;
        lock (_gate)
        {
            if (!_accepting) return;
            _accepting = false;
            cancelled = _pending.ToList();
            _pending.Clear();
            _shutdown.Cancel();
            worker = _worker;
        }

        foreach (var item in cancelled)
            CompleteItem(item, CancelledResult("DELIVERY_QUEUE_CANCELLED", "应用正在退出，等待中的话术已取消。"));
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }

    private async Task ProcessLoopAsync(QueuedDelivery current)
    {
        while (true)
        {
            DeliveryResult result;
            try
            {
                result = await _delivery.DeliverAsync(current.Request, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = CancelledResult("DELIVERY_QUEUE_CANCELLED", "投递队列已取消。" );
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"投递队列处理异常，当前话术不会自动重试：{exception.GetType().Name}: {exception.Message}");
                result = FailedResult("INSERT_FAILED", "投递失败，未自动重试。" );
            }

            CompleteItem(current, result);
            if (result.Status is DeliveryStatus.Failed or DeliveryStatus.Cancelled)
                ItemFailed?.Invoke(result);

            List<QueuedDelivery>? targetCancelled = null;
            QueuedDelivery? next = null;
            DeliveryQueueStatus status;
            DeliveryBatchSummary? summary = null;
            lock (_gate)
            {
                CountResult(result);
                if (result.ErrorCode == "TARGET_CHANGED" && current.Request.Target is not null)
                {
                    targetCancelled = RemovePendingForTarget(current.Request.Target);
                    _batchCancelled += targetCancelled.Count;
                }

                if (_pending.TryDequeue(out next))
                {
                    status = new(true, _pending.Count);
                }
                else
                {
                    _processing = false;
                    status = new(false, 0);
                    summary = new(_batchCompleted, _batchFailed, _batchCancelled);
                }
            }

            if (targetCancelled is not null)
            {
                foreach (var item in targetCancelled)
                    CompleteItem(item, CancelledResult("DELIVERY_QUEUE_CANCELLED", "目标窗口已变化，剩余话术已取消。"));
            }
            StatusChanged?.Invoke(status);
            if (summary is not null) BatchCompleted?.Invoke(summary);
            if (next is null) return;
            current = next;
        }
    }

    private List<QueuedDelivery> RemovePendingForTarget(DeliveryTarget target)
    {
        var cancelled = new List<QueuedDelivery>();
        var retained = new Queue<QueuedDelivery>();
        while (_pending.TryDequeue(out var item))
        {
            if (item.Request.Target == target) cancelled.Add(item);
            else retained.Enqueue(item);
        }
        while (retained.TryDequeue(out var item)) _pending.Enqueue(item);
        return cancelled;
    }

    private void CountResult(DeliveryResult result)
    {
        if (result.Status == DeliveryStatus.Cancelled) _batchCancelled++;
        else if (result.Status == DeliveryStatus.Failed) _batchFailed++;
        else _batchCompleted++;
    }

    private void ResetBatchCounters() => (_batchCompleted, _batchFailed, _batchCancelled) = (0, 0, 0);

    private void CompleteItem(QueuedDelivery item, DeliveryResult result)
    {
        item.Completion.TrySetResult(result);
        ItemCompleted?.Invoke(result, item.Query);
    }
    private static DeliveryQueueTicket Rejected(string code, int waiting) => new(false, code, waiting, null);
    private static DeliveryResult FailedResult(string code, string message) =>
        new(DeliveryStatus.Failed, DeliveryEffect.None, DeliveryStage.NotStarted, DeliveryConfidence.Confirmed, code, message, false, Guid.NewGuid());

    private static DeliveryResult CancelledResult(string code, string message) =>
        new(DeliveryStatus.Cancelled, DeliveryEffect.None, DeliveryStage.NotStarted, DeliveryConfidence.Confirmed, code, message, false, Guid.NewGuid());

    private sealed record QueuedDelivery(DeliveryRequest Request, Guid QueueSessionId, string? Query)
    {
        public TaskCompletionSource<DeliveryResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}



