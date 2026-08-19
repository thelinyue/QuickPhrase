using System;
using System.Collections.Generic;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using SysIO = System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls.Primitives;

namespace XamlSetterChecker
{
    // 确定性校验器：把主题 XAML 当作 XML 解析，
    // 对每个 Style / ControlTemplate 的 TargetType，逐一校验其 Setter.Property
    // （以及 Trigger.Property）是否为真实存在的依赖属性。
    // 这能精确定位“Setter.Property 解析为 null”的非法 Setter，无需靠不可靠的 BAML 行号。
    internal class Program
    {
        private static readonly Dictionary<string, Type> TypeMap = new()
        {
            ["Window"] = typeof(Window),
            ["Button"] = typeof(Button),
            ["TextBlock"] = typeof(TextBlock),
            ["TextBox"] = typeof(TextBox),
            ["CheckBox"] = typeof(CheckBox),
            ["ListBoxItem"] = typeof(ListBoxItem),
            ["ListViewItem"] = typeof(ListViewItem),
            ["Border"] = typeof(Border),
            ["ContextMenu"] = typeof(ContextMenu),
            ["MenuItem"] = typeof(MenuItem),
            ["Separator"] = typeof(Separator),
            ["Control"] = typeof(Control),
            ["Grid"] = typeof(Grid),
            ["FrameworkElement"] = typeof(FrameworkElement),
            ["UIElement"] = typeof(UIElement),
            ["ContentControl"] = typeof(ContentControl),
            ["Panel"] = typeof(Panel),
            ["StackPanel"] = typeof(StackPanel),
            ["DockPanel"] = typeof(DockPanel),
            ["Canvas"] = typeof(Canvas),
            ["Image"] = typeof(Image),
            ["ScrollViewer"] = typeof(ScrollViewer),
            ["ListBox"] = typeof(ListBox),
            ["ListView"] = typeof(ListView),
            ["ComboBox"] = typeof(ComboBox),
            ["TreeView"] = typeof(TreeView),
            ["HeaderedContentControl"] = typeof(HeaderedContentControl),
            ["ContentPresenter"] = typeof(ContentPresenter),
            ["TextElement"] = typeof(TextElement),
            ["Shape"] = typeof(Shape),
            ["Rectangle"] = typeof(Rectangle),
            ["Ellipse"] = typeof(Ellipse),
            ["Path"] = typeof(Path),
            ["Popup"] = typeof(Popup),
        };

        private static int _errors;

        private static void Main(string[] args)
        {


            var baseDir = SysIO.Path.GetFullPath(SysIO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "desktop", "QuickPhrase.Desktop"));
            var files = new[]
            {
                SysIO.Path.Combine(baseDir, "Themes", "QuickPhraseTheme.xaml"),
                SysIO.Path.Combine(baseDir, "Themes", "Converters.xaml"),
                SysIO.Path.Combine(baseDir, "Themes", "Controls.xaml"),
            };

            foreach (var f in files)
            {
                Console.WriteLine($"==== {SysIO.Path.GetFileName(f)} ====");
                if (!SysIO.Path.Exists(f))
                {
                    Console.WriteLine("  [MISSING] " + f);
                    continue;
                }
                var doc = XDocument.Load(f, LoadOptions.SetLineInfo);
                Walk(doc.Root!, f, null);
            }

            Console.WriteLine();
            Console.WriteLine(_errors == 0 ? "OK: 未检测到非法 Setter/Trigger 属性。" : $"发现 {_errors} 处非法 Setter/Trigger 属性。");
            Environment.Exit(_errors == 0 ? 0 : 1);
        }

        private static void Walk(XElement node, string file, Type? ownerType)
        {
            var localName = node.Name.LocalName;

            if ((localName == "Style" || localName == "ControlTemplate") && node.Attribute("TargetType") != null)
            {
                var tt = node.Attribute("TargetType")!.Value.Trim();
                ownerType = ResolveType(tt) ?? ownerType;
            }

            if (localName == "Setter" && node.Attribute("Property") != null && node.Attribute("TargetName") == null)
            {
                CheckProperty(file, node, "Setter", node.Attribute("Property")!.Value, ownerType);
            }
            if ((localName == "Trigger" || localName == "DataTrigger") && node.Attribute("Property") != null)
            {
                CheckProperty(file, node, localName, node.Attribute("Property")!.Value, ownerType);
            }

            foreach (var child in node.Elements())
                Walk(child, file, ownerType);
        }

        private static void CheckProperty(string file, XElement node, string kind, string prop, Type? ownerType)
        {
            if (ownerType == null)
                return;
            if (prop.Contains("."))
                return;

            if (!HasDependencyProperty(ownerType, prop))
            {
                var li = (IXmlLineInfo)node;
                Console.WriteLine($"  [INVALID {kind}] {prop}  -> 类型 {ownerType.Name} 上不存在（{SysIO.Path.GetFileName(file)} 第 {li.LineNumber} 行）");
                _errors++;
            }
        }

        // 通过“<属性>Property”静态字段判断依赖属性是否真实注册在目标类型或其基类上。
        // 这与 WPF 在 Setter 物化时解析属性名所用的机制等价（找不到即为非法 Setter）。
        private static bool HasDependencyProperty(Type owner, string propName)
        {
            var fi = owner.GetField(propName + "Property",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return fi != null && fi.FieldType == typeof(DependencyProperty);
        }

        private static Type? ResolveType(string name)
        {
            var clean = name.Contains(":") ? name.Split(':')[^1] : name;
            return TypeMap.TryGetValue(clean, out var t) ? t : null;
        }
    }
}
