using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using QuickPhrase.Core;

namespace QuickPhrase.Desktop.DesignSystem.Components;

/// <summary>快捷键捕获完成事件，携带尚未写入组件 Chord 的候选组合。</summary>
public sealed class ShortcutCaptureCompletedEventArgs : RoutedEventArgs
{
    public ShortcutCaptureCompletedEventArgs(RoutedEvent routedEvent, ShortcutChord chord)
        : base(routedEvent)
    {
        Chord = chord;
    }

    public ShortcutChord Chord { get; }
}

/// <summary>快捷键捕获完成事件处理器。</summary>
public delegate void ShortcutCaptureCompletedEventHandler(object sender, ShortcutCaptureCompletedEventArgs e);

/// <summary>一次键盘输入对捕获流程的影响。</summary>
internal enum ShortcutCaptureAction
{
    Ignore,
    Cancel,
    Complete,
    Reject,
}

/// <summary>纯键盘解释结果，便于在不创建窗口或注册系统热键的情况下验证捕获规则。</summary>
internal readonly record struct ShortcutCaptureInterpretation(
    ShortcutCaptureAction Action,
    ShortcutChord? Chord = null,
    string? ErrorMessage = null);

/// <summary>
/// 结构化快捷键的展示与捕获组件。它只把 WPF 键盘输入转换为 Core ShortcutChord 候选值，
/// 不更新当前 Chord，也不负责系统占用检测、平台注册、配置保存或应用级热键编排。
/// </summary>
public partial class ShortcutInput : System.Windows.Controls.UserControl
{
    private const string NumpadUnsupportedMessage = "暂不支持数字小键盘，请使用主键盘数字键。";
    private readonly ObservableCollection<string> _displayKeys = [];

    public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(
        nameof(Chord),
        typeof(ShortcutChord?),
        typeof(ShortcutInput),
        new PropertyMetadata(null, ChordChanged));

