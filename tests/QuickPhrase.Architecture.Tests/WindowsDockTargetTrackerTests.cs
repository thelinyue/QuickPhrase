using QuickPhrase.Core;
using QuickPhrase.Platform.Windows;

namespace QuickPhrase.Architecture.Tests;

public sealed class WindowsDockTargetTrackerTests
{
    [Fact]
    public void PixelBounds_AreConvertedToDipsUsingTargetDpi()
    {
        var result = WindowsDockGeometry.ToDips(new WindowsPixelBounds(150, 75, 1650, 975), 144);

        Assert.Equal(new DesktopWindowBounds(100, 50, 1000, 600), result);
    }

    [Fact]
    public void NativeSource_RefreshesCurrentExternalWindowAndConvertsGeometry()
    {
        var native = new FakeDockNativeApi
        {
            ForegroundWindow = (nint)42,
            ForegroundProcessId = 900,
            Dpi = 144,
            WindowBounds = new WindowsPixelBounds(150, 75, 1650, 975),
            WorkArea = new WindowsPixelBounds(0, 0, 2880, 1560),
        };
        using var source = new WindowsWinEventDockTargetSource(native);
        (bool IsCurrentProcess, WindowsDockTargetSnapshot? Snapshot)? observed = null;
        source.ForegroundChanged += (isCurrent, _, snapshot) => observed = (isCurrent, snapshot);

        source.Refresh();

        Assert.False(observed?.IsCurrentProcess);
        Assert.Equal(new DesktopWindowBounds(100, 50, 1000, 600), observed?.Snapshot?.TargetBounds);
        Assert.Equal(new DesktopWindowBounds(0, 0, 1920, 1040), observed?.Snapshot?.WorkArea);
    }

    [Fact]
    public void NativeSource_ExternalForegroundWithoutGeometryPublishesUnavailable()
    {
        var native = FakeDockNativeApi.CreateAvailableExternal();
        native.WindowRectAvailable = false;
        using var source = new WindowsWinEventDockTargetSource(native);
        (bool IsCurrentProcess, WindowsDockTargetSnapshot? Snapshot)? observed = null;
        source.ForegroundChanged += (isCurrent, _, snapshot) => observed = (isCurrent, snapshot);

        source.Refresh();

        Assert.False(observed?.IsCurrentProcess);
        Assert.Null(observed?.Snapshot);
    }
    [Fact]
    public void NativeSource_IgnoresOwnForegroundWithoutReplacingTrackedExternalWindow()
    {
        var native = FakeDockNativeApi.CreateAvailableExternal();
        using var source = new WindowsWinEventDockTargetSource(native);
        var foregroundEvents = new List<bool>();
        source.ForegroundChanged += (isCurrent, _, _) => foregroundEvents.Add(isCurrent);
        source.Refresh();

        native.ForegroundWindow = (nint)77;
        native.ForegroundProcessId = (uint)native.CurrentProcessId;
        native.Raise(WindowsDockEvents.Foreground, native.ForegroundWindow, WindowsDockEvents.WindowObjectId);
        native.Raise(WindowsDockEvents.LocationChange, (nint)42, WindowsDockEvents.WindowObjectId);

        Assert.Equal([false, true], foregroundEvents);
        Assert.Equal((nint)42, source.TrackedWindow);
    }

    [Fact]
    public void NativeSource_DestroyedTrackedWindowPublishesUnavailable()
    {
        var native = FakeDockNativeApi.CreateAvailableExternal();
        using var source = new WindowsWinEventDockTargetSource(native);
        WindowsDockTargetSnapshot? observed = CreateSnapshot(CreateTarget("placeholder"));
        source.LocationChanged += snapshot => observed = snapshot;
        source.Refresh();

        native.Raise(WindowsDockEvents.Destroy, (nint)42, WindowsDockEvents.WindowObjectId);

        Assert.Null(observed);
        Assert.Equal((nint)0, source.TrackedWindow);
    }

