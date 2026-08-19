using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QuickPhrase.Desktop.Onboarding;

namespace QuickPhrase.Desktop;

/// <summary>
/// 闪语五步向导窗口只负责布局、焦点和无位移的内容淡入，
/// 不承载数据规则和正式投递逻辑。步骤状态仍由 ViewModel 驱动。
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel _viewModel;

    public OnboardingWindow(OnboardingViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) =>
        {
            UpdateLayout();
            Activate();
            AnimateCurrentStep();
        };
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public void CloseWithoutSkipping()
    {
        Close();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(OnboardingViewModel.CurrentStep)) return;
        // 等待 DataTrigger 完成 Visibility 切换后再淡入，避免动画参与布局测量。
        Dispatcher.BeginInvoke(AnimateCurrentStep, DispatcherPriority.Loaded);
    }

    private void AnimateCurrentStep()
    {
        var panel = _viewModel.CurrentStep switch
        {
            OnboardingStep.Welcome => WelcomeStepPanel,
            OnboardingStep.Category => CategoryStepPanel,
            OnboardingStep.Phrase => PhraseStepPanel,
            OnboardingStep.Practice => PracticeStepPanel,
            OnboardingStep.Complete => CompleteStepPanel,
            _ => null,
        };
        if (panel is null || panel.Visibility != Visibility.Visible) return;

        panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(120),
            FillBehavior = FillBehavior.Stop,
        });
    }
}
