using System.Text.Json;
using QuickPhrase.Core;

namespace QuickPhrase.Platform.Windows;

/// <summary>
/// 投递诊断的最小脱敏落盘器。只记录阶段、能力版本和耗时，不记录话术、剪贴板或窗口内容。
/// 清理只发生在启动和第一次真实投递时，不使用定时器制造空闲磁盘写入。
/// </summary>
public sealed class DeliveryTraceWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly object _gate = new();
    private bool _cleaned;
    private bool _disposed;

    public DeliveryTraceWriter(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickPhrase", "Logs");
        CleanupExpired();
    }

    public void Write(DeliveryTrace trace)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_cleaned) CleanupExpiredCore();
            Directory.CreateDirectory(_directory);
            var file = Path.Combine(_directory, $"delivery-{trace.TimestampUtc:yyyy-MM-dd}.jsonl");
            var json = JsonSerializer.Serialize(trace, JsonOptions);
            File.AppendAllText(file, json + Environment.NewLine);
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }

    private void CleanupExpired()
    {
        lock (_gate) CleanupExpiredCore();
    }

    private void CleanupExpiredCore()
    {
        if (_cleaned) return;
        _cleaned = true;
        if (!Directory.Exists(_directory)) return;
        var cutoff = DateTime.UtcNow.Date.AddDays(-7);
        string[] files;
        try { files = Directory.GetFiles(_directory, "delivery-*.jsonl"); }
        catch { return; }
        foreach (var file in files)
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                try { File.Delete(file); } catch { /* 清理失败不影响投递。 */ }
            }
        }
    }
}
