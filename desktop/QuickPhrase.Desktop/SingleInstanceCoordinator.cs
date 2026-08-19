using System.IO.Pipes;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace QuickPhrase.Desktop;

/// <summary>按当前 Windows 用户隔离的单实例协调器；次实例只发送唤醒命令后退出。</summary>
internal sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _stop;
    private Task? _server;
    private bool _ownsMutex;

    public SingleInstanceCoordinator()
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        _pipeName = $"QuickPhrase.Activation.{user}";
        _mutex = new Mutex(false, $"Global\\QuickPhrase.{user}");
    }

    public bool TryBecomePrimary() => _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);

    public void StartServer(Func<string, Task> onMessage)
    {
        _stop = new CancellationTokenSource();
        _server = Task.Run(() => RunServerAsync(onMessage, _stop.Token));
    }

    public static async Task<bool> ActivatePrimaryAsync(string pipeName, string message, CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(1000, cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(message);
            await client.WriteAsync(bytes, cancellationToken);
            await client.FlushAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunServerAsync(Func<string, Task> onMessage, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var message = await reader.ReadToEndAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(message)) await onMessage(message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"单实例激活管道异常：{ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop is not null)
        {
            await _stop.CancelAsync();
            if (_server is not null)
            {
                try { await _server; } catch (OperationCanceledException) { }
            }
            _stop.Dispose();
        }
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }
}
