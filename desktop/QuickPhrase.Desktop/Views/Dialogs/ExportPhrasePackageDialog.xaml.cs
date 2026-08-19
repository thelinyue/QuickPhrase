using System.Windows;
using System.Windows.Controls;
using QuickPhrase.Core;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>话术包导出范围和内容选择窗口，只负责 UI 交互，不执行文件写入。</summary>
public partial class ExportPhrasePackageDialog : Window
{
    public ExportPhrasePackageViewModel ViewModel { get; }

    public ExportPhrasePackageDialog(ExportPhrasePackageViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    private void ScopeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex 在 InitializeComponent 阶段就可能触发事件，此时 ViewModel 尚未注入。
        if (DataContext is not ExportPhrasePackageViewModel viewModel)
            return;

        if (ScopeCombo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<PhrasePackageExportScope>(tag, out var scope))
            viewModel.Scope = scope;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel.SetAllSelected(true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => ViewModel.SetAllSelected(false);

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ErrorMessage = ViewModel.ValidateSelection();
        if (ViewModel.ErrorMessage is not null) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
