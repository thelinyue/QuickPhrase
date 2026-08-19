using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace QuickPhrase.Desktop;

/// <summary>快捷捕获对话框：在聚焦窗口内监听下一次「修饰键 + 主键」组合。
/// 注意 Alt+Space 等系统组合可能被 OS 拦截，属已知限制，正式捕获由 Phase F 桌面层处理。</summary>
public partial class HotkeyCaptureDialog : Window
{
    public string Display { get; private set; } = "";

    /// <summary>供设置持久化使用的快捷键归一化值，避免重复保存展示格式。</summary>
    public string Normalized => string.Join("+", Display
        .Split('+', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Trim().ToLowerInvariant())
        .Where(part => part.Length > 0)
        .Distinct());

    public HotkeyCaptureDialog(string current)
    {
        InitializeComponent();
        CapturedText.Text = string.IsNullOrEmpty(current) ? "按下组合键…" : current;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return;
        }

        var mods = Keyboard.Modifiers;
        var parts = new List<string>();
        if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((mods & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(key.ToString());
        Display = string.Join("+", parts);
        CapturedText.Text = Display;
        e.Handled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(Display)) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}