    public static readonly DependencyProperty IsCapturingProperty = DependencyProperty.Register(
        nameof(IsCapturing),
        typeof(bool),
        typeof(ShortcutInput),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            null,
            CoerceIsCapturing));

    public static readonly DependencyProperty ErrorMessageProperty = DependencyProperty.Register(
        nameof(ErrorMessage),
        typeof(string),
        typeof(ShortcutInput),
        new PropertyMetadata(null));

    public static readonly RoutedEvent CaptureCompletedEvent = EventManager.RegisterRoutedEvent(
        nameof(CaptureCompleted),
        RoutingStrategy.Bubble,
        typeof(ShortcutCaptureCompletedEventHandler),
        typeof(ShortcutInput));

    public static readonly RoutedEvent CaptureCanceledEvent = EventManager.RegisterRoutedEvent(
        nameof(CaptureCanceled),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(ShortcutInput));

    public ShortcutInput()
    {
        DisplayKeys = new ReadOnlyObservableCollection<string>(_displayKeys);
        InitializeComponent();
        IsEnabledChanged += ShortcutInput_IsEnabledChanged;
        RefreshDisplayKeys();
    }

    /// <summary>
    /// 供 XAML 和宿主只读展示的键帽序列。内部集合会随 Chord 更新，调用方不能绕过组件修改展示状态。
    /// </summary>
    public ReadOnlyObservableCollection<string> DisplayKeys { get; }

    public ShortcutChord? Chord
    {
        get => (ShortcutChord?)GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        set => SetValue(IsCapturingProperty, value);
    }

    public string? ErrorMessage
    {
        get => (string?)GetValue(ErrorMessageProperty);
        set => SetValue(ErrorMessageProperty, value);
    }

    public event ShortcutCaptureCompletedEventHandler CaptureCompleted
    {
        add => AddHandler(CaptureCompletedEvent, value);
        remove => RemoveHandler(CaptureCompletedEvent, value);
    }

    public event RoutedEventHandler CaptureCanceled
    {
        add => AddHandler(CaptureCanceledEvent, value);
        remove => RemoveHandler(CaptureCanceledEvent, value);
    }

    private static void ChordChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((ShortcutInput)dependencyObject).RefreshDisplayKeys();
    }

    private static object CoerceIsCapturing(DependencyObject dependencyObject, object baseValue)
    {
        return dependencyObject is ShortcutInput { IsEnabled: true } && (bool)baseValue;
    }

    private void ShortcutInput_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            // 禁用控件时同步清除捕获基值，避免重新启用后恢复到陈旧的捕获状态。
            SetCurrentValue(IsCapturingProperty, false);
        }
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        BeginCapture();
    }

    private void CaptureButton_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = ProcessKeyInput(key, Keyboard.Modifiers, e.IsRepeat);
    }

    private void BeginCapture()
    {
        if (!IsEnabled)
            return;

        SetCurrentValue(ErrorMessageProperty, null);
        SetCurrentValue(IsCapturingProperty, true);
        CaptureButton.Focus();
    }

    /// <summary>
    /// 处理一次已经标准化的 WPF 键盘输入。单独保留此入口，使预览键盘事件和 STA 状态机测试走同一条真实路径。
    /// 自动重复一律被消费但不改变状态，避免 Alt+Space 完成后重复的 Space 再次启动捕获。
    /// </summary>
    internal bool ProcessKeyInput(Key key, ModifierKeys modifiers, bool isRepeat)
    {
        if (!IsEnabled)
            return false;

        if (isRepeat)
            return true;

        if (!IsCapturing)
        {
            if (key is not (Key.Enter or Key.Space))
                return false;

            BeginCapture();
            return true;
        }

        var interpretation = InterpretKey(key, modifiers);
        switch (interpretation.Action)
        {
            case ShortcutCaptureAction.Cancel:
                SetCurrentValue(ErrorMessageProperty, null);
                SetCurrentValue(IsCapturingProperty, false);
                RaiseEvent(new RoutedEventArgs(CaptureCanceledEvent, this));
                return true;

            case ShortcutCaptureAction.Complete when interpretation.Chord is { } candidate:
                SetCurrentValue(ErrorMessageProperty, null);
                SetCurrentValue(IsCapturingProperty, false);
                RaiseEvent(new ShortcutCaptureCompletedEventArgs(CaptureCompletedEvent, candidate));
                return true;

            case ShortcutCaptureAction.Reject:
                SetCurrentValue(ErrorMessageProperty, interpretation.ErrorMessage);
                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// 将一次 WPF 键盘输入解释为取消、忽略、拒绝或合法候选组合。
    /// Modifier-only、无修饰键和普通不支持主键保持捕获状态；数字小键盘会给出明确中文反馈。
    /// </summary>
    internal static ShortcutCaptureInterpretation InterpretKey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape)
            return new ShortcutCaptureInterpretation(ShortcutCaptureAction.Cancel);

        if (IsModifierKey(key))
            return new ShortcutCaptureInterpretation(ShortcutCaptureAction.Ignore);

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return new ShortcutCaptureInterpretation(ShortcutCaptureAction.Reject, ErrorMessage: NumpadUnsupportedMessage);

        if (!TryMapKey(key, out var shortcutKey))
            return new ShortcutCaptureInterpretation(ShortcutCaptureAction.Ignore);

        var shortcutModifiers = MapModifiers(modifiers);
        var chord = new ShortcutChord(shortcutModifiers, shortcutKey);
        return ShortcutChordValidator.Validate(chord).IsValid
            ? new ShortcutCaptureInterpretation(ShortcutCaptureAction.Complete, chord)
            : new ShortcutCaptureInterpretation(ShortcutCaptureAction.Ignore);
    }

    private static ShortcutModifiers MapModifiers(ModifierKeys modifiers)
    {
        var result = ShortcutModifiers.None;
        if ((modifiers & ModifierKeys.Control) != 0)
            result |= ShortcutModifiers.Ctrl;
        if ((modifiers & ModifierKeys.Alt) != 0)
            result |= ShortcutModifiers.Alt;
        if ((modifiers & ModifierKeys.Shift) != 0)
            result |= ShortcutModifiers.Shift;
        if ((modifiers & ModifierKeys.Windows) != 0)
            result |= ShortcutModifiers.Win;
        return result;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private static bool TryMapKey(Key key, out ShortcutKey shortcutKey)
    {
        if (key == Key.Space)
        {
            shortcutKey = ShortcutKey.Space;
            return true;
        }

        if (key is >= Key.A and <= Key.Z)
        {
            shortcutKey = (ShortcutKey)((int)ShortcutKey.A + key - Key.A);
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            shortcutKey = (ShortcutKey)((int)ShortcutKey.Digit0 + key - Key.D0);
            return true;
        }

        if (key is >= Key.F1 and <= Key.F12)
        {
            shortcutKey = (ShortcutKey)((int)ShortcutKey.F1 + key - Key.F1);
            return true;
        }

        shortcutKey = default;
        return false;
    }

    private void RefreshDisplayKeys()
    {
        _displayKeys.Clear();
        if (Chord is not { } chord)
            return;

        AddDisplayKeyIfPresent(chord.Modifiers, ShortcutModifiers.Ctrl, "Ctrl");
        AddDisplayKeyIfPresent(chord.Modifiers, ShortcutModifiers.Alt, "Alt");
        AddDisplayKeyIfPresent(chord.Modifiers, ShortcutModifiers.Shift, "Shift");
        AddDisplayKeyIfPresent(chord.Modifiers, ShortcutModifiers.Win, "Win");
        AddDisplayPart(FormatKey(chord.Key));
    }

    private void AddDisplayKeyIfPresent(ShortcutModifiers modifiers, ShortcutModifiers expected, string text)
    {
        if ((modifiers & expected) != 0)
            AddDisplayPart(text);
    }

    private void AddDisplayPart(string text)
    {
        if (_displayKeys.Count > 0)
            _displayKeys.Add("+");
        _displayKeys.Add(text);
    }

    private static string FormatKey(ShortcutKey key) => key switch
    {
        >= ShortcutKey.Digit0 and <= ShortcutKey.Digit9 => ((int)key - (int)ShortcutKey.Digit0).ToString(),
        _ => key.ToString(),
    };
}
