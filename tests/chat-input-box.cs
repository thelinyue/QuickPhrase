using System;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Forms;

// 旧 WinForms 的默认 UIA 代理只报告 Pane。显式提供真实控件状态，
// 让隔离验收经过与产品相同的跨进程查询；不读写正文、不执行 UIA 输入操作。
public class ShanyuChatInputBox : TextBox {
    public bool ValuePatternAvailable = true;
    InputProvider provider;
    protected override void WndProc(ref Message message) {
        if (message.Msg == 0x003D && message.LParam.ToInt64() == AutomationInteropProvider.RootObjectId) {
            if (provider == null) provider = new InputProvider(this);
            message.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, message.WParam, message.LParam, provider);
            return;
        }
        base.WndProc(ref message);
    }
    sealed class InputProvider : IRawElementProviderSimple, IValueProvider {
        readonly ShanyuChatInputBox input;
        public InputProvider(ShanyuChatInputBox input) { this.input = input; }
        public ProviderOptions ProviderOptions { get { return ProviderOptions.ServerSideProvider; } }
        public IRawElementProviderSimple HostRawElementProvider { get { return AutomationInteropProvider.HostProviderFromHandle(input.Handle); } }
        public object GetPatternProvider(int patternId) { return input.ValuePatternAvailable && patternId == ValuePatternIdentifiers.Pattern.Id ? this : null; }
        public object GetPropertyValue(int propertyId) {
            if (propertyId == AutomationElementIdentifiers.ControlTypeProperty.Id) return ControlType.Edit.Id;
            if (propertyId == AutomationElementIdentifiers.AutomationIdProperty.Id) return input.Name;
            if (propertyId == AutomationElementIdentifiers.ClassNameProperty.Id) return "ShanyuChatInputBox";
            if (propertyId == AutomationElementIdentifiers.IsEnabledProperty.Id) return input.Enabled;
            if (propertyId == AutomationElementIdentifiers.IsOffscreenProperty.Id) return !input.Visible;
            if (propertyId == AutomationElementIdentifiers.IsKeyboardFocusableProperty.Id) return input.CanSelect;
            if (propertyId == AutomationElementIdentifiers.HasKeyboardFocusProperty.Id) return input.Focused;
            if (propertyId == AutomationElementIdentifiers.IsPasswordProperty.Id) return input.UseSystemPasswordChar || input.PasswordChar != '\0';
            if (propertyId == AutomationElementIdentifiers.IsControlElementProperty.Id || propertyId == AutomationElementIdentifiers.IsContentElementProperty.Id) return true;
            if (propertyId == AutomationElementIdentifiers.BoundingRectangleProperty.Id) {
                var rect = input.RectangleToScreen(input.ClientRectangle);
                return new double[] { rect.Left, rect.Top, rect.Width, rect.Height };
            }
            return null;
        }
        public bool IsReadOnly { get { return input.ReadOnly; } }
        public string Value { get { return String.Empty; } }
        public void SetValue(string value) { throw new InvalidOperationException("隔离吸附宿主不接受 UI Automation 写入"); }
    }
}
