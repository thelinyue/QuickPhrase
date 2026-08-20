using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Markup;
using System.Xml.Linq;
using QuickPhrase.Core;
using QuickPhrase.Desktop.DesignSystem.Components;

namespace QuickPhrase.Desktop.Tests.Theme;

/// <summary>
/// 锁定复合组件的公共契约和职责边界，防止页面迁移时重新引入视觉字面量或业务耦合。
/// </summary>
public sealed class DesignSystemComponentTests
{
    private static readonly string[] ComponentXamlFiles =
    [
        "Components.xaml",
        "SettingItem.xaml",
        "SearchInput.xaml",
        "PhraseResultItem.xaml",
        "CategoryTreeItem.xaml",
        "ShortcutInput.xaml",
    ];

    private static readonly string[] ComponentCodeFiles =
    [
        "SettingItem.xaml.cs",
        "SearchInput.xaml.cs",
        "PhraseResultItem.xaml.cs",
        "CategoryTreeItem.xaml.cs",
        "ShortcutInput.xaml.cs",
    ];

    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void RequiredComponentFiles_ExistAndParseAsXml()
    {
        foreach (var fileName in ComponentXamlFiles)
        {
            var path = ComponentPath(fileName);
            Assert.True(File.Exists(path), $"缺少复合组件 XAML：{path}");
            _ = XDocument.Load(path);
        }

        foreach (var fileName in ComponentCodeFiles)
            Assert.True(File.Exists(ComponentPath(fileName)), $"缺少复合组件代码：{fileName}");
    }

