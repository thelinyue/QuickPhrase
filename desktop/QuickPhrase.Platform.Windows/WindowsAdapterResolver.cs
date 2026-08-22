using System.Collections.Immutable;
using System.Diagnostics;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 优先解析企业微信专属 Adapter；其余目标进入通用文本输入 Adapter，
/// 仍由运行时目标与焦点验证决定是否允许插入。Windows 目标上下文由本程序集管理，
/// 不把 HWND、PID 等类型传入 Core。
/// </summary>
public sealed class WindowsAdapterResolver : IAdapterResolver, IAdapterBatchStabilityWaiter, IDisposable
{
    private readonly Func<DeliveryTarget, string?> _productVersionReader;
    private readonly Func<DeliveryTarget, bool> _targetValidator;
    private readonly WindowsTargetContextStore _contexts;
    private readonly UiAutomationWorker _uiAutomation;
    private readonly ClipboardTransaction _clipboard;

    public WindowsAdapterResolver(
        Func<DeliveryTarget, string?>? productVersionReader = null,
        Func<DeliveryTarget, bool>? targetValidator = null)
    {
        _contexts = WindowsTargetContextStore.Shared;
        _productVersionReader = productVersionReader ?? ReadProductVersion;
        _targetValidator = targetValidator ?? IsTargetCurrent;
        _uiAutomation = new UiAutomationWorker();
        _clipboard = new ClipboardTransaction();
    }

    public IApplicationAdapter Resolve(DeliveryTarget target, string? productVersion = null)
    {
        if (productVersion is null)
        {
            try { productVersion = _productVersionReader(target); }
            catch { productVersion = null; }
        }
        if (string.Equals(target.ApplicationId, "WXWork", StringComparison.OrdinalIgnoreCase))
            return new WeComAdapter(target, productVersion, _clipboard, _targetValidator, _contexts);

        return new GenericTextInputAdapter(target, _clipboard, _targetValidator, _contexts, _uiAutomation);
    }

    /// <summary>返回设置页使用的脱敏能力快照；不会读取输入框或话术内容。</summary>
    public AdapterStatusSnapshot GetStatus(DeliveryTarget? target)
    {
        if (target is null)
            return new(
                "Unknown", null, null, null,
                CapabilityStatus.Unverified,
                CapabilityStatus.Unverified,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                CapabilityStatus.Unsupported,
                "CopyOnly");

        var adapter = Resolve(target);
        var profile = adapter.Profile;
        return new(adapter.AdapterId, target.ApplicationId, _productVersionReader(target), profile.ProfileVersion,
            profile.InsertTextStatus,
            profile.VerifyTextInsertStatus,
            profile.InsertImageStatus,
            profile.VerifyImageInsertStatus,
            profile.TriggerSendStatus,
            profile.VerifySendStatus,
            profile.FallbackMode);
    }

    /// <summary>
    /// 段间等待只针对当前已声明 TriggerSend 的企业微信 Adapter。通过连续脱敏焦点/Caret 指纹确认输入区恢复稳定，
    /// 客户端版本仅用于诊断，绝不参与等待或图片能力准入。
    /// </summary>
    public async Task<VerificationResult> WaitForStabilityAsync(DeliveryTarget target, CancellationToken cancellationToken)
    {
        if (!_targetValidator(target))
            return VerificationResult.Failed("TARGET_CHANGED");
        if (!_contexts.TryGet(target.RuntimeKey, out var windowsTarget))
            return VerificationResult.Failed("TARGET_CONTEXT_MISSING");
        if (!string.Equals(target.ApplicationId, "WXWork", StringComparison.OrdinalIgnoreCase))
            return VerificationResult.Failed("BATCH_STABILITY_UNSUPPORTED");

        var timeout = TimeSpan.FromMilliseconds(800);
        var pollInterval = TimeSpan.FromMilliseconds(25);
        var started = Stopwatch.GetTimestamp();
        WeComFocusFingerprint? previous = null;
        var stableSamples = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_targetValidator(target) || WindowsNativeMethods.GetForegroundWindow() != windowsTarget.Hwnd)
                return VerificationResult.Failed("TARGET_CHANGED");

            if (WindowsNativeMethods.TryCaptureFocusFingerprint(windowsTarget.WindowThreadId, out var current)
                && WeComFocusPolicy.IsChatComposer(windowsTarget, current))
            {
                stableSamples = previous is { } before
                    && WeComFocusPolicy.IsStableChatComposer(windowsTarget, before, current)
                    ? stableSamples + 1
                    : 1;
                previous = current;
                if (stableSamples >= 2)
                    return VerificationResult.Verified;
            }
            else
            {
                previous = null;
                stableSamples = 0;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                return VerificationResult.Inconclusive("ADAPTER_STABILITY_TIMEOUT");
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _uiAutomation.Dispose();
        _clipboard.Dispose();
    }

    private bool IsTargetCurrent(DeliveryTarget target) =>
        _contexts.TryGet(target.RuntimeKey, out var identity) && WindowsTargetDetector.IsIdentityCurrent(identity);

