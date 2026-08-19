using System.Collections.Immutable;
using System.Diagnostics;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 仅解析当前阶段批准的企业微信 Adapter，其余目标统一交给安全 Copy Only。
/// Windows 目标上下文由本程序集管理，不把 HWND、PID 等类型传入 Core。
/// </summary>
public sealed class WindowsAdapterResolver : IAdapterResolver, IDisposable
{
    public const string SupportedWeComProductVersion = "5.0.9.6065";
    public static IReadOnlySet<string> KnownAdapterIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WXWork" };

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
        productVersion ??= _productVersionReader(target);
        if (string.Equals(target.ApplicationId, "WXWork", StringComparison.OrdinalIgnoreCase))
            return new WeComAdapter(target, productVersion, _clipboard, _targetValidator, _contexts);

        return new UnknownApplicationAdapter(target.ApplicationId);
    }

    public static bool IsKnownAdapterId(string? adapterId) =>
        !string.IsNullOrWhiteSpace(adapterId) && KnownAdapterIds.Contains(adapterId);

    /// <summary>返回设置页使用的脱敏能力快照；不会读取输入框或话术内容。</summary>
    public AdapterStatusSnapshot GetStatus(DeliveryTarget? target)
    {
        if (target is null)
            return new("Unknown", null, null, null, CapabilityStatus.Unverified, CapabilityStatus.Unverified, CapabilityStatus.Unsupported, CapabilityStatus.Unsupported, "CopyOnly");

        var adapter = Resolve(target);
        var profile = adapter.Profile;
        return new(adapter.AdapterId, target.ApplicationId, _productVersionReader(target), profile.ProfileVersion,
            profile.InsertTextStatus, profile.VerifyInsertStatus, profile.SendTextStatus, profile.VerifySendStatus, profile.FallbackMode);
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
    CapabilityStatus VerifyInsert,
    CapabilityStatus SendText,
    CapabilityStatus VerifySend,
    string FallbackMode);

internal sealed class UnknownApplicationAdapter(string applicationId) : IApplicationAdapter
{
    public string AdapterId => "Unknown";
    public string? DetectedProductVersion => null;
    public AdapterProfile Profile { get; } = new("Unknown", applicationId, "*", "unverified", CapabilityStatus.Unverified,
        CapabilityStatus.Unverified, CapabilityStatus.Unsupported, CapabilityStatus.Unsupported, "CopyOnly", null);
    public AdapterCapabilities DetectCapabilities() => new(CapabilityStatus.Unverified, CapabilityStatus.Unverified, CapabilityStatus.Unsupported, CapabilityStatus.Unsupported);
    public Task<InsertResult> InsertAsync(DeliveryRequest request, CancellationToken cancellationToken) => Task.FromResult(new InsertResult(false, false, "CAPABILITY_UNVERIFIED"));
    public Task<VerificationResult> VerifyInsertAsync(DeliveryRequest request, CancellationToken cancellationToken) => Task.FromResult(VerificationResult.Failed("CAPABILITY_UNVERIFIED"));
    public Task<SendResult> SendAsync(DeliveryRequest request, CancellationToken cancellationToken) => Task.FromResult(new SendResult(false, "CAPABILITY_UNSUPPORTED"));
    public Task<VerificationResult> VerifySendAsync(DeliveryRequest request, CancellationToken cancellationToken) => Task.FromResult(VerificationResult.Failed("CAPABILITY_UNSUPPORTED"));
}

/// <summary>企业微信单版本 Adapter。精确版本只开放已验收的 Clipboard 插入，验证和发送能力仍保持悲观。</summary>
internal sealed class WeComAdapter : IApplicationAdapter
{
    private readonly DeliveryTarget _target;
    private readonly string? _productVersion;
    private readonly ClipboardTransaction _clipboard;
    private readonly Func<DeliveryTarget, bool> _targetValidator;
    private readonly WindowsTargetContextStore _contexts;