    [Fact]
    public void ComponentsDictionary_ExposesStableComponentStyleKeys()
    {
        var document = XDocument.Load(ComponentPath("Components.xaml"));
        var keys = document.Descendants()
            .Attributes(XamlNamespace + "Key")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in new[]
        {
            "Style.Component.SettingItem",
            "Style.Component.SearchInput",
            "Style.Component.PhraseResultItem",
            "Style.Component.CategoryTreeItem",
            "Style.Component.ShortcutInput",
        })
        {
            Assert.Contains(key, keys);
        }
    }

    [Fact]
    public void SettingItem_ExposesApprovedDependencyPropertiesAndFixedTwoColumnLayout()
    {
        AssertDependencyProperty(SettingItem.TitleProperty, typeof(string), nameof(SettingItem.Title));
        AssertDependencyProperty(SettingItem.DescriptionProperty, typeof(string), nameof(SettingItem.Description));
        AssertDependencyProperty(SettingItem.ControlContentProperty, typeof(object), nameof(SettingItem.ControlContent));
        AssertDependencyProperty(SettingItem.ShowDividerProperty, typeof(bool), nameof(SettingItem.ShowDivider));

        var xaml = File.ReadAllText(ComponentPath("SettingItem.xaml"));
        Assert.Contains("ColumnDefinition Width=\"*\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ColumnDefinition Width=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Title, ElementName=Root}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Description, ElementName=Root}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding ControlContent, ElementName=Root}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowDivider", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchInput_UsesSharedSearchStyleAndTwoWayTextDependencyProperty()
    {
        AssertDependencyProperty(SearchInput.TextProperty, typeof(string), nameof(SearchInput.Text));
        AssertDependencyProperty(SearchInput.PlaceholderProperty, typeof(string), nameof(SearchInput.Placeholder));

        var metadata = Assert.IsType<FrameworkPropertyMetadata>(SearchInput.TextProperty.GetMetadata(typeof(SearchInput)));
        Assert.True(metadata.BindsTwoWayByDefault);

        var xaml = File.ReadAllText(ComponentPath("SearchInput.xaml"));
        Assert.Contains("Style=\"{StaticResource Style.Input.Search}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"{StaticResource Size.Input.Search}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Padding=\"{StaticResource Thickness.Control.Input}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Text, ElementName=Root", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinition Width=\"32\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortcutInput_ExposesApprovedStateAndRoutedEvents()
    {
        AssertDependencyProperty(ShortcutInput.ChordProperty, typeof(ShortcutChord?), nameof(ShortcutInput.Chord));
        AssertDependencyProperty(ShortcutInput.IsCapturingProperty, typeof(bool), nameof(ShortcutInput.IsCapturing));
        AssertDependencyProperty(ShortcutInput.ErrorMessageProperty, typeof(string), nameof(ShortcutInput.ErrorMessage));

        Assert.Equal(nameof(ShortcutInput.CaptureCompleted), ShortcutInput.CaptureCompletedEvent.Name);
        Assert.Equal(RoutingStrategy.Bubble, ShortcutInput.CaptureCompletedEvent.RoutingStrategy);
        Assert.Equal(nameof(ShortcutInput.CaptureCanceled), ShortcutInput.CaptureCanceledEvent.Name);
        Assert.Equal(RoutingStrategy.Bubble, ShortcutInput.CaptureCanceledEvent.RoutingStrategy);
        Assert.NotNull(typeof(ShortcutInput).GetEvent(nameof(ShortcutInput.CaptureCompleted)));
        Assert.NotNull(typeof(ShortcutInput).GetEvent(nameof(ShortcutInput.CaptureCanceled)));
    }

    [Fact]
    public void ShortcutInput_InterpretsCancelModifierAndSupportedChordKeysWithoutMutatingCurrentChord()
    {
        Assert.Equal(ShortcutCaptureAction.Cancel, ShortcutInput.InterpretKey(Key.Escape, ModifierKeys.None).Action);
        Assert.Equal(ShortcutCaptureAction.Ignore, ShortcutInput.InterpretKey(Key.LeftCtrl, ModifierKeys.Control).Action);
        Assert.Equal(ShortcutCaptureAction.Ignore, ShortcutInput.InterpretKey(Key.A, ModifierKeys.None).Action);
        Assert.Equal(ShortcutCaptureAction.Ignore, ShortcutInput.InterpretKey(Key.Tab, ModifierKeys.Control).Action);
        var numpad = ShortcutInput.InterpretKey(Key.NumPad1, ModifierKeys.Control);
        Assert.Equal(ShortcutCaptureAction.Reject, numpad.Action);
        Assert.Contains("数字小键盘", numpad.ErrorMessage, StringComparison.Ordinal);

        AssertCompleted(Key.Space, ModifierKeys.Alt, new ShortcutChord(ShortcutModifiers.Alt, ShortcutKey.Space));

        for (var index = 0; index < 26; index++)
        {
            AssertCompleted(
                (Key)((int)Key.A + index),
                ModifierKeys.Control | ModifierKeys.Shift,
                new ShortcutChord(ShortcutModifiers.Ctrl | ShortcutModifiers.Shift, (ShortcutKey)((int)ShortcutKey.A + index)));
        }

        for (var index = 0; index < 10; index++)
        {
            AssertCompleted(
                (Key)((int)Key.D0 + index),
                ModifierKeys.Windows,
                new ShortcutChord(ShortcutModifiers.Win, (ShortcutKey)((int)ShortcutKey.Digit0 + index)));
        }

        for (var index = 0; index < 12; index++)
        {
            AssertCompleted(
                (Key)((int)Key.F1 + index),
                ModifierKeys.Alt | ModifierKeys.Control,
                new ShortcutChord(ShortcutModifiers.Alt | ShortcutModifiers.Ctrl, (ShortcutKey)((int)ShortcutKey.F1 + index)));
        }

        var source = File.ReadAllText(ComponentPath("ShortcutInput.xaml.cs"));
        Assert.DoesNotContain("Chord = result", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetCurrentValue(ChordProperty", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortcutInput_XamlCoversDisplayCapturingErrorDisabledAndKeyboardFocus()
    {
        var xaml = File.ReadAllText(ComponentPath("ShortcutInput.xaml"));
        Assert.Contains("IsCapturing", xaml, StringComparison.Ordinal);
        Assert.Contains("ErrorMessage", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("IsKeyboardFocused", xaml, StringComparison.Ordinal);
        Assert.Contains("请按下新的快捷键", xaml, StringComparison.Ordinal);
        Assert.Contains("Style.Keycap", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"+\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Brush.Border.Focus}", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_ShortcutInputProvidesAccessibleConflictAndDisabledStatesWithoutApplication()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            AssertSettingItemShortcutInputAutomationContract();
            AssertShortcutInputConflictErrorPresentation();
        });
    }

    private static void AssertSettingItemShortcutInputAutomationContract()
    {
        var settingItemSource = File.ReadAllText(ComponentPath("SettingItem.xaml.cs"));
        Assert.Contains("AutomationProperties.NameProperty", settingItemSource, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.HelpTextProperty", settingItemSource, StringComparison.Ordinal);
        Assert.Contains("BindingOperations.SetBinding", settingItemSource, StringComparison.Ordinal);

        var shortcutInput = LoadShortcutInputVisual();
        AutomationProperties.SetName(shortcutInput, "打开闪念");
        AutomationProperties.SetHelpText(shortcutInput, "使用快捷键打开闪念");
        using var host = ShowInTestWindow(shortcutInput);
        var captureButton = FindVisualChild<Button>(shortcutInput, "CaptureButton");
        var peer = new ButtonAutomationPeer(captureButton);

        Assert.Equal("打开闪念", peer.GetName());
        Assert.Equal("使用快捷键打开闪念", peer.GetHelpText());

        AutomationProperties.SetName(shortcutInput, "打开启动器");
        AutomationProperties.SetHelpText(shortcutInput, "显示闪念启动器");
        PumpDispatcher();
        Assert.Equal("打开启动器", peer.GetName());
        Assert.Equal("显示闪念启动器", peer.GetHelpText());
    }

    private static void AssertShortcutInputConflictErrorPresentation()
    {
        var shortcutInput = LoadShortcutInputVisual();
        AutomationProperties.SetName(shortcutInput, "打开闪念");
        AutomationProperties.SetHelpText(shortcutInput, "使用快捷键打开闪念");
        shortcutInput.ErrorMessage = "快捷键冲突";
        using var host = ShowInTestWindow(shortcutInput);
        var captureButton = FindVisualChild<Button>(shortcutInput, "CaptureButton");
        var errorText = FindVisualChild<TextBlock>(shortcutInput, "ErrorText");
        var peer = new ButtonAutomationPeer(captureButton);

        Assert.Equal("快捷键冲突", errorText.Text);
        Assert.Equal(Visibility.Visible, errorText.Visibility);
        Assert.Equal(
            ((SolidColorBrush)shortcutInput.FindResource("Brush.Status.Error")).Color,
            Assert.IsType<SolidColorBrush>(errorText.Foreground).Color);
        Assert.Equal("打开闪念", peer.GetName());
        Assert.Equal("快捷键冲突", peer.GetHelpText());

        shortcutInput.IsEnabled = false;
        PumpDispatcher();
        Assert.Equal(
            ((SolidColorBrush)shortcutInput.FindResource("Brush.Text.Disabled")).Color,
            Assert.IsType<SolidColorBrush>(errorText.Foreground).Color);
        Assert.Equal("快捷键冲突", peer.GetHelpText());

        shortcutInput.IsEnabled = true;
        shortcutInput.ErrorMessage = null;
        PumpDispatcher();
        Assert.Equal(Visibility.Collapsed, errorText.Visibility);
        Assert.Equal("使用快捷键打开闪念", peer.GetHelpText());

        AutomationProperties.SetName(shortcutInput, string.Empty);
        PumpDispatcher();
        Assert.Equal("快捷键输入", peer.GetName());
    }

    private static ShortcutInputVisualHost LoadShortcutInputVisual()
    {
        var document = XDocument.Load(ComponentPath("ShortcutInput.xaml"), LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("ShortcutInput.xaml 缺少根元素。");
        var presentationNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var testNamespace = XNamespace.Get("clr-namespace:QuickPhrase.Desktop.Tests.Theme;assembly=QuickPhrase.Desktop.Tests");

        root.Add(new XAttribute(XNamespace.Xmlns + "test", testNamespace.NamespaceName));
        root.Name = testNamespace + nameof(ShortcutInputVisualHost);
        root.Attribute(XamlNamespace + "Class")?.Remove();
        root.Attribute("Style")?.Remove();
        root.Descendants().Attributes("Click").Remove();
        root.Descendants().Attributes("PreviewKeyDown").Remove();
        root.AddFirst(
            new XElement(
                testNamespace + $"{nameof(ShortcutInputVisualHost)}.Resources",
                new XElement(
                    presentationNamespace + "ResourceDictionary",
                    new XElement(
                        presentationNamespace + "ResourceDictionary.MergedDictionaries",
                        RuntimeResourcePaths.Select(path =>
                            new XElement(
                                presentationNamespace + "ResourceDictionary",
                                new XAttribute("Source", $"/QuickPhrase;component/{path}")))),
                    CreateRuntimeStyle(presentationNamespace, "Style.Button.Secondary", "Button"),
                    CreateRuntimeStyle(presentationNamespace, "Style.Keycap", "Border"),
                    CreateRuntimeStyle(presentationNamespace, "Style.Text.Mono", "TextBlock"),
                    CreateRuntimeStyle(presentationNamespace, "Style.Text.Body.Medium", "TextBlock"),
                    CreateRuntimeStyle(presentationNamespace, "Style.Text.Caption", "TextBlock"))));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
        var shortcutInput = Assert.IsType<ShortcutInputVisualHost>(XamlReader.Load(stream));
        Assert.Equal(RuntimeResourcePaths.Length, shortcutInput.Resources.MergedDictionaries.Count);
        return shortcutInput;
    }

    private static XElement CreateRuntimeStyle(XNamespace presentationNamespace, string key, string targetType) =>
        new(
            presentationNamespace + "Style",
            new XAttribute(XamlNamespace + "Key", key),
            new XAttribute("TargetType", targetType));

    [Fact]
    public void PhraseAndCategoryComponents_RemainVirtualizationFriendlyContentOnlyControls()
    {
        foreach (var fileName in new[] { "PhraseResultItem.xaml", "CategoryTreeItem.xaml" })
        {
            var document = XDocument.Load(ComponentPath(fileName));
            Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "ItemsControl");
            Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "ListBox");
            Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "ListView");
            Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "TreeView");
        }

        AssertDependencyProperty(PhraseResultItem.TitleProperty, typeof(string), nameof(PhraseResultItem.Title));
        AssertDependencyProperty(PhraseResultItem.DescriptionProperty, typeof(string), nameof(PhraseResultItem.Description));
        AssertDependencyProperty(PhraseResultItem.MetadataProperty, typeof(string), nameof(PhraseResultItem.Metadata));
        AssertDependencyProperty(PhraseResultItem.TrailingContentProperty, typeof(object), nameof(PhraseResultItem.TrailingContent));

        AssertDependencyProperty(CategoryTreeItem.TitleProperty, typeof(string), nameof(CategoryTreeItem.Title));
        AssertDependencyProperty(CategoryTreeItem.CountTextProperty, typeof(string), nameof(CategoryTreeItem.CountText));
        AssertDependencyProperty(CategoryTreeItem.LeadingContentProperty, typeof(object), nameof(CategoryTreeItem.LeadingContent));
        AssertDependencyProperty(CategoryTreeItem.TrailingContentProperty, typeof(object), nameof(CategoryTreeItem.TrailingContent));
    }

    [Fact]
    public void ComponentXaml_ContainsNoHexAndUsesApprovedResourceLookupModes()
    {
        var hexPattern = new Regex("#[0-9a-fA-F]{3,8}(?![0-9a-fA-F])", RegexOptions.CultureInvariant);
        var staticThemePattern = new Regex(@"\{StaticResource\s+(?:Color\.|Brush\.|Effect\.Shadow\.)", RegexOptions.CultureInvariant);
        var dynamicMetricPattern = new Regex(@"\{DynamicResource\s+(?:Typography\.|Thickness\.|Radius\.|Size\.|Motion\.)", RegexOptions.CultureInvariant);

        foreach (var fileName in ComponentXamlFiles)
        {
            var xaml = File.ReadAllText(ComponentPath(fileName));
            Assert.DoesNotMatch(hexPattern, xaml);
            Assert.DoesNotMatch(staticThemePattern, xaml);
            Assert.DoesNotMatch(dynamicMetricPattern, xaml);
        }
    }

    [Fact]
    public void ComponentCode_DoesNotCallPlatformPersistenceOrShortcutServices_AndHasChineseDesignComments()
    {
        var forbidden = new[]
        {
            "RegisterHotKey", "UnregisterHotKey", "user32", "HwndSource", "SQLite", "Sqlite",
            "IShortcutService", "StageAsync", "CommitAsync", "RollbackAsync", "ILogger", "Debug.Write",
        };

        foreach (var fileName in ComponentCodeFiles)
        {
            var source = File.ReadAllText(ComponentPath(fileName));
            foreach (var term in forbidden)
                Assert.DoesNotContain(term, source, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("/// <summary>", source, StringComparison.Ordinal);
            Assert.Matches("[\\u4e00-\\u9fff]", source);
        }
    }

    private static readonly string[] RuntimeResourcePaths =
    [
        "DesignSystem/Tokens/Typography.xaml",
        "DesignSystem/Tokens/Thickness.xaml",
        "DesignSystem/Tokens/Radius.xaml",
        "DesignSystem/Tokens/Sizes.xaml",
        "DesignSystem/Tokens/Motion.xaml",
        "DesignSystem/Themes/QuickPhraseTheme.Light.xaml",
    ];

    private static WindowHost ShowInTestWindow(FrameworkElement content)
    {
        var window = new Window
        {
            Width = 480,
            Height = 320,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = content,
        };
        window.Show();
        content.ApplyTemplate();
        window.UpdateLayout();
        PumpDispatcher();
        return new WindowHost(window);
    }

    private static T FindVisualChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (TryFindVisualChild<T>(root, name) is { } match)
            return match;

        throw new Xunit.Sdk.XunitException($"找不到可视化子元素：{name}");
    }

    private static T? TryFindVisualChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && match.Name == name)
                return match;

            if (TryFindVisualChild<T>(child, name) is { } nested)
                return nested;
        }

        return null;
    }

    private static void PumpDispatcher() => Dispatcher.CurrentDispatcher.Invoke(
        static () => { },
        DispatcherPriority.ApplicationIdle);

    private sealed class WindowHost(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }

    private static void AssertCompleted(Key key, ModifierKeys modifiers, ShortcutChord expected)
    {
        var result = ShortcutInput.InterpretKey(key, modifiers);
        Assert.Equal(ShortcutCaptureAction.Complete, result.Action);
        Assert.Equal(expected, result.Chord);
    }

    private static void AssertDependencyProperty(DependencyProperty property, Type propertyType, string name)
    {
        Assert.Equal(name, property.Name);
        Assert.Equal(propertyType, property.PropertyType);
    }

    private static string ComponentPath(string fileName) => Path.Combine(FindRepoRoot(), "desktop", "QuickPhrase.Desktop", "DesignSystem", "Components", fileName);

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QuickPhrase.sln")))
            current = current.Parent;

        return current?.FullName ?? throw new DirectoryNotFoundException("找不到 QuickPhrase 仓库根目录。");
    }
}

/// <summary>
/// 仅供运行时测试加载生产 ShortcutInput.xaml 的轻量外壳。
/// 它提供生产 XAML 绑定所需的状态属性，不包含捕获、持久化或平台行为。
/// </summary>
public sealed class ShortcutInputVisualHost : UserControl
{
    public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(
        nameof(Chord),
        typeof(ShortcutChord?),
        typeof(ShortcutInputVisualHost),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsCapturingProperty = DependencyProperty.Register(
        nameof(IsCapturing),
        typeof(bool),
        typeof(ShortcutInputVisualHost),
        new PropertyMetadata(false));

    public static readonly DependencyProperty ErrorMessageProperty = DependencyProperty.Register(
        nameof(ErrorMessage),
        typeof(string),
        typeof(ShortcutInputVisualHost),
        new PropertyMetadata(null));

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

    public IReadOnlyList<string> DisplayKeys { get; } = ["Ctrl", "+", "Shift", "+", "Space"];
}
