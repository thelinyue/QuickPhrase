using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 独立 STA 剪贴板事务。所有 OLE Clipboard 调用都在同一条 STA 线程执行，
/// 并通过序列号保护用户在投递期间新复制的内容不被旧内容覆盖。
/// </summary>
internal sealed class ClipboardTransaction : IClipboardTransaction, IDisposable
{
    private readonly BlockingCollection<ClipboardWork> _queue = new(16);
    private readonly Thread _thread;
    private bool _disposed;

    public ClipboardTransaction()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "QuickPhrase Clipboard STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<ClipboardResult> CopyOnlyAsync(string text, CancellationToken cancellationToken) =>
        EnqueueAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            if (!TrySetText(text)) return ClipboardResult.Failed("CLIPBOARD_SET_FAILED");
            return ClipboardResult.Copied;
        }, cancellationToken);

    public Task<ClipboardResult> PasteAsync(string text, DeliveryTarget target, CancellationToken cancellationToken) =>
        EnqueueAsync(token =>
        {
            if (!WindowsTargetContextStore.Shared.TryGet(target.RuntimeKey, out var identity))
                return ClipboardResult.Failed("TARGET_CONTEXT_MISSING");
            return PasteCore(text, identity, token);
        }, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private ClipboardResult PasteCore(string text, WindowsTargetIdentity target, CancellationToken cancellationToken)
    {
        IDataObject? original = null;
        uint quickPhraseSequence = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            original = Clipboard.GetDataObject();
            cancellationToken.ThrowIfCancellationRequested();
            if (!TrySetText(text)) return ClipboardResult.Failed("CLIPBOARD_SET_FAILED");
            quickPhraseSequence = WindowsNativeMethods.GetClipboardSequenceNumber();
            cancellationToken.ThrowIfCancellationRequested();
            if (!WindowsTargetDetector.IsIdentityCurrent(target) || !WindowsNativeMethods.SetForegroundWindow(target.Hwnd))
                return ClipboardResult.Failed("TARGET_VALIDATION_FAILED");
            Thread.Sleep(30);
            cancellationToken.ThrowIfCancellationRequested();
            // 粘贴前再次确认仍是已捕获的目标窗口，前台切换时宁可失败也不能向错误窗口发送 Ctrl+V。
            if (WindowsNativeMethods.GetForegroundWindow() != target.Hwnd)
                return ClipboardResult.Failed("TARGET_VALIDATION_FAILED");
            if (!WindowsNativeMethods.SendCtrlV()) return ClipboardResult.Failed("CLIPBOARD_PASTE_FAILED");
            Thread.Sleep(30);
            var currentSequence = WindowsNativeMethods.GetClipboardSequenceNumber();
            if (currentSequence == quickPhraseSequence && original is not null)
            {
                try { Clipboard.SetDataObject(original, true); }
                catch (ExternalException) { /* 恢复失败不能覆盖用户当前内容，也不重复粘贴。 */ }
            }
            return ClipboardResult.Pasted;
        }
        catch (ExternalException)
        {
            return ClipboardResult.Failed("CLIPBOARD_FAILED");
        }
    }

    private bool TrySetText(string text)
    {
        var delays = new[] { 20, 40, 80, 160, 320 };
        foreach (var delay in delays)
        {
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                return true;
            }
            catch (ExternalException) { Thread.Sleep(delay); }
        }
        return false;
    }

    private async Task<ClipboardResult> EnqueueAsync(Func<CancellationToken, ClipboardResult> operation, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ClipboardTransaction));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = new ClipboardWork<ClipboardResult>(operation, linked.Token);
        try { _queue.Add(work, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(ClipboardTransaction)); }

        var completed = await Task.WhenAny(work.Task, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(false);
        if (completed == work.Task) return await work.Task.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        linked.Cancel();
        return ClipboardResult.Failed("CLIPBOARD_TIMEOUT");
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable()) work.Execute();
    }

    private abstract class ClipboardWork
    {
        public abstract void Execute();
    }

    private sealed class ClipboardWork<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken) : ClipboardWork
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> Task => _completion.Task;
        public override void Execute()
        {
            if (cancellationToken.IsCancellationRequested) { _completion.TrySetCanceled(cancellationToken); return; }
            try { _completion.TrySetResult(operation(cancellationToken)); }
            catch (Exception exception) { _completion.TrySetException(exception); }
        }
    }
}
