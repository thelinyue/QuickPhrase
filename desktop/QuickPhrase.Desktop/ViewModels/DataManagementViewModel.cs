using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickPhrase.Core;
using QuickPhrase.Desktop.Services;

namespace QuickPhrase.Desktop.ViewModels;

/// <summary>
/// 设置页“数据管理”编排模型。文件选择器和 WPF 对话框仍由 View 处理，数据读写只通过 ICommandService 完成。
/// </summary>
public sealed partial class DataManagementViewModel : ObservableObject
{
    private readonly ICommandService _commands;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    public event EventHandler? ImportRequested;
    public event EventHandler? ExportRequested;

    public DataManagementViewModel(ICommandService commands) => _commands = commands;

    [RelayCommand]
    private void RequestImport() => ImportRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RequestExport() => ExportRequested?.Invoke(this, EventArgs.Empty);

    public async Task<ImportPhrasePackageViewModel?> LoadImportAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IsBusy) return null;
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = "正在读取话术包…";
        try
        {
            var package = await _commands.ReadPhrasePackageAsync(path, cancellationToken);
            var errors = PhrasePackagePlanner.Validate(package);
            if (errors.Count > 0)
            {
                ErrorMessage = errors[0];
                return null;
            }

            var snapshot = await _commands.CapturePhrasePackageSnapshotAsync(cancellationToken);
            StatusMessage = null;
            return new ImportPhrasePackageViewModel(_commands, package, snapshot);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
            return null;
        }
        catch (Exception)
        {
            ErrorMessage = "话术包读取失败，请确认文件完整且格式正确。";
            StatusMessage = null;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<ExportPhrasePackageViewModel?> LoadExportAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy) return null;
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = "正在读取本地话术…";
        try
        {
            var snapshot = await _commands.CapturePhrasePackageSnapshotAsync(cancellationToken);
            StatusMessage = null;
            return new ExportPhrasePackageViewModel(snapshot);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
            return null;
        }
        catch (Exception)
        {
            ErrorMessage = "读取本地话术失败，请稍后重试。";
            StatusMessage = null;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<PhrasePackageImportResult?> ConfirmImportAsync(ImportPhrasePackageViewModel import, CancellationToken cancellationToken = default)
    {
        var selectionError = import.ValidateSelection();
        if (selectionError is not null)
        {
            ErrorMessage = selectionError;
            return null;
        }

        if (IsBusy) return null;
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = "正在导入话术包…";
        try
        {
            await import.RebuildPlanAsync(cancellationToken);
            var result = await _commands.ImportPhrasePackageAsync(import.Plan, cancellationToken);
            if (!result.Succeeded) ErrorMessage = result.Message;
            else StatusMessage = result.Message;
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
            return null;
        }
        catch (Exception)
        {
            ErrorMessage = "话术包导入失败，数据库未发生变更。";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> WriteExportAsync(string path, ExportPhrasePackageViewModel export, CancellationToken cancellationToken = default)
    {
        var selectionError = export.ValidateSelection();
        if (selectionError is not null)
        {
            ErrorMessage = selectionError;
            return false;
        }

        if (IsBusy) return false;
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = "正在写入话术包…";
        try
        {
            var document = export.BuildDocument();
            await _commands.WritePhrasePackageAsync(path, document, cancellationToken);
            StatusMessage = "话术包导出完成。";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
            return false;
        }
        catch (Exception)
        {
            ErrorMessage = "话术包导出失败，未生成完整文件。";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
