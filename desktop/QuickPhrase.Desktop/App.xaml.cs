using System.Windows;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuickPhrase.Platform.Windows;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop;

/// <summary>应用入口保持显式生命周期，托盘驻留时不依赖任何管理窗口。</summary>
public partial class App : System.Windows.Application
{
    private ApplicationController? _controller;
    private static volatile bool _crashReported;

    public App()
    {
        // 启动期未处理异常的兜底：写入 %TEMP%\QuickPhrase-crash-*.log 并弹窗提示，
        // 避免“双击无反应、无任何线索”的静默退出（例如 XAML 模板将 Color 误赋给 Brush 属性）。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportCrash(args.ExceptionObject as Exception ?? new InvalidOperationException("未知未处理异常"), "AppDomain.CurrentDomain.UnhandledException");
        DispatcherUnhandledException += (_, args) =>
        {
            ReportCrash(args.Exception, "DispatcherUnhandledException");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportCrash(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };

        // 触发 ThemeService 静态构造，确保 Light/Dark 资源在首次访问前可用。
        _ = ThemeService.Instance;
    }

    /// <summary>把崩溃信息写入临时日志并弹窗提示用户日志路径；已处理过则跳过重复弹窗。</summary>
    private static void ReportCrash(Exception exception, string source)
    {
        string? logPath = null;
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            logPath = Path.Combine(Path.GetTempPath(), $"QuickPhrase-crash-{stamp}.log");
            var sb = new StringBuilder();
            sb.AppendLine("QuickPhrase 启动崩溃报告");
            sb.AppendLine($"时间(本地): {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"异常来源: {source}");
            sb.AppendLine($"进程路径: {Environment.ProcessPath ?? "<未知>"}");
            sb.AppendLine($"命令行: {Environment.CommandLine}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"系统架构: {RuntimeInformation.OSArchitecture}  进程架构: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"CLR: {Environment.Version}  Runtime: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"是否自包含发布: {RuntimeInformation.FrameworkDescription.Contains("Framework")}");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine(exception.ToString());
            File.WriteAllText(logPath, sb.ToString());
        }
        catch
        {
            logPath = null;
        }

        if (_crashReported) return;
        _crashReported = true;
        try
        {
            var detail = exception is null ? "未知错误" : $"{exception.GetType().Name}：{exception.Message}";
            var location = logPath is null ? string.Empty : $"\n\n详细日志已保存到：\n{logPath}";
            System.Windows.MessageBox.Show($"闪语启动失败。\n错误：{detail}{location}",
                "闪语 - 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 无桌面环境时忽略（例如纯服务会话）。
        }

        try { Current?.Shutdown(1); } catch { }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 部分 Windows 显示驱动/RDP 会让首次 WPF 窗口出现“控件可访问但客户区全白”；
        // 先使用软件合成保证引导和故障面板可见，Native Launcher 仍独立运行。
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);
        // 主题资源需在 Application.Resources 已经构建后立即合并，否则后续 Converters/Controls
        // 字典中所有 {StaticResource AccentBrush} 等会引用旧值。Light 已通过 App.xaml 加载，
        // 这里根据用户偏好决定是否追加 Dark 覆盖字典。
        ThemeService.Instance.Initialize();
        StartupTrace.Mark("native-startup");
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _controller = new ApplicationController();
        var shutdownForUpgrade = e.Args.Contains("--shutdown-for-upgrade", StringComparer.OrdinalIgnoreCase);
        var backupForUpgrade = e.Args.Contains("--backup-for-upgrade", StringComparer.OrdinalIgnoreCase);
        if (!_controller.TryBecomePrimary())
        {
            var ok = await SingleInstanceCoordinator.ActivatePrimaryAsync(
                $"QuickPhrase.Activation.{System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "unknown"}",
                shutdownForUpgrade ? "shutdown-for-upgrade" : "show-management",
                new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
            if (!ok) System.Windows.MessageBox.Show("已有实例，但无法唤醒主实例。", "闪语", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        // 安装器升级前会启动一个带此参数的临时进程；若当前没有旧实例，
        // 该进程会成为主实例，也必须立即退出，不能初始化数据或打开管理界面。
        if (HandlePrimaryUpgradeShutdown(shutdownForUpgrade, Shutdown)) return;

        // 升级备份必须早于数据运行时初始化；新版本迁移校验不应阻断对旧数据库的快照保护。
        if (backupForUpgrade)
        {
            try
            {
                var backup = await _controller.CreateUpgradeBackupAsync("upgrade");
                Console.WriteLine($"UPGRADE_BACKUP_CREATED: {backup}");
                Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"UPGRADE_BACKUP_FAILED：升级前数据备份失败，未替换程序文件。{exception.Message}");
                Shutdown(1);
            }
            return;
        }

        // 先创建托盘图标，确保后台启动或数据初始化耗时期间也不会错过 NotifyIcon 初始化。
        try
        {
            _controller.StartTray();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"托盘模式启动失败：{exception.Message}");
            System.Windows.MessageBox.Show($"闪语托盘图标初始化失败。\n{exception.Message}", "闪语 - 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 1;
            Shutdown(1);
            return;
        }

        try
        {
            await _controller.InitializeDataAsync();
            StartupTrace.Mark("data-runtime-ready");
        }
        catch (DataStoreException exception)
        {
            Console.Error.WriteLine($"数据初始化失败（{exception.Code}）：{exception.Message}");
            System.Windows.MessageBox.Show($"闪语数据初始化失败。\n错误码：{exception.Code}\n{exception.Message}", "闪语", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.ExitCode = 1;
            Shutdown(1);
            return;
        }

        _controller.StartActivationServer();
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            if (_controller.ShouldShowOnboarding) _controller.OpenOnboarding();
            else if (!_controller.StartMinimized) _controller.OpenManagement();
        }
    }

    internal static bool HandlePrimaryUpgradeShutdown(bool shutdownForUpgrade, Action<int> shutdown)
    {
        if (!shutdownForUpgrade) return false;
        shutdown(0);
        return true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_controller is not null) await _controller.DisposeAsync();
        base.OnExit(e);
    }
}