    private string? ReadProductVersion(DeliveryTarget target)
    {
        try
        {
            if (!_contexts.TryGet(target.RuntimeKey, out var identity)) return null;
            using var process = Process.GetProcessById(identity.ProcessId);
            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) ? null : FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        catch { return null; }
    }
}

public sealed record AdapterStatusSnapshot(
    string AdapterId,
    string? ProcessName,
    string? ProductVersion,
    string? ProfileVersion,
    CapabilityStatus InsertText,
    CapabilityStatus VerifyTextInsert,
    CapabilityStatus InsertImage,
    CapabilityStatus VerifyImageInsert,
    CapabilityStatus TriggerSend,
    CapabilityStatus VerifySend,
    string FallbackMode);

/// <summary>
/// 企业微信运行时能力 Adapter。客户端版本只保留为诊断元数据；所有插入和发送准入均由
/// 当前窗口身份、前台状态以及输入区焦点/Caret 指纹在动作前后实时决定。
/// </summary>
internal sealed class WeComAdapter : IApplicationAdapter, IImageApplicationAdapter
{
    private readonly string? _productVersion;
    private readonly ClipboardTransaction _clipboard;
    private readonly Func<DeliveryTarget, bool> _targetValidator;
    private readonly WindowsTargetContextStore _contexts;
    private WeComFocusFingerprint? _insertFingerprint;

    public WeComAdapter(DeliveryTarget target, string? productVersion, ClipboardTransaction clipboard,
        Func<DeliveryTarget, bool> targetValidator, WindowsTargetContextStore contexts)
    {
        _productVersion = productVersion;
        _clipboard = clipboard;
        _targetValidator = targetValidator;
        _contexts = contexts;
    }

    public string AdapterId => "WXWork";
    public string? DetectedProductVersion => _productVersion;
    public AdapterProfile Profile => new(
        "WXWork", "WXWork", "phase5-wecom-runtime-1",
        CapabilityStatus.Verified,
        CapabilityStatus.Verified,
        CapabilityStatus.Unsupported,
        CapabilityStatus.Unsupported,
        CapabilityStatus.Verified,
        CapabilityStatus.Unsupported,
        "CopyOnly", null);

    public AdapterCapabilities DetectCapabilities() => new(
        Profile.InsertTextStatus,
        Profile.VerifyTextInsertStatus,
        Profile.InsertImageStatus,
        Profile.VerifyImageInsertStatus,
        Profile.TriggerSendStatus,
        Profile.VerifySendStatus);

    public async Task<InsertResult> InsertAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        var stages = ImmutableArray.CreateBuilder<DeliverySubstage>();
        if (request.Target is null)
            return new InsertResult(false, false, "TARGET_CONTEXT_MISSING", stages.ToImmutable());
        if (!_targetValidator(request.Target))
            return new InsertResult(false, false, "TARGET_CHANGED", stages.ToImmutable());
        if (!_contexts.TryGet(request.Target.RuntimeKey, out var windowsTarget))
            return new InsertResult(false, false, "TARGET_CONTEXT_MISSING", stages.ToImmutable());

