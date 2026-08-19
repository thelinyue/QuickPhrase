using System.Collections.Concurrent;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// Windows 运行时目标上下文。HWND、PID、线程 ID 和进程启动时间只在平台层短期保存，
/// 通过 Core 可见的 RuntimeKey 与逻辑 DeliveryTarget 关联，避免 Windows 类型泄漏到 Core。
/// </summary>
internal sealed record WindowsTargetIdentity(
    nint Hwnd,
    int ProcessId,
    int WindowThreadId,
    DateTimeOffset ProcessStartTimeUtc,
    string ProcessName,
    DateTimeOffset CapturedAtUtc);

/// <summary>UIA/Win32 焦点指纹只在 Windows 平台层使用，不进入领域契约。</summary>
internal sealed record WindowsFocusElementIdentity(
    nint? NativeWindowHandle = null,
    string? AutomationId = null,
    string? ClassName = null,
    IReadOnlyList<int>? RuntimeId = null);

/// <summary>
/// 保存一次 Launcher 会话的短期 Windows 目标。容量受限，避免长时间运行积累失效句柄。
/// </summary>
internal sealed class WindowsTargetContextStore
{
    public static WindowsTargetContextStore Shared { get; } = new();

    private readonly ConcurrentDictionary<string, (WindowsTargetIdentity Identity, DateTimeOffset LastAccessUtc)> _items = new(StringComparer.Ordinal);

    public string Register(WindowsTargetIdentity identity)
    {
        var key = Guid.NewGuid().ToString("N");
        _items[key] = (identity, DateTimeOffset.UtcNow);
        Trim();
        return key;
    }

    public bool TryGet(string key, out WindowsTargetIdentity identity)
    {
        if (_items.TryGetValue(key, out var item))
        {
            _items[key] = (item.Identity, DateTimeOffset.UtcNow);
            identity = item.Identity;
            return true;
        }

        identity = default!;
        return false;
    }

    public void Remove(string key) => _items.TryRemove(key, out _);

    private void Trim()
    {
        if (_items.Count <= 256) return;
        foreach (var item in _items.OrderBy(pair => pair.Value.LastAccessUtc).Take(_items.Count - 256))
            _items.TryRemove(item.Key, out _);
    }
}
