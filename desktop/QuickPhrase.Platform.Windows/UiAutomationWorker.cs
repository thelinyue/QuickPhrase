using System.Collections.Concurrent;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 无窗口 COM MTA UIA Worker。WPF STA、Launcher 和全局热键循环永远不直接执行 UIA 调用。
/// </summary>
internal sealed class UiAutomationWorker : IUiAutomationWorker, IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new(64);
    private readonly Thread _thread;
    private bool _disposed;

    public UiAutomationWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "QuickPhrase UIA MTA" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public async Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken, TimeSpan timeout)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UiAutomationWorker));
        var item = new WorkItem<T>(operation, cancellationToken);
        try { _queue.Add(item, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(UiAutomationWorker)); }

        var completed = await Task.WhenAny(item.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != item.Task)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("UI Automation 操作超时。");
        }
        return await item.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable()) item.Execute();
    }

    private abstract class WorkItem
    {
        public abstract void Execute();
    }

    private sealed class WorkItem<T>(Func<T> operation, CancellationToken cancellationToken) : WorkItem
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> Task => _completion.Task;

        public override void Execute()
        {
            if (cancellationToken.IsCancellationRequested) { _completion.TrySetCanceled(cancellationToken); return; }
            try { _completion.TrySetResult(operation()); }
            catch (Exception exception) { _completion.TrySetException(exception); }
        }
    }
}
