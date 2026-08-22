using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 独立 STA 剪贴板事务。文字和图片共用同一个受序列号保护的 finally：只要 QuickPhrase
/// 已写入 Payload，后续目标、前台、按键、异常或取消路径都会尽力恢复原剪贴板；第三方已修改时绝不覆盖。
/// </summary>
internal sealed class ClipboardTransaction : IClipboardTransaction, IDisposable
{
    private readonly BlockingCollection<ClipboardWork> _queue = new(16);
    private readonly Thread _thread;
    private readonly IClipboardPlatform _platform;
    private readonly WindowsTargetContextStore _contexts;
    private bool _disposed;

    public ClipboardTransaction() : this(new WindowsClipboardPlatform(), WindowsTargetContextStore.Shared) { }

    internal ClipboardTransaction(IClipboardPlatform platform, WindowsTargetContextStore contexts)
    {
        _platform = platform;
        _contexts = contexts;
        _thread = new Thread(Run) { IsBackground = true, Name = "QuickPhrase Clipboard STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<ClipboardResult> CopyOnlyAsync(string text, CancellationToken cancellationToken) =>
        EnqueueAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            return _platform.TrySetText(text) ? ClipboardResult.Copied : ClipboardResult.Failed("CLIPBOARD_SET_FAILED");
        }, cancellationToken);

    public Task<ClipboardResult> PasteAsync(string text, DeliveryTarget target, CancellationToken cancellationToken) =>
        EnqueueAsync(token =>
        {
            if (!_contexts.TryGet(target.RuntimeKey, out var identity))
                return ClipboardResult.Failed("TARGET_CONTEXT_MISSING");
            return PastePayloadCore(() => _platform.TrySetText(text), identity, token);
        }, cancellationToken);

    public Task<ClipboardResult> PasteImageAsync(byte[] normalizedImage, DeliveryTarget target, CancellationToken cancellationToken) =>
        EnqueueAsync(token =>
        {
            if (!_contexts.TryGet(target.RuntimeKey, out var identity))
                return ClipboardResult.Failed("TARGET_CONTEXT_MISSING");

            try
            {
                using var stream = new MemoryStream(normalizedImage, writable: false);
                using var decoded = System.Drawing.Image.FromStream(stream, false, true);
                using var bitmap = new System.Drawing.Bitmap(decoded);
                return PastePayloadCore(() => _platform.TrySetImage(bitmap), identity, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return ClipboardResult.Failed("IMAGE_DECODE_FAILED"); }
        }, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private ClipboardResult PastePayloadCore(Func<bool> setPayload, WindowsTargetIdentity target, CancellationToken cancellationToken)
    {
        IDataObject? original = null;
        uint quickPhraseSequence = 0;
        var payloadWasSet = false;
        var sequenceCaptured = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            original = _platform.CaptureDataObject();
            cancellationToken.ThrowIfCancellationRequested();
            if (!setPayload()) return ClipboardResult.Failed("CLIPBOARD_SET_FAILED");
            payloadWasSet = true;
            quickPhraseSequence = _platform.GetSequenceNumber();
            sequenceCaptured = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!_platform.IsIdentityCurrent(target) || !_platform.SetForegroundWindow(target.Hwnd))
                return ClipboardResult.Failed("TARGET_VALIDATION_FAILED");
            _platform.Delay(30);
            cancellationToken.ThrowIfCancellationRequested();
            // 粘贴前再次确认仍是已捕获目标，前台变化时宁可失败，也不能向错误窗口注入 Ctrl+V。
            if (_platform.GetForegroundWindow() != target.Hwnd)
                return ClipboardResult.Failed("TARGET_VALIDATION_FAILED");
            if (!_platform.SendCtrlV()) return ClipboardResult.Failed("CLIPBOARD_PASTE_FAILED");
            _platform.Delay(30);
            return ClipboardResult.Pasted;
        }
        catch (ExternalException)
        {
            // 错误码不包含异常消息，避免图片字节、文件名、路径或剪贴板内容进入日志。
            return ClipboardResult.Failed("CLIPBOARD_FAILED");
        }
        finally
        {
            if (payloadWasSet && sequenceCaptured && original is not null)
                TryRestoreOriginalClipboard(original, quickPhraseSequence);
        }
    }

    private void TryRestoreOriginalClipboard(IDataObject original, uint quickPhraseSequence)
    {
        try
        {
            // 只有剪贴板仍由本次 QuickPhrase Payload 占用时才恢复；第三方新复制的内容必须保留。
            if (_platform.GetSequenceNumber() == quickPhraseSequence)
                _platform.RestoreDataObject(original);
        }
        catch (ExternalException)
        {
            // 恢复失败不允许触发第二次粘贴，也不记录可能携带隐私的系统异常消息。
        }
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
            catch (OperationCanceledException exception) { _completion.TrySetCanceled(exception.CancellationToken); }
            catch (Exception exception) { _completion.TrySetException(exception); }
        }
    }
}

/// <summary>真实 Windows 剪贴板与键盘桥接；重试仅处理剪贴板被短暂占用，不改变投递次数。</summary>
internal sealed class WindowsClipboardPlatform : IClipboardPlatform
{
    public IDataObject? CaptureDataObject() => Clipboard.GetDataObject();

    public bool TrySetText(string text)
    {
        foreach (var delay in RetryDelays)
        {
            try { Clipboard.SetText(text, TextDataFormat.UnicodeText); return true; }
            catch (ExternalException) { Thread.Sleep(delay); }
        }
        return false;
    }

    public bool TrySetImage(System.Drawing.Image image)
    {
        foreach (var delay in RetryDelays)
        {
            try { Clipboard.SetImage(image); return true; }
            catch (ExternalException) { Thread.Sleep(delay); }
        }
        return false;
    }

    public uint GetSequenceNumber() => WindowsNativeMethods.GetClipboardSequenceNumber();
    public bool IsIdentityCurrent(WindowsTargetIdentity target) => WindowsTargetDetector.IsIdentityCurrent(target);
    public bool SetForegroundWindow(nint hwnd) => WindowsNativeMethods.SetForegroundWindow(hwnd);
    public nint GetForegroundWindow() => WindowsNativeMethods.GetForegroundWindow();
    public bool SendCtrlV() => WindowsNativeMethods.SendCtrlV();
    public void RestoreDataObject(IDataObject dataObject) => Clipboard.SetDataObject(dataObject, true);
    public void Delay(int milliseconds) => Thread.Sleep(milliseconds);

    private static readonly int[] RetryDelays = [20, 40, 80, 160, 320];
}
