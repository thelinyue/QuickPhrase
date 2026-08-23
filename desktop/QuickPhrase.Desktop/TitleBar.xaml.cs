using System.Windows;
using System.Windows.Controls;

namespace QuickPhrase.Desktop;

/// <summary>自定义标题栏：只表达设置、最小化、贴边切换和关闭意图，不直接依赖窗口跟踪服务。</summary>
public partial class TitleBar : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty PageTitleProperty =
        DependencyProperty.Register(nameof(PageTitle), typeof(string), typeof(TitleBar), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty ShowSettingsButtonProperty =
        DependencyProperty.Register(nameof(ShowSettingsButton), typeof(bool), typeof(TitleBar), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowDockButtonProperty =
        DependencyProperty.Register(nameof(ShowDockButton), typeof(bool), typeof(TitleBar), new PropertyMetadata(false));
    public static readonly DependencyProperty IsDockedProperty =
        DependencyProperty.Register(nameof(IsDocked), typeof(bool), typeof(TitleBar), new PropertyMetadata(false));

    public string PageTitle { get => (string)GetValue(PageTitleProperty); set => SetValue(PageTitleProperty, value); }
    public bool ShowSettingsButton { get => (bool)GetValue(ShowSettingsButtonProperty); set => SetValue(ShowSettingsButtonProperty, value); }
    public bool ShowDockButton { get => (bool)GetValue(ShowDockButtonProperty); set => SetValue(ShowDockButtonProperty, value); }
    public bool IsDocked { get => (bool)GetValue(IsDockedProperty); set => SetValue(IsDockedProperty, value); }

    public event RoutedEventHandler? SettingsRequested;
    public event RoutedEventHandler? DockToggleRequested;

    public TitleBar() => InitializeComponent();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, e);
    private void DockButton_Click(object sender, RoutedEventArgs e) => DockToggleRequested?.Invoke(this, e);
    private void MinButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) window.WindowState = WindowState.Minimized;
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) window.Close();
    }
}
