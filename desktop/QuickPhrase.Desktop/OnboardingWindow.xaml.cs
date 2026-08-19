using System.Windows;
using QuickPhrase.Desktop.Onboarding;

namespace QuickPhrase.Desktop;

/// <summary>闪语五步向导窗口只负责布局与焦点，不承载数据规则和正式投递逻辑。</summary>
public partial class OnboardingWindow : Window
{
    public OnboardingWindow(OnboardingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => { UpdateLayout(); Activate(); };
    }

    public void CloseWithoutSkipping()
    {
        Close();
    }
}