    [Fact]
    public void NativeSource_MinimizedTrackedWindowPublishesUnavailableAndDisposeUnhooks()
    {
        var native = FakeDockNativeApi.CreateAvailableExternal();
        var source = new WindowsWinEventDockTargetSource(native);
        WindowsDockTargetSnapshot? observed = CreateSnapshot(CreateTarget("placeholder"));
        source.LocationChanged += snapshot => observed = snapshot;
        source.Refresh();
        native.Minimized = true;

        native.Raise(WindowsDockEvents.LocationChange, (nint)42, WindowsDockEvents.WindowObjectId);
        source.Dispose();

        Assert.Null(observed);
        Assert.Equal(native.InstalledHooks, native.UnhookedHooks);
    }

    [Fact]
    public void ForegroundChange_CapturesTheEventWindowInsteadOfReadingForegroundAgain()
    {
        var target = CreateTarget("runtime-event");
        var source = new FakeDockTargetSource();
        nint capturedWindow = 0;
        using var tracker = new WindowsDockTargetTracker(source, hwnd =>
        {
            capturedWindow = hwnd;
            return target;
        });

        source.RaiseForeground(false, CreateSnapshot(target));

        Assert.Equal((nint)42, capturedWindow);
        Assert.Equal(target, tracker.Current?.Target);
    }

    [Fact]
    public void ForegroundChange_WhenNewExternalTargetCannotBeCapturedMarksPreviousTargetUnavailable()
    {
        var previous = CreateTarget("runtime-previous");
        var source = new FakeDockTargetSource();
        var captureCount = 0;
        using var tracker = new WindowsDockTargetTracker(source, _ => ++captureCount == 1 ? previous : null);
        source.RaiseForeground(false, CreateSnapshot(previous));

        source.RaiseForeground(false, CreateSnapshot(CreateTarget("runtime-unknown")));

        Assert.Equal(previous, tracker.Current?.Target);
        Assert.False(tracker.Current?.IsAvailable);
    }
    [Fact]
    public void ForegroundChange_ToExternalWindow_PublishesCapturedTarget()
    {
        var target = CreateTarget("runtime-1");
        var source = new FakeDockTargetSource();
        using var tracker = new WindowsDockTargetTracker(source, _ => target);
        WindowsDockTargetSnapshot? observed = null;
        tracker.Changed += snapshot => observed = snapshot;

        source.RaiseForeground(isCurrentProcessWindow: false, CreateSnapshot(target));

        Assert.Equal(target, observed?.Target);
        Assert.True(observed?.IsAvailable);
    }

    [Fact]
    public void ForegroundChange_ToQuickPhraseWindow_KeepsLastExternalTarget()
    {
        var target = CreateTarget("runtime-1");
        var source = new FakeDockTargetSource();
        using var tracker = new WindowsDockTargetTracker(source, _ => target);
        var observed = new List<WindowsDockTargetSnapshot>();
        tracker.Changed += observed.Add;
        source.RaiseForeground(false, CreateSnapshot(target));

        source.RaiseForeground(true, null);

        Assert.Single(observed);
        Assert.Equal(target, tracker.Current?.Target);
    }

    [Fact]
    public void LocationChange_ForCurrentTarget_PublishesUpdatedBounds()
    {
        var target = CreateTarget("runtime-1");
        var source = new FakeDockTargetSource();
        using var tracker = new WindowsDockTargetTracker(source, _ => target);
        source.RaiseForeground(false, CreateSnapshot(target));
        WindowsDockTargetSnapshot? observed = null;
        tracker.Changed += snapshot => observed = snapshot;
        var moved = CreateSnapshot(target) with { TargetBounds = new DesktopWindowBounds(300, 100, 900, 700) };

        source.RaiseLocation(moved);

        Assert.Equal(moved.TargetBounds, observed?.TargetBounds);
    }

