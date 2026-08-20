using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickPhrase.Core;
using FileOpenDialog = Microsoft.Win32.OpenFileDialog;
using FileSaveDialog = Microsoft.Win32.SaveFileDialog;
using WpfMessageBox = System.Windows.MessageBox;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>设置页：左侧模块导航与右侧即时生效内容。纯 WPF，保持 Windows 原生效率工具的紧凑层级。</summary>
public partial class SettingsView : System.Windows.Controls.UserControl
{
    public SettingsViewModel ViewModel { get; }

    public event EventHandler? CloseRequested;

    /// <summary>
    /// 将设置页模型的重新引导请求转发给宿主窗口，方便应用编排层订阅。
    /// 事件本身不执行导航，也不触碰业务数据。
    /// </summary>
    public event EventHandler? RestartOnboardingRequested;

    public SettingsView(ICommandService commands, ISyncAccountService? syncAccounts = null, ISyncProvider? syncProvider = null)
    {
        InitializeComponent();
        ViewModel = new SettingsViewModel(commands, syncAccounts, syncProvider);
        DataContext = ViewModel;
        ShowSection(SettingsNavigation.SelectedIndex);
        ViewModel.RestartOnboardingRequested += ViewModel_RestartOnboardingRequested;
        ViewModel.DataManagement.ImportRequested += DataManagement_ImportRequested;
        ViewModel.DataManagement.ExportRequested += DataManagement_ExportRequested;

        Loaded += async (_, _) => await ViewModel.LoadAsync();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        };
    }


    /// <summary>
    /// 左侧导航只切换当前模块的可见性，不创建新的窗口或 ViewModel，
    /// 因而保留原有绑定、命令入口和即时保存链路。
    /// </summary>
    private void SettingsNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GeneralSection is null || HotkeysSection is null || DeliverySection is null ||
            AdaptersSection is null || EnterpriseSyncSection is null || DataManagementSection is null)
            return;

        ShowSection(SettingsNavigation.SelectedIndex);
    }

    private void ShowSection(int index)
    {
        GeneralSection.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        HotkeysSection.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        DeliverySection.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        AdaptersSection.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        EnterpriseSyncSection.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        DataManagementSection.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void EnterprisePassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel.EnterpriseSync is not null && sender is PasswordBox passwordBox)
            ViewModel.EnterpriseSync.Password = passwordBox.Password;
    }

    private async void ClearEnterpriseCache_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.EnterpriseSync is null) return;
        var choice = WpfMessageBox.Show(Window.GetWindow(this), "只清除本机企业缓存，不会删除个人话术或服务器数据。清除后需重新同步才能使用企业话术。是否继续？", "确认清除企业缓存", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.OK) await ViewModel.EnterpriseSync.ClearEnterpriseCacheAsync();
    }

    private void ViewModel_RestartOnboardingRequested(object? sender, EventArgs e) =>
        RestartOnboardingRequested?.Invoke(this, e);

    private async void DataManagement_ImportRequested(object? sender, EventArgs e)
    {
        var open = new FileOpenDialog
        {
            Filter = "QuickPhrase 话术包 (*.qphrase)|*.qphrase",
            DefaultExt = ".qphrase",
            CheckFileExists = true,
            Multiselect = false,
            Title = "选择话术包",
        };
        if (open.ShowDialog(Window.GetWindow(this)) != true) return;

        var import = await ViewModel.DataManagement.LoadImportAsync(open.FileName);
        if (import is null)
        {
            ShowDataManagementError(ViewModel.DataManagement.ErrorMessage ?? "话术包读取失败。");
            return;
        }

        var preview = new ImportPhrasePackageDialog(import) { Owner = Window.GetWindow(this) };
        if (preview.ShowDialog() != true) return;

        var result = await ViewModel.DataManagement.ConfirmImportAsync(import);
        if (result is null) return;
        if (result.Succeeded)
        {
            WpfMessageBox.Show(Window.GetWindow(this),
                $"话术包导入完成。\n新增分类：{result.NewCategoryCount}\n新增话术：{result.NewPhraseCount}\n跳过重复：{result.SkippedDuplicateCount}",
                "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            ShowDataManagementError(result.Message);
        }
    }

    private async void DataManagement_ExportRequested(object? sender, EventArgs e)
    {
        var export = await ViewModel.DataManagement.LoadExportAsync();
        if (export is null)
        {
            ShowDataManagementError(ViewModel.DataManagement.ErrorMessage ?? "读取本地话术失败。");
            return;
        }

        var preview = new ExportPhrasePackageDialog(export) { Owner = Window.GetWindow(this) };
        if (preview.ShowDialog() != true) return;

        try
        {
            // 先由 Core 生成闭包，再弹出保存位置；文件写入仍由 ICommandService 处理。
            _ = export.BuildDocument();
        }
        catch (InvalidOperationException exception)
        {
            ShowDataManagementError(exception.Message);
            return;
        }

        var save = new FileSaveDialog
        {
            Filter = "QuickPhrase 话术包 (*.qphrase)|*.qphrase",
            DefaultExt = ".qphrase",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = SanitizePackageFileName(export.Name),
            Title = "保存话术包",
        };
        if (save.ShowDialog(Window.GetWindow(this)) != true) return;

        var succeeded = await ViewModel.DataManagement.WriteExportAsync(save.FileName, export);
        if (succeeded)
        {
            WpfMessageBox.Show(Window.GetWindow(this), "话术包导出完成。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            ShowDataManagementError(ViewModel.DataManagement.ErrorMessage ?? "话术包导出失败。");
        }
    }

    private void ShowDataManagementError(string message) =>
        WpfMessageBox.Show(Window.GetWindow(this), message, "数据管理", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static string SanitizePackageFileName(string name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "我的话术包" : name.Trim();
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value.EndsWith(".qphrase", StringComparison.OrdinalIgnoreCase) ? value : value + ".qphrase";
    }

    private async void ApplyRecommendedShortcut_Click(object sender, RoutedEventArgs e) =>
        await ApplyShortcutPresetAsync(new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space));

    private async void ApplyAlternateShortcut_Click(object sender, RoutedEventArgs e) =>
        await ApplyShortcutPresetAsync(new ShortcutChord(ShortcutModifiers.Ctrl, ShortcutKey.Space));

    private async Task ApplyShortcutPresetAsync(ShortcutChord chord)
    {
        var result = await ViewModel.ApplyLauncherShortcutAsync(chord);
        if (!result.IsSuccess)
            ShowShortcutError(result.Error?.Message ?? "快捷键保存失败，请重试。");
    }

    private void EditCustomShortcut_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HotkeyCaptureDialog(
            ViewModel.LauncherShortcut,
            ViewModel.ApplyLauncherShortcutAsync)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    private void ShowShortcutError(string message) =>
        WpfMessageBox.Show(Window.GetWindow(this), message, "快捷键", MessageBoxButton.OK, MessageBoxImage.Warning);
}