        var started = Stopwatch.GetTimestamp();
        var activated = ActivateTarget(windowsTarget);
        stages.Add(new DeliverySubstage("target-activation", activated ? "ACTIVATED" : "TARGET_ACTIVATION_FAILED", Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        if (!activated) return new InsertResult(false, false, "TARGET_ACTIVATION_FAILED", stages.ToImmutable());

        cancellationToken.ThrowIfCancellationRequested();
        started = Stopwatch.GetTimestamp();
        _insertFingerprint = await WaitForComposerFingerprintAsync(windowsTarget, TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliverySubstage("control-fingerprint", _insertFingerprint.HasValue ? "FINGERPRINT_READY" : "TARGET_CONTROL_PROFILE_MISMATCH", Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        if (!_insertFingerprint.HasValue) return new InsertResult(false, false, "TARGET_CONTROL_PROFILE_MISMATCH", stages.ToImmutable());

        // 运行时检查通过后固定使用受保护剪贴板事务；不读取或比对目标输入框正文。
        started = Stopwatch.GetTimestamp();
        var clipboard = await _clipboard.PasteAsync(request.Phrase.Body.TextProjection, request.Target, cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliverySubstage("clipboard-paste", clipboard.Code, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        return clipboard.Succeeded
            ? new InsertResult(true, false, "INSERTED", stages.ToImmutable())
            : new InsertResult(false, false, clipboard.Code, stages.ToImmutable());
    }

    /// <summary>
    /// 企业微信图片执行链已具备，但能力快照在 Windows 11 人工矩阵通过前始终保持 Unsupported，
    /// 因此生产状态机不会调用此方法。未来启用时仍复用同一目标、焦点和剪贴板安全边界。
    /// </summary>
    public async Task<InsertResult> InsertImageAsync(DeliveryRequest request, MediaAssetContent image, CancellationToken cancellationToken)
    {
        var stages = ImmutableArray.CreateBuilder<DeliverySubstage>();
        if (request.Target is null)
            return new InsertResult(false, false, "TARGET_CONTEXT_MISSING", stages.ToImmutable());
        if (!_targetValidator(request.Target))
            return new InsertResult(false, false, "TARGET_CHANGED", stages.ToImmutable());
        if (!_contexts.TryGet(request.Target.RuntimeKey, out var windowsTarget))
            return new InsertResult(false, false, "TARGET_CONTEXT_MISSING", stages.ToImmutable());

        var started = Stopwatch.GetTimestamp();
        var activated = ActivateTarget(windowsTarget);
        stages.Add(new DeliverySubstage("target-activation", activated ? "ACTIVATED" : "TARGET_ACTIVATION_FAILED", Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        if (!activated) return new InsertResult(false, false, "TARGET_ACTIVATION_FAILED", stages.ToImmutable());

        cancellationToken.ThrowIfCancellationRequested();
        started = Stopwatch.GetTimestamp();
        _insertFingerprint = await WaitForComposerFingerprintAsync(windowsTarget, TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliverySubstage("control-fingerprint", _insertFingerprint.HasValue ? "FINGERPRINT_READY" : "TARGET_CONTROL_PROFILE_MISMATCH", Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        if (!_insertFingerprint.HasValue) return new InsertResult(false, false, "TARGET_CONTROL_PROFILE_MISMATCH", stages.ToImmutable());

        started = Stopwatch.GetTimestamp();
        var clipboard = await _clipboard.PasteImageAsync(image.Bytes, request.Target, cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliverySubstage("clipboard-paste", clipboard.Code, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        return clipboard.Succeeded
            ? new InsertResult(true, false, "IMAGE_INSERTED", stages.ToImmutable())
            : new InsertResult(false, false, clipboard.Code, stages.ToImmutable());
    }

    public Task<VerificationResult> VerifyImageInsertAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
        VerifyInsertAsync(request, cancellationToken);

    public async Task<VerificationResult> VerifyInsertAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        if (request.Target is null || !_targetValidator(request.Target))
            return VerificationResult.Failed("TARGET_CHANGED");
        if (!_contexts.TryGet(request.Target.RuntimeKey, out var windowsTarget))
            return VerificationResult.Failed("TARGET_CONTEXT_MISSING");

        if (_insertFingerprint is not { } before)
            return VerificationResult.Failed("INSERT_FINGERPRINT_MISSING");

        // Ctrl+V 返回不代表企业微信已经处理完粘贴消息；稳定等待后必须重新采集脱敏焦点指纹。
        await WeComFocusPolicy.WaitForPostPasteStabilityAsync(cancellationToken).ConfigureAwait(false);
        var after = await WaitForComposerFingerprintAsync(windowsTarget, TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        var stable = WindowsNativeMethods.GetForegroundWindow() == windowsTarget.Hwnd
            && after is { } value
            && WeComFocusPolicy.IsStableChatComposer(windowsTarget, before, value);
        return stable
            ? VerificationResult.Verified
            : VerificationResult.Failed("TARGET_CONTROL_PROFILE_MISMATCH");
    }

    public async Task<SendResult> SendAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Target is null || !_targetValidator(request.Target))
            return new SendResult(false, Code: "TARGET_CHANGED");
        if (!_contexts.TryGet(request.Target.RuntimeKey, out var windowsTarget))
            return new SendResult(false, Code: "TARGET_CONTEXT_MISSING");
        if (_insertFingerprint is not { } before)
            return new SendResult(false, Code: "INSERT_FINGERPRINT_MISSING");

        var current = await WaitForComposerFingerprintAsync(windowsTarget, TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        if (WindowsNativeMethods.GetForegroundWindow() != windowsTarget.Hwnd
            || current is not { } value
            || !WeComFocusPolicy.IsStableChatComposer(windowsTarget, before, value))
            return new SendResult(false, Code: "TARGET_CONTROL_PROFILE_MISMATCH");

        return WindowsNativeMethods.SendEnter() switch
        {
            KeyboardInjectionResult.Applied => SendResult.Applied,
            KeyboardInjectionResult.Inconclusive => SendResult.Unknown("SEND_INPUT_INCONCLUSIVE"),
            _ => new SendResult(false, Code: "SEND_INPUT_FAILED"),
        };
    }

    public Task<VerificationResult> VerifySendAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(VerificationResult.Inconclusive("SEND_RESULT_UNVERIFIED"));

    private static async Task<WeComFocusFingerprint?> WaitForComposerFingerprintAsync(
        WindowsTargetIdentity target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WindowsNativeMethods.TryCaptureFocusFingerprint(target.WindowThreadId, out var fingerprint)
                && WeComFocusPolicy.IsChatComposer(target, fingerprint))
                return fingerprint;

            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return null;
            var pollInterval = TimeSpan.FromMilliseconds(10);
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ActivateTarget(WindowsTargetIdentity target)
    {
        if (!WindowsNativeMethods.IsWindow(target.Hwnd) || !WindowsNativeMethods.SetForegroundWindow(target.Hwnd)) return false;
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (DateTime.UtcNow < deadline)
        {
            if (WindowsNativeMethods.GetForegroundWindow() == target.Hwnd) return true;
            Thread.Sleep(15);
        }
        return false;
    }
}