    [Fact]
    public void TargetUnavailable_KeepsSnapshotAndMarksItUnavailable()
    {
        var target = CreateTarget("runtime-1");
        var source = new FakeDockTargetSource();
        using var tracker = new WindowsDockTargetTracker(source, _ => target);
        source.RaiseForeground(false, CreateSnapshot(target));

        source.RaiseLocation(null);

        Assert.Equal(target, tracker.Current?.Target);
        Assert.False(tracker.Current?.IsAvailable);
    }

    [Fact]
    public void Dispose_UnsubscribesAndDisposesSource()
    {
        var source = new FakeDockTargetSource();
        var tracker = new WindowsDockTargetTracker(source, _ => CreateTarget("runtime"));

        tracker.Dispose();
        source.RaiseForeground(false, CreateSnapshot(CreateTarget("other")));

        Assert.True(source.Disposed);
        Assert.Null(tracker.Current);
    }

    private static DeliveryTarget CreateTarget(string runtimeKey) =>
        new("wecom", "WindowsDesktopWindow", "wecom", "企业微信", runtimeKey, DateTimeOffset.UtcNow);

    private static WindowsDockTargetSnapshot CreateSnapshot(DeliveryTarget target) =>
        new(target, new DesktopWindowBounds(100, 100, 900, 700), new DesktopWindowBounds(0, 0, 1920, 1040), true);

    private sealed class FakeDockTargetSource : IWindowsDockTargetSource
    {
        public event Action<bool, nint, WindowsDockTargetSnapshot?>? ForegroundChanged;
        public event Action<WindowsDockTargetSnapshot?>? LocationChanged;
        public bool Disposed { get; private set; }
        public void Refresh() { }
        public void RaiseForeground(bool isCurrentProcessWindow, WindowsDockTargetSnapshot? snapshot) =>
            ForegroundChanged?.Invoke(isCurrentProcessWindow, (nint)42, snapshot);
        public void RaiseLocation(WindowsDockTargetSnapshot? snapshot) => LocationChanged?.Invoke(snapshot);
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeDockNativeApi : IWindowsDockNativeApi
    {
        private Action<uint, nint, int>? _callback;

        public int CurrentProcessId { get; init; } = 100;
        public nint ForegroundWindow { get; set; }
        public uint ForegroundProcessId { get; set; }
        public uint Dpi { get; set; } = 96;
        public bool Minimized { get; set; }
        public bool WindowRectAvailable { get; set; } = true;
        public WindowsPixelBounds WindowBounds { get; set; }
        public WindowsPixelBounds WorkArea { get; set; }
        public List<nint> InstalledHooks { get; } = [(nint)1, (nint)2];
        public List<nint> UnhookedHooks { get; } = [];

        public static FakeDockNativeApi CreateAvailableExternal() => new()
        {
            ForegroundWindow = (nint)42,
            ForegroundProcessId = 900,
            WindowBounds = new WindowsPixelBounds(100, 100, 1000, 800),
            WorkArea = new WindowsPixelBounds(0, 0, 1920, 1040),
        };

        public nint GetForegroundWindow() => ForegroundWindow;
        public uint GetWindowProcessId(nint hwnd) => hwnd == ForegroundWindow || hwnd == (nint)42 ? ForegroundProcessId : 0;
        public bool IsWindow(nint hwnd) => hwnd != 0;
        public bool IsWindowVisible(nint hwnd) => true;
        public bool IsIconic(nint hwnd) => Minimized;
        public bool TryGetWindowRect(nint hwnd, out WindowsPixelBounds bounds) { bounds = WindowBounds; return WindowRectAvailable; }
        public uint GetDpiForWindow(nint hwnd) => Dpi;
        public bool TryGetWorkArea(nint hwnd, out WindowsPixelBounds bounds) { bounds = WorkArea; return true; }
        public IReadOnlyList<nint> InstallWinEventHooks(Action<uint, nint, int> callback) { _callback = callback; return InstalledHooks; }
        public void UnhookWinEvent(nint hook) => UnhookedHooks.Add(hook);
        public void Raise(uint eventType, nint hwnd, int objectId) => _callback?.Invoke(eventType, hwnd, objectId);
    }
}