    public WeComAdapter(DeliveryTarget target, string? productVersion, ClipboardTransaction clipboard,
        Func<DeliveryTarget, bool> targetValidator, WindowsTargetContextStore contexts)
    {
        _target = target;
        _productVersion = productVersion;
        _clipboard = clipboard;
        _targetValidator = targetValidator;
        _contexts = contexts;
    }

    public string AdapterId => "WXWork";
    public string? DetectedProductVersion => _productVersion;
    public AdapterProfile Profile => new(
        "WXWork", "WXWork", WindowsAdapterResolver.SupportedWeComProductVersion, "phase5-wecom-3",
        IsSupportedVersion ? CapabilityStatus.Verified : CapabilityStatus.Unverified,
        CapabilityStatus.Unverified,
        CapabilityStatus.Unsupported,
        CapabilityStatus.Unsupported,
        "CopyOnly", null);

    public AdapterCapabilities DetectCapabilities() => new(Profile.InsertTextStatus, Profile.VerifyInsertStatus,
        Profile.SendTextStatus, Profile.VerifySendStatus);

    public async Task<InsertResult> InsertAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        var stages = ImmutableArray.CreateBuilder<DeliverySubstage>();
        if (request.Target is null || !IsSupportedVersion)
            return new InsertResult(false, false, "CAPABILITY_UNVERIFIED", stages.ToImmutable());
        if (!_targetValidator(request.Target))
            return new InsertResult(false, false, "TARGET_CHANGED", stages.ToImmutable());
        if (!_contexts.TryGet(request.Target.RuntimeKey, out var windowsTarget))
            return new InsertResult(false, false, "TARGET_CONTEXT_MISSING", stages.ToImmutable());

        // Launcher 刚刚关闭时先激活捕获的原窗口，再用已验收的 Win32 caret 指纹判断聊天编辑区。
        var started = Stopwatch.GetTimestamp();
        var activated = ActivateTarget(windowsTarget);
        stages.Add(new DeliverySubstage("target-activation", activated ? "ACTIVATED" : "TARGET_ACTIVATION_FAILED", Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        if (!activated) return new InsertResult(false, false, "TARGET_ACTIVATION_FAILED", stages.ToImmutable());

        cancellationToken.ThrowIfCancellationRequested();
        started = Stopwatch.GetTimestamp();
        var fingerprintReady = await WeComFocusWaiter.WaitAsync(
            windowsTarget,
            static threadId => WindowsNativeMethods.TryCaptureFocusFingerprint(threadId, out var fingerprint) ? fingerprint : null,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(10),
            cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliverySubstage("control-fingerprint", fingerprintReady ? "FINGERPRINT_READY" : "TARGET_CONTROL_PROFILE_MISMATCH", Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        if (!fingerprintReady) return new InsertResult(false, false, "TARGET_CONTROL_PROFILE_MISMATCH", stages.ToImmutable());

        // 企业微信精确版本固定使用已人工验收的剪贴板事务；不受全局兼容模式开关影响。
        started = Stopwatch.GetTimestamp();
        var clipboard = await _clipboard.PasteAsync(request.Phrase.Content, request.Target, cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliverySubstage("clipboard-paste", clipboard.Code, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        return clipboard.Succeeded
            ? new InsertResult(true, false, "INSERTED", stages.ToImmutable())
            : new InsertResult(false, false, clipboard.Code, stages.ToImmutable());
    }

    public Task<VerificationResult> VerifyInsertAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(VerificationResult.Inconclusive("INSERT_VERIFICATION_INCONCLUSIVE"));

    public Task<SendResult> SendAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new SendResult(false, "CAPABILITY_UNSUPPORTED"));

    public Task<VerificationResult> VerifySendAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(VerificationResult.Failed("CAPABILITY_UNSUPPORTED"));

    private bool IsSupportedVersion => string.Equals(_productVersion, WindowsAdapterResolver.SupportedWeComProductVersion, StringComparison.OrdinalIgnoreCase);

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
