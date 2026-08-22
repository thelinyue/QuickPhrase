using System.Collections.Immutable;
using System.Diagnostics;
using System.Windows.Automation;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 通用文本输入 Adapter。它不是“任意窗口强制粘贴”通道：只在独立 UIA MTA Worker 已确认
/// 当前焦点是目标进程中的可写标准文本控件后，才执行受保护剪贴板加 Ctrl+V。
/// 此 Adapter 永不声明快捷发送能力，避免把 Ctrl+Enter 降级为只插入或误发送。
/// </summary>
internal sealed class GenericTextInputAdapter : IApplicationAdapter
{
    private const string AdapterIdValue = "GenericTextInput";
    private readonly DeliveryTarget _target;
    private readonly ClipboardTransaction _clipboard;
    private readonly Func<DeliveryTarget, bool> _targetValidator;
    private readonly WindowsTargetContextStore _contexts;
    private readonly UiAutomationWorker _uiAutomation;
    private GenericTextInputFocusFingerprint? _insertFingerprint;

    public GenericTextInputAdapter(
        DeliveryTarget target,
        ClipboardTransaction clipboard,
        Func<DeliveryTarget, bool> targetValidator,
        WindowsTargetContextStore contexts,
        UiAutomationWorker uiAutomation)
    {
        _target = target;
        _clipboard = clipboard;
        _targetValidator = targetValidator;
        _contexts = contexts;
        _uiAutomation = uiAutomation;
    }

