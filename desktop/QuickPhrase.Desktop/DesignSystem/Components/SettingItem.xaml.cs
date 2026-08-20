using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;

namespace QuickPhrase.Desktop.DesignSystem.Components;

/// <summary>
/// 设置页统一行组件。组件只提供标题、说明与右侧控件的固定布局，
/// 不执行保存、命令编排或持久化，业务状态由宿主 ViewModel 负责。
/// </summary>
public partial class SettingItem : System.Windows.Controls.UserControl
{
    private static readonly DependencyProperty AutomationOwnerProperty = DependencyProperty.RegisterAttached(
        "AutomationOwner",
        typeof(SettingItem),
        typeof(SettingItem),
        new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(SettingItem),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(SettingItem),
        new PropertyMetadata(null));

    public static readonly DependencyProperty ControlContentProperty = DependencyProperty.Register(
        nameof(ControlContent),
        typeof(object),
        typeof(SettingItem),
        new PropertyMetadata(null, ControlContentChanged));

    public static readonly DependencyProperty ShowDividerProperty = DependencyProperty.Register(
        nameof(ShowDivider),
        typeof(bool),
        typeof(SettingItem),
        new PropertyMetadata(false));

    private DependencyObject? _automationTarget;
    private bool _ownsAutomationName;
    private bool _ownsAutomationHelpText;

    public SettingItem()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? ControlContent
    {
        get => GetValue(ControlContentProperty);
        set => SetValue(ControlContentProperty, value);
    }

    public bool ShowDivider
    {
        get => (bool)GetValue(ShowDividerProperty);
        set => SetValue(ShowDividerProperty, value);
    }

    private static void ControlContentChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((SettingItem)dependencyObject).ApplyAutomationSemantics(args.NewValue as DependencyObject);
    }

    /// <summary>
    /// 为未显式声明无障碍文本的右侧控件绑定标题和说明。绑定会随 SettingItem 文案更新，
    /// 同时尊重页面或控件自行提供的 AutomationProperties，避免覆盖更具体的业务语义。
    /// </summary>
    private void ApplyAutomationSemantics(DependencyObject? target)
    {
        ReleaseAutomationSemantics();
        if (target is null)
            return;

        _automationTarget = target;
        if (target.ReadLocalValue(AutomationProperties.NameProperty) == DependencyProperty.UnsetValue)
        {
            BindingOperations.SetBinding(
                target,
                AutomationProperties.NameProperty,
                new System.Windows.Data.Binding(nameof(Title)) { Source = this, Mode = BindingMode.OneWay });
            _ownsAutomationName = true;
        }

        if (target.ReadLocalValue(AutomationProperties.HelpTextProperty) == DependencyProperty.UnsetValue)
        {
            BindingOperations.SetBinding(
                target,
                AutomationProperties.HelpTextProperty,
                new System.Windows.Data.Binding(nameof(Description))
                {
                    Source = this,
                    Mode = BindingMode.OneWay,
                    TargetNullValue = string.Empty,
                });
            _ownsAutomationHelpText = true;
        }

        if (_ownsAutomationName || _ownsAutomationHelpText)
            target.SetValue(AutomationOwnerProperty, this);
    }

    private void ReleaseAutomationSemantics()
    {
        if (_automationTarget is null || !ReferenceEquals(_automationTarget.GetValue(AutomationOwnerProperty), this))
        {
            ResetAutomationOwnership();
            return;
        }

        if (_ownsAutomationName)
            BindingOperations.ClearBinding(_automationTarget, AutomationProperties.NameProperty);
        if (_ownsAutomationHelpText)
            BindingOperations.ClearBinding(_automationTarget, AutomationProperties.HelpTextProperty);
        _automationTarget.ClearValue(AutomationOwnerProperty);
        ResetAutomationOwnership();
    }

    private void ResetAutomationOwnership()
    {
        _automationTarget = null;
        _ownsAutomationName = false;
        _ownsAutomationHelpText = false;
    }
}
