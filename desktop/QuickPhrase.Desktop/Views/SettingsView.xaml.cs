using System.Windows;
using System.Windows.Input;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>设置页：通用 / 快捷键 / 发送行为 / 应用适配。纯 WPF，Windows Settings 风格。</summary>
public partial class SettingsView : System.Windows.Controls.UserControl
{
    public SettingsViewModel ViewModel { get; }

    public event EventHandler? CloseRequested;

    public SettingsView(ICommandService commands)
    {
        InitializeComponent();
        ViewModel = new SettingsViewModel(commands);
        DataContext = ViewModel;

        ViewModel.Saved += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        ViewModel.Cancelled += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        Loaded += async (_, _) => await ViewModel.LoadAsync();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                ViewModel.CancelCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void EditLauncherShortcut_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var dlg = new HotkeyCaptureDialog(ViewModel.LauncherShortcutDisplay) { Owner = owner };
        if (dlg.ShowDialog() == true)
            ViewModel.LauncherShortcutDisplay = dlg.Display;
    }
}
