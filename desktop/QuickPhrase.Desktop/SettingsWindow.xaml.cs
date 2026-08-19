using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Point = System.Windows.Point;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>
/// 设置窗口只负责承载设置视图和关闭动画，不直接访问持久化实现。
/// SettingsView 与 SettingsViewModel 通过 ICommandService 保存本地设置。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsView _settingsView;
    private bool _allowClose;
    private bool _closeAnimationStarted;

    public SettingsViewModel ViewModel => _settingsView.ViewModel;

    /// <summary>设置页请求重新打开使用引导时，由应用编排层决定窗口切换与数据恢复。</summary>
    public event EventHandler? RestartOnboardingRequested;

    public SettingsWindow(ICommandService commands)
    {
        InitializeComponent();
        _settingsView = new SettingsView(commands);
        _settingsView.CloseRequested += SettingsView_CloseRequested;
        _settingsView.RestartOnboardingRequested += SettingsView_RestartOnboardingRequested;
        ContentRegion.Content = _settingsView;
    }

    private void SettingsView_RestartOnboardingRequested(object? sender, EventArgs e) =>
        RestartOnboardingRequested?.Invoke(this, e);

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_closeAnimationStarted) return;

        // WPF Window ������ֹ���� RenderTransform������ֻ�����ڴ����ڲ�����������������ʱ�׳� InvalidOperationException��
        var transform = new TranslateTransform(0, 8);
        WindowRoot.RenderTransform = transform;
        WindowRoot.RenderTransformOrigin = new Point(0.5, 0.5);
        WindowRoot.Opacity = 0;

        var duration = new Duration(TimeSpan.FromMilliseconds(160));
        var fade = new DoubleAnimation(0, 1, duration);
        var slide = new DoubleAnimation(8, 0, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        WindowRoot.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private async void SettingsView_CloseRequested(object? sender, EventArgs e)
    {
        await CloseWithAnimationAsync();
    }

    private async void SettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;

        // 设置已经逐项即时生效，关闭窗口不再出现“保存/取消”确认。
        e.Cancel = true;
        await CloseWithAnimationAsync();
    }
    private async Task CloseWithAnimationAsync()
    {
        if (_closeAnimationStarted) return;
        _closeAnimationStarted = true;

        // 即时保存已经排队的变更后再退出，避免关闭窗口丢失最后一次操作；不显示确认对话框。
        await ViewModel.ApplyPendingChangesAsync();

        var duration = new Duration(TimeSpan.FromMilliseconds(120));
        var fade = new DoubleAnimation(WindowRoot.Opacity, 0, duration);
        var transform = WindowRoot.RenderTransform as TranslateTransform ?? new TranslateTransform();
        WindowRoot.RenderTransform = transform;
        var slide = new DoubleAnimation(0, 8, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fade.Completed += (_, _) => completion.TrySetResult(null);
        WindowRoot.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
        await completion.Task;

        _allowClose = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _settingsView.CloseRequested -= SettingsView_CloseRequested;
        _settingsView.RestartOnboardingRequested -= SettingsView_RestartOnboardingRequested;
        base.OnClosed(e);
    }
}
