using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickPhrase.Desktop.Views.Shared;

/// <summary>
/// 话术库与 Launcher 共用的轻量状态呈现器。
/// 状态控件只负责统一排版和动作入口，状态判定仍由各宿主 ViewModel/窗口负责，避免把业务流程塞入视觉组件。
/// </summary>
public partial class StatePresenter : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(StatePresenter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(StatePresenter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(StatePresenter), new PropertyMetadata(string.Empty, OnActionPropertyChanged));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(StatePresenter), new PropertyMetadata(null, OnActionPropertyChanged));

    public static readonly DependencyProperty StateKindProperty = DependencyProperty.Register(
        nameof(StateKind), typeof(StateKind), typeof(StatePresenter), new PropertyMetadata(StateKind.Empty));

    public StatePresenter()
    {
        InitializeComponent();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
    public StateKind StateKind { get => (StateKind)GetValue(StateKindProperty); set => SetValue(StateKindProperty, value); }

    public bool IsActionVisible => !string.IsNullOrWhiteSpace(ActionText) && ActionCommand is not null;

    private static void OnActionPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // IsActionVisible 是派生依赖属性：动作文本或命令任一变化时，主动同步其值，
        // 这样 XAML 绑定可以在宿主运行时注入重试/新建命令后立即刷新。
        var presenter = (StatePresenter)d;
        presenter.SetValue(IsActionVisibleProperty,
            !string.IsNullOrWhiteSpace(presenter.ActionText) && presenter.ActionCommand is not null);
    }

    public static readonly DependencyProperty IsActionVisibleProperty = DependencyProperty.Register(
        nameof(IsActionVisible), typeof(bool), typeof(StatePresenter), new PropertyMetadata(false));
}

public enum StateKind
{
    Empty,
    Loading,
    Error,
}


