using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using QuickPhrase.Core;
using FileOpenDialog = Microsoft.Win32.OpenFileDialog;
using FileSaveDialog = Microsoft.Win32.SaveFileDialog;
using WpfMessageBox = System.Windows.MessageBox;
using QuickPhrase.Desktop.Services;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>设置页：通用 / 快捷键 / 发送行为 / 应用适配。纯 WPF，Windows Settings 风格。</summary>
public partial class SettingsView : System.Windows.Controls.UserControl
{
    public SettingsViewModel ViewModel { get; }

    public event EventHandler? CloseRequested;

    /// <summary>
    /// 将设置页模型的重新引导请求转发给宿主窗口，方便应用编排层订阅。
    /// 事件本身不执行导航，也不触碰业务数据。
    /// </summary>
    public event EventHandler? RestartOnboardingRequested;

    public SettingsView(ICommandService commands)
    {
        InitializeComponent();
        ViewModel = new SettingsViewModel(commands);
        DataContext = ViewModel;

        ViewModel.Saved += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        ViewModel.Cancelled += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        ViewModel.RestartOnboardingRequested += ViewModel_RestartOnboardingRequested;
        ViewModel.DataManagement.ImportRequested += DataManagement_ImportRequested;
        ViewModel.DataManagement.ExportRequested += DataManagement_ExportRequested;

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

    private void EditLauncherShortcut_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var dlg = new HotkeyCaptureDialog(ViewModel.LauncherShortcutDisplay) { Owner = owner };
        if (dlg.ShowDialog() == true)
            ViewModel.LauncherShortcutDisplay = dlg.Display;
    }
}
