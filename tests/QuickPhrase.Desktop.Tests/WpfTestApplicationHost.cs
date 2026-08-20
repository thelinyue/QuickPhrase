using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 为整个 Desktop 测试程序集提供唯一的 WPF Application 与 STA Dispatcher。
/// WPF 的 Application、DynamicResource、Style 和 Freezable 都具有线程归属；
/// 所有运行时控件测试必须投递到该宿主，避免并行测试在多个 STA 线程间交叉访问资源。
/// </summary>
internal static class WpfTestApplicationHost
{
    private static readonly Lazy<HostState> State =
        new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Invoke(Action<Application> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Invoke(application =>
        {
            action(application);
            return true;
        });
    }

    public static T Invoke<T>(Func<Application, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var state = State.Value;

        return state.Dispatcher.CheckAccess()
            ? action(state.Application)
            : state.Dispatcher.Invoke(() => action(state.Application));
    }

    private static HostState Create()
    {
        var ready = new TaskCompletionSource<HostState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                };

                foreach (var relativePath in new[]
                {
                    "Themes/Converters.xaml",
                    "Themes/QuickPhraseTheme.xaml",
                    "Themes/Controls.xaml",
                })
                {
                    var uri = new Uri($"/QuickPhrase;component/{relativePath}", UriKind.Relative);
                    application.Resources.MergedDictionaries.Add(
                        (ResourceDictionary)Application.LoadComponent(uri));
                }

                var dispatcher = Dispatcher.CurrentDispatcher;
                ready.TrySetResult(new HostState(application, dispatcher));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "QuickPhrase WPF 测试宿主线程",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    private sealed record HostState(Application Application, Dispatcher Dispatcher);
}
