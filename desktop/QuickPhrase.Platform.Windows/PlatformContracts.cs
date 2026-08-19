using Microsoft.Data.Sqlite;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>Windows 平台能力的内部占位契约，后续阶段实现，不向 Core 泄漏具体线程/剪贴板细节。</summary>
internal interface IUiAutomationWorker { }

/// <summary>剪贴板事务的最小结果，不携带任何剪贴板正文。</summary>
internal sealed record ClipboardResult(bool Succeeded, string Code)
{
    public static ClipboardResult Copied { get; } = new(true, "CLIPBOARD_COPIED");
    public static ClipboardResult Pasted { get; } = new(true, "CLIPBOARD_PASTED");
    public static ClipboardResult Failed(string code = "CLIPBOARD_FAILED") => new(false, code);
}

internal interface IClipboardTransaction
{
    Task<ClipboardResult> CopyOnlyAsync(string text, CancellationToken cancellationToken);
    Task<ClipboardResult> PasteAsync(string text, DeliveryTarget target, CancellationToken cancellationToken);
}
internal interface IDatabaseWriteQueue
{
    Task<T> EnqueueAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
}
internal interface IHotkeyService { }

public static class PlatformAssemblyMarker
{
    public const string TargetFramework = "net10.0-windows10.0.19041.0";
}
