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
/// �������ô��ڡ����ڱ��ַ�ģ̬���������Կɲ��������ñ��汾����������
/// SettingsView/SettingsViewModel��������ͨ�������� ICommandService д�뱾�����òִ���
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsView _settingsView;
    private bool _allowClose;
    private bool _closeAnimationStarted;

    public SettingsViewModel ViewModel => _settingsView.ViewModel;

    public SettingsWindow(ICommandService commands)
    {
        InitializeComponent();
        _settingsView = new SettingsView(commands);
        _settingsView.CloseRequested += SettingsView_CloseRequested;
        ContentRegion.Content = _settingsView;
    }

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

        if (ViewModel.HasUnsavedChanges)
        {
            e.Cancel = true;
            var dialog = new NavigationConfirmDialog { Owner = this };
            _ = dialog.ShowDialog();
            switch (dialog.Decision)
            {
                case NavigationDecision.ContinueEditing:
                    return;
                case NavigationDecision.SaveAndLeave:
                    await ViewModel.SaveAsync();
                    if (!ViewModel.HasUnsavedChanges)
                    {
                        _allowClose = true;
                        Close();
                    }
                    return;
                default:
                    ViewModel.DiscardChanges();
                    break;
            }
        }

        e.Cancel = true;
        await CloseWithAnimationAsync();
    }

    private async Task CloseWithAnimationAsync()
    {
        if (_closeAnimationStarted) return;
        _closeAnimationStarted = true;

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
        base.OnClosed(e);
    }
}







