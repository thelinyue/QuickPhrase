using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QuickPhrase.Core;
using QuickPhrase.Desktop.DesignSystem.Components;

namespace QuickPhrase.Desktop;

/// <summary>
/// 快捷键捕获对话框只编排候选值、取消和异步应用结果。
/// 键盘解释由 ShortcutInput 负责，系统占用检测、Stage/Commit/Rollback 与 SQLite 保存由注入的应用委托完成。
/// </summary>
public partial class HotkeyCaptureDialog : Window, INotifyPropertyChanged
{
    private readonly Func<ShortcutChord, CancellationToken, Task<RepositoryResult<AppSettings>>> _applyAsync;
    private ShortcutChord _candidateChord;
    private string? _captureErrorMessage;
    private bool _isApplying;

    public HotkeyCaptureDialog(
        ShortcutChord current,
        Func<ShortcutChord, CancellationToken, Task<RepositoryResult<AppSettings>>> applyAsync)
    {
        ArgumentNullException.ThrowIfNull(applyAsync);
        _candidateChord = current;
        _applyAsync = applyAsync;
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ShortcutChord CandidateChord
    {
        get => _candidateChord;
        private set
        {
            if (_candidateChord == value)
                return;

            _candidateChord = value;
            OnPropertyChanged();
        }
    }

    public string? CaptureErrorMessage
    {
        get => _captureErrorMessage;
        private set
        {
            if (string.Equals(_captureErrorMessage, value, StringComparison.Ordinal))
                return;

            _captureErrorMessage = value;
            OnPropertyChanged();
        }
    }

    private void CapturedShortcut_CaptureCompleted(object sender, ShortcutCaptureCompletedEventArgs e)
    {
        CandidateChord = e.Chord;
        CaptureErrorMessage = null;
    }

    private void CapturedShortcut_CaptureCanceled(object sender, RoutedEventArgs e)
    {
        CandidateChord = CapturedShortcut.Chord ?? CandidateChord;
        CaptureErrorMessage = null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplying)
            return;

        _isApplying = true;
        SaveButton.IsEnabled = false;
        CapturedShortcut.IsEnabled = false;
        CaptureErrorMessage = null;
        try
        {
            var result = await _applyAsync(CandidateChord, CancellationToken.None);
            if (!result.IsSuccess || result.Value is null)
            {
                CaptureErrorMessage = result.Error?.Message ?? "快捷键保存失败，请重试。";
                return;
            }

            DialogResult = true;
        }
        catch (Exception exception)
        {
            var traceId = Guid.NewGuid();
            System.Diagnostics.Trace.TraceError(
                "快捷键保存失败。阶段：HOTKEY_DIALOG_APPLY；结果码：SHORTCUT_APPLY_FAILED；TraceId：{0}；异常类型：{1}",
                traceId,
                exception.GetType().Name);
            CaptureErrorMessage = $"快捷键保存失败，请重试。TraceId：{traceId}";
        }
        finally
        {
            _isApplying = false;
            if (IsVisible)
            {
                SaveButton.IsEnabled = true;
                CapturedShortcut.IsEnabled = true;
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