    public string AdapterId => AdapterIdValue;
    public string? DetectedProductVersion => null;
    public AdapterProfile Profile => new(
        AdapterIdValue,
        _target.ApplicationId,
        "generic-text-input-uia-1",
        CapabilityStatus.Verified,
        CapabilityStatus.Verified,
        CapabilityStatus.Unsupported,
        CapabilityStatus.Unsupported,
        CapabilityStatus.Unsupported,
        CapabilityStatus.Unsupported,
        "CopyOnly",
        null);

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
        var activated = WindowsTargetDetector.TryActivate(windowsTarget, TimeSpan.FromMilliseconds(500));
        stages.Add(new DeliverySubstage("target-activation", activated ? "ACTIVATED" : "TARGET_ACTIVATION_FAILED", Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        if (!activated)
            return new InsertResult(false, false, "TARGET_ACTIVATION_FAILED", stages.ToImmutable());

        cancellationToken.ThrowIfCancellationRequested();
        started = Stopwatch.GetTimestamp();
        _insertFingerprint = await GenericTextInputFocusPolicy.WaitForEditableFocusAsync(
            _uiAutomation,
            windowsTarget,
            TimeSpan.FromMilliseconds(500),
            cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliverySubstage(
            "control-fingerprint",
            _insertFingerprint.HasValue ? "FINGERPRINT_READY" : "GENERIC_TEXT_INPUT_UNAVAILABLE",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        if (!_insertFingerprint.HasValue)
            return new InsertResult(false, false, "GENERIC_TEXT_INPUT_UNAVAILABLE", stages.ToImmutable());

        // 焦点验证通过后只使用受保护剪贴板；不读取输入框正文、选区或剪贴板原内容。
        started = Stopwatch.GetTimestamp();
        var clipboard = await _clipboard.PasteAsync(request.Phrase.Body.TextProjection, request.Target, cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliverySubstage("clipboard-paste", clipboard.Code, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        return clipboard.Succeeded
            ? new InsertResult(true, false, "INSERTED", stages.ToImmutable())
            : new InsertResult(false, false, clipboard.Code, stages.ToImmutable());
    }

    public async Task<VerificationResult> VerifyInsertAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        if (request.Target is null || !_targetValidator(request.Target))
            return VerificationResult.Failed("TARGET_CHANGED");
        if (!_contexts.TryGet(request.Target.RuntimeKey, out var windowsTarget))
            return VerificationResult.Failed("TARGET_CONTEXT_MISSING");
        if (_insertFingerprint is not { } before)
            return VerificationResult.Failed("INSERT_FINGERPRINT_MISSING");

        // Ctrl+V 返回不代表目标已处理粘贴。稳定等待后只复核脱敏焦点指纹。
        await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken).ConfigureAwait(false);
        var after = await GenericTextInputFocusPolicy.WaitForEditableFocusAsync(
            _uiAutomation,
            windowsTarget,
            TimeSpan.FromMilliseconds(250),
            cancellationToken).ConfigureAwait(false);
        var stable = WindowsNativeMethods.GetForegroundWindow() == windowsTarget.Hwnd
            && after is { } value
            && GenericTextInputFocusPolicy.IsStableEditableTextInput(before, value);
        return stable
            ? VerificationResult.Verified
            : VerificationResult.Failed("GENERIC_TEXT_INPUT_FOCUS_CHANGED");
    }

    public Task<SendResult> SendAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new SendResult(false, Code: "CAPABILITY_UNSUPPORTED"));

    public Task<VerificationResult> VerifySendAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(VerificationResult.Failed("CAPABILITY_UNSUPPORTED"));
}

/// <summary>
/// 通用插入的脱敏焦点指纹。字段只描述控件身份与可编辑能力，禁止记录 Name、Value、Text、选区等内容。
/// </summary>
internal readonly record struct GenericTextInputFocusSnapshot(
    int ProcessId,
    nint NativeWindowHandle,
    string? AutomationId,
    string? ClassName,
    IReadOnlyList<int> RuntimeId,
    bool IsEnabled,
    bool IsKeyboardFocusable,
    bool IsPassword,
    bool IsEditableTextControl);

internal readonly record struct GenericTextInputFocusFingerprint(
    int ProcessId,
    nint NativeWindowHandle,
    string? AutomationId,
    string? ClassName,
    IReadOnlyList<int> RuntimeId);

/// <summary>
/// 通用文本控件的 UIA 准入与稳定性策略。所有 AutomationElement 访问都只发生在 UiAutomationWorker 内，
/// 本类对外仅返回脱敏快照，避免 UIA COM 对象跨线程或泄漏用户输入内容。
/// </summary>
internal static class GenericTextInputFocusPolicy
{
    public static bool IsEligibleEditableTextInput(WindowsTargetIdentity target, GenericTextInputFocusSnapshot snapshot) =>
        snapshot.ProcessId == target.ProcessId
        && snapshot.NativeWindowHandle != 0
        && snapshot.RuntimeId.Count > 0
        && snapshot.IsEnabled
        && snapshot.IsKeyboardFocusable
        && !snapshot.IsPassword
        && snapshot.IsEditableTextControl;

    public static bool IsStableEditableTextInput(
        GenericTextInputFocusFingerprint before,
        GenericTextInputFocusFingerprint after) =>
        before.ProcessId == after.ProcessId
        && before.NativeWindowHandle == after.NativeWindowHandle
        && string.Equals(before.AutomationId, after.AutomationId, StringComparison.Ordinal)
        && string.Equals(before.ClassName, after.ClassName, StringComparison.Ordinal)
        && before.RuntimeId.SequenceEqual(after.RuntimeId);

    public static async Task<GenericTextInputFocusFingerprint?> WaitForEditableFocusAsync(
        UiAutomationWorker worker,
        WindowsTargetIdentity target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GenericTextInputFocusFingerprint? fingerprint;
            try
            {
                fingerprint = await worker.InvokeAsync(
                    () => CaptureEditableFocus(target),
                    cancellationToken,
                    TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // UIA 未在限定时间内响应或探测异常时不能猜测焦点状态，交由调用方安全复制。
                return null;
            }

            if (fingerprint.HasValue)
                return fingerprint;

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static GenericTextInputFocusFingerprint? CaptureEditableFocus(WindowsTargetIdentity target)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null)
                return null;

            var current = element.Current;
            var editable = current.ControlType == ControlType.Edit
                && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                && pattern is ValuePattern valuePattern
                && !valuePattern.Current.IsReadOnly;
            var runtimeId = element.GetRuntimeId();
            var snapshot = new GenericTextInputFocusSnapshot(
                current.ProcessId,
                current.NativeWindowHandle,
                current.AutomationId,
                current.ClassName,
                runtimeId,
                current.IsEnabled,
                current.IsKeyboardFocusable,
                current.IsPassword,
                editable);
            return IsEligibleEditableTextInput(target, snapshot)
                ? new GenericTextInputFocusFingerprint(
                    snapshot.ProcessId,
                    snapshot.NativeWindowHandle,
                    snapshot.AutomationId,
                    snapshot.ClassName,
                    snapshot.RuntimeId)
                : null;
        }
        catch
        {
            // UIA 的瞬态 COM/元素异常一律视为无法验证；绝不因探测异常继续注入按键。
            return null;
        }
    }
}
