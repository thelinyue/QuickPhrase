using System.Runtime.InteropServices;
using System.Windows.Forms;
using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

/// <summary>图片和文字共用同一剪贴板安全事务；测试仅使用内存 seam，不访问用户真实剪贴板或窗口。</summary>
public sealed class ClipboardTransactionTests
{
    [Fact]
    public async Task ImagePasteRunsOnStaSendsCtrlVOnceAndRestoresOriginalClipboard()
    {
        var setup = CreateSetup();
        using var transaction = new ClipboardTransaction(setup.Platform, setup.Contexts);

        var result = await transaction.PasteImageAsync(OnePixelPng, setup.Target, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(ApartmentState.STA, setup.Platform.PayloadApartmentState);
        Assert.Equal(1, setup.Platform.SendCtrlVCalls);
        Assert.Equal(1, setup.Platform.RestoreCalls);
    }

    [Theory]
    [InlineData(ClipboardFailure.TargetValidation)]
    [InlineData(ClipboardFailure.ForegroundActivation)]
    [InlineData(ClipboardFailure.ForegroundChanged)]
    [InlineData(ClipboardFailure.CtrlV)]
    [InlineData(ClipboardFailure.ExternalException)]
    public async Task ImagePasteRestoresOriginalClipboardOnEveryFailureAfterPayloadWasSet(ClipboardFailure failure)
    {
        var setup = CreateSetup();
        setup.Platform.Failure = failure;
        using var transaction = new ClipboardTransaction(setup.Platform, setup.Contexts);

        var result = await transaction.PasteImageAsync(OnePixelPng, setup.Target, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, setup.Platform.RestoreCalls);
        Assert.InRange(setup.Platform.SendCtrlVCalls, 0, 1);
    }

    [Fact]
    public async Task ImagePasteRestoresOriginalClipboardWhenCancelledAfterPayloadWasSet()
    {
        var setup = CreateSetup();
        using var cancellation = new CancellationTokenSource();
        setup.Platform.AfterPayloadSet = cancellation.Cancel;
        using var transaction = new ClipboardTransaction(setup.Platform, setup.Contexts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transaction.PasteImageAsync(OnePixelPng, setup.Target, cancellation.Token));

        Assert.Equal(1, setup.Platform.RestoreCalls);
        Assert.Equal(0, setup.Platform.SendCtrlVCalls);
    }

    [Fact]
    public async Task ImagePasteDoesNotOverwriteClipboardChangedByThirdParty()
    {
        var setup = CreateSetup();
        setup.Platform.ChangeSequenceAfterCtrlV = true;
        using var transaction = new ClipboardTransaction(setup.Platform, setup.Contexts);

        var result = await transaction.PasteImageAsync(OnePixelPng, setup.Target, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, setup.Platform.SendCtrlVCalls);
        Assert.Equal(0, setup.Platform.RestoreCalls);
    }

    [Fact]
    public async Task TextAndImageUseTheSameFailureSafeFinally()
    {
        var setup = CreateSetup();
        setup.Platform.Failure = ClipboardFailure.CtrlV;
        using var transaction = new ClipboardTransaction(setup.Platform, setup.Contexts);

        var textResult = await transaction.PasteAsync("隐私正文", setup.Target, CancellationToken.None);
        setup.Platform.ResetForNextPayload();
        var imageResult = await transaction.PasteImageAsync(OnePixelPng, setup.Target, CancellationToken.None);

        Assert.False(textResult.Succeeded);
        Assert.False(imageResult.Succeeded);
        Assert.Equal(2, setup.Platform.TotalRestoreCalls);
    }

    [Fact]
    public async Task ClipboardFailureLogsNeverContainImageBytesPathsOrFileNames()
    {
        var setup = CreateSetup();
        setup.Platform.Failure = ClipboardFailure.ExternalException;
        setup.Platform.ExceptionMessage = "sensitive-image-name.png C:\\private\\customer\\image.png iVBORw0KGgo";
        using var transaction = new ClipboardTransaction(setup.Platform, setup.Contexts);
        var originalError = Console.Error;
        using var writer = new StringWriter();
        Console.SetError(writer);
        try
        {
            var result = await transaction.PasteImageAsync(OnePixelPng, setup.Target, CancellationToken.None);
            Assert.False(result.Succeeded);
        }
        finally
        {
            Console.SetError(originalError);
        }

        var log = writer.ToString();
        Assert.DoesNotContain("sensitive-image-name.png", log, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\private\\customer", log, StringComparison.Ordinal);
        Assert.DoesNotContain("iVBORw0KGgo", log, StringComparison.Ordinal);
    }

    private static ClipboardSetup CreateSetup()
    {
        var contexts = new WindowsTargetContextStore();
        var identity = new WindowsTargetIdentity((nint)123, 456, 789, DateTimeOffset.UtcNow, "test", DateTimeOffset.UtcNow);
        var key = contexts.Register(identity);
        var target = new DeliveryTarget("WXWork", "WindowsDesktopWindow", "WXWork", "企业微信", key, DateTimeOffset.UtcNow);
        return new ClipboardSetup(target, contexts, new FakeClipboardPlatform(identity));
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zp1sAAAAASUVORK5CYII=");

    private sealed record ClipboardSetup(
        DeliveryTarget Target,
        WindowsTargetContextStore Contexts,
        FakeClipboardPlatform Platform);

    public enum ClipboardFailure
    {
        None,
        TargetValidation,
        ForegroundActivation,
        ForegroundChanged,
        CtrlV,
        ExternalException,
    }

    private sealed class FakeClipboardPlatform(WindowsTargetIdentity identity) : IClipboardPlatform
    {
        private readonly IDataObject _original = new DataObject();
        private uint _sequence = 10;
        private int _restoreCalls;
        public ClipboardFailure Failure { get; set; }
        public string ExceptionMessage { get; set; } = "测试剪贴板异常";
        public bool ChangeSequenceAfterCtrlV { get; set; }
        public Action? AfterPayloadSet { get; set; }
        public ApartmentState PayloadApartmentState { get; private set; }
        public int SendCtrlVCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public int TotalRestoreCalls => _restoreCalls;

        public IDataObject? CaptureDataObject() => _original;
        public bool TrySetText(string text) => SetPayload();
        public bool TrySetImage(System.Drawing.Image image) => SetPayload();
        public uint GetSequenceNumber() => _sequence;
        public bool IsIdentityCurrent(WindowsTargetIdentity target) => Failure != ClipboardFailure.TargetValidation && target == identity;
        public bool SetForegroundWindow(nint hwnd) => Failure != ClipboardFailure.ForegroundActivation && hwnd == identity.Hwnd;
        public nint GetForegroundWindow() => Failure == ClipboardFailure.ForegroundChanged ? (nint)999 : identity.Hwnd;
        public bool SendCtrlV()
        {
            SendCtrlVCalls++;
            if (Failure == ClipboardFailure.ExternalException) throw new ExternalException(ExceptionMessage);
            if (ChangeSequenceAfterCtrlV) _sequence++;
            return Failure != ClipboardFailure.CtrlV;
        }
        public void RestoreDataObject(IDataObject dataObject)
        {
            RestoreCalls++;
            _restoreCalls++;
        }
        public void Delay(int milliseconds) { }

        public void ResetForNextPayload()
        {
            RestoreCalls = 0;
            SendCtrlVCalls = 0;
            _sequence++;
        }

        private bool SetPayload()
        {
            PayloadApartmentState = Thread.CurrentThread.GetApartmentState();
            _sequence++;
            AfterPayloadSet?.Invoke();
            return true;
        }
    }
}
