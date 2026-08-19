using System.Windows;
using QuickPhrase.Desktop.ViewModels;

namespace QuickPhrase.Desktop;

/// <summary>话术包导入预览窗口，只负责展示分类选择和确认，不直接访问文件或数据库。</summary>
public partial class ImportPhrasePackageDialog : Window
{
    public ImportPhrasePackageViewModel ViewModel { get; }

    public ImportPhrasePackageDialog(ImportPhrasePackageViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
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
