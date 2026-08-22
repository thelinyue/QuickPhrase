using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace QuickPhrase.Desktop.Tests.Theme;

/// <summary>
/// 验证 Phase 2 WPF Style 的公开资源键、交互状态和 Design Token 使用边界。
/// 测试直接检查 XAML 真源，避免依赖共享 WPF Application 生命周期。
/// </summary>
public sealed class DesignSystemStyleTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] StyleFiles =
    {
        "Text.xaml",
        "Buttons.xaml",
        "Inputs.xaml",
        "SelectionControls.xaml",
        "Lists.xaml",
        "Surfaces.xaml",
        "Dialogs.xaml",
    };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuickPhrase.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("找不到 QuickPhrase 仓库根目录。");
    }

    private static string StylePath(string fileName) => Path.Combine(
        FindRepoRoot(),
        "desktop",
        "QuickPhrase.Desktop",
        "DesignSystem",
        "Styles",
        fileName);

    private static XDocument LoadStyleDocument(string fileName) =>
        XDocument.Load(StylePath(fileName), LoadOptions.PreserveWhitespace);

    private static XElement FindStyle(string fileName, string key)
    {
        return LoadStyleDocument(fileName)
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "Style" &&
                element.Attribute(XamlNamespace + "Key")?.Value == key);
    }

    private static IReadOnlySet<string> ReadStyleKeys(string fileName)
    {
        return LoadStyleDocument(fileName)
            .Descendants()
            .Where(element => element.Name.LocalName == "Style")
            .Select(element => element.Attribute(XamlNamespace + "Key")?.Value)
            .Where(key => key is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertHasTrigger(XElement style, string property, string value)
    {
        Assert.Contains(
            style.Descendants().Where(element => element.Name.LocalName.EndsWith("Trigger", StringComparison.Ordinal)),
            trigger =>
                trigger.Attribute("Property")?.Value == property &&
                trigger.Attribute("Value")?.Value == value);
    }


    private static void AssertHasEnabledMultiTrigger(XElement style, string property, string value = "True")
    {
        Assert.Contains(
            style.Descendants().Where(element => element.Name.LocalName == "MultiTrigger"),
            trigger =>
                trigger.Descendants().Any(condition =>
                    condition.Name.LocalName == "Condition" &&
                    condition.Attribute("Property")?.Value == property &&
                    condition.Attribute("Value")?.Value == value) &&
                trigger.Descendants().Any(condition =>
                    condition.Name.LocalName == "Condition" &&
                    condition.Attribute("Property")?.Value == "IsEnabled" &&
                    condition.Attribute("Value")?.Value == "True"));
    }

    private static ResourceDictionary CreateDesignSystemResources()
    {
        var presentationNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var root = new XElement(
            presentationNamespace + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", XamlNamespace.NamespaceName));

        foreach (var relativePath in new[]
        {
            "DesignSystem/Tokens/Typography.xaml",
            "DesignSystem/Tokens/Thickness.xaml",
            "DesignSystem/Tokens/Radius.xaml",
            "DesignSystem/Tokens/Sizes.xaml",
            "DesignSystem/Tokens/Motion.xaml",
            "DesignSystem/Tokens/Colors.xaml",
        "DesignSystem/Themes/Theme.Light.xaml",
        "DesignSystem/Tokens/Brushes.xaml",
            "DesignSystem/Styles/Text.xaml",
            "DesignSystem/Styles/Buttons.xaml",
            "DesignSystem/Styles/Inputs.xaml",
            "DesignSystem/Styles/SelectionControls.xaml",
            "DesignSystem/Styles/Lists.xaml",
            "DesignSystem/Styles/Surfaces.xaml",
            "DesignSystem/Styles/Dialogs.xaml",
        })
        {
            var path = Path.Combine(
                FindRepoRoot(),
                "desktop",
                "QuickPhrase.Desktop",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var source = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            foreach (var node in source.Root?.Nodes() ?? Enumerable.Empty<XNode>())
            {
                // 测试字典已经按生产顺序展平全部依赖；忽略源字典的合并节点，
                // 避免相对 Source 脱离原文件 BaseUri 后被错误解析到程序集根目录。
                if (node is XElement element && element.Name.LocalName == "ResourceDictionary.MergedDictionaries")
                    continue;

                root.Add(node is XElement resource ? new XElement(resource) : node);
            }
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new XDocument(root).ToString()));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static Window ShowInTestWindow(FrameworkElement content, ResourceDictionary resources)
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
        window.Resources = resources;
        window.Show();
        content.ApplyTemplate();
        window.UpdateLayout();
        PumpDispatcher();
        return window;
    }

    private static void PumpDispatcher()
    {
        Dispatcher.CurrentDispatcher.Invoke(
            static () => { },
            DispatcherPriority.ApplicationIdle);
    }

    private static T? FindVisualDescendant<T>(DependencyObject? root, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        if (root is null)
            return null;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed && (predicate is null || predicate(typed)))
                return typed;

            var descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    private static void MarkInvalid(DependencyObject target, DependencyProperty property)
    {
        var expression = BindingOperations.SetBinding(
            target,
            property,
            new Binding(nameof(ValidationProbe.Value))
            {
                Source = new ValidationProbe(),
            });
        Validation.MarkInvalid(
            expression,
            new ValidationError(new ExceptionValidationRule(), expression, "测试校验错误", null));
    }

    private static void AssertBrushColor(FrameworkElement element, string resourceKey, Brush? actual)
    {
        var expected = Assert.IsType<SolidColorBrush>(element.FindResource(resourceKey));
        var actualBrush = Assert.IsType<SolidColorBrush>(actual);
        Assert.Equal(expected.Color, actualBrush.Color);
    }

    [Fact]
    public void RequiredPublicStyleKeys_AreExposed()
    {
        var requiredByFile = new Dictionary<string, string[]>
        {
            ["Text.xaml"] = new[]
            {
                "Style.Text.Title.Large", "Style.Text.Title.Medium", "Style.Text.Title.Small",
                "Style.Text.Body.Large", "Style.Text.Body.Medium", "Style.Text.Body.Small",
                "Style.Text.Caption", "Style.Text.Label", "Style.Text.Mono",
            },
            ["Buttons.xaml"] = new[]
            {
                "Style.Button.Primary", "Style.Button.Secondary", "Style.Button.Ghost", "Style.Button.Danger",
                "Style.Button.Primary.Compact", "Style.Button.Secondary.Compact",
                "Style.Button.Ghost.Compact", "Style.Button.Danger.Compact", "Style.Button.Icon",
            },
            ["Inputs.xaml"] = new[]
            {
                "Style.Input.Default", "Style.Input.Search", "Style.Select.Default",
            },
            ["SelectionControls.xaml"] = new[] { "Style.Switch.Default" },
            ["Lists.xaml"] = new[] { "Style.ListItem.Navigation", "Style.ListItem.Phrase" },
            ["Surfaces.xaml"] = new[]
            {
                "Style.Card.Default", "Style.Card.Elevated", "Style.Popup.Surface",
                "Style.Setting.Group", "Style.Setting.Row", "Style.Keycap", "Style.Toast.Surface",
            },
            ["Dialogs.xaml"] = new[]
            {
                "Style.Dialog.Window", "Style.Menu.Context", "Style.Menu.Item",
                "Style.Menu.Item.Danger", "Style.Menu.Separator",
            },
        };

        foreach (var pair in requiredByFile)
        {
            var keys = ReadStyleKeys(pair.Key);
            foreach (var requiredKey in pair.Value)
                Assert.Contains(requiredKey, keys);
        }
    }

    [Fact]
    public void TextStyles_ConsumeTypographyTokens()
    {
        var document = LoadStyleDocument("Text.xaml");
        var xaml = document.ToString(SaveOptions.DisableFormatting);

        foreach (var role in new[]
        {
            "Title.Large", "Title.Medium", "Title.Small",
            "Body.Large", "Body.Medium", "Body.Small", "Caption", "Label", "Mono",
        })
        {
            Assert.Contains($"{{StaticResource Typography.{role}.FontSize}}", xaml, StringComparison.Ordinal);
            Assert.Contains($"{{StaticResource Typography.{role}.FontWeight}}", xaml, StringComparison.Ordinal);
            Assert.Contains($"{{StaticResource Typography.{role}.LineHeight}}", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ButtonTemplate_CoversAllStates_AndDoesNotDimWholeControlWhenDisabled()
    {
        var style = FindStyle("Buttons.xaml", "Style.Button.Base");

        AssertHasTrigger(style, "IsMouseOver", "True");
        AssertHasTrigger(style, "IsPressed", "True");
        AssertHasTrigger(style, "IsEnabled", "False");
        AssertHasTrigger(style, "IsKeyboardFocused", "True");

        var disabledTriggers = style.Descendants()
            .Where(element =>
                element.Name.LocalName == "Trigger" &&
                element.Attribute("Property")?.Value == "IsEnabled" &&
                element.Attribute("Value")?.Value == "False");

        Assert.All(disabledTriggers, trigger =>
            Assert.DoesNotContain(
                trigger.Descendants().Where(element => element.Name.LocalName == "Setter"),
                setter =>
                    setter.Attribute("Property")?.Value == "Opacity" &&
                    (setter.Attribute("TargetName") is null || setter.Attribute("TargetName")?.Value == "Root")));
    }

    [Fact]
    public void ButtonVariantHoverAndPressedStates_DoNotOverrideDisabledState()
    {
        foreach (var key in new[] { "Style.Button.Primary", "Style.Button.Secondary", "Style.Button.Ghost", "Style.Button.Danger" })
        {
            var style = FindStyle("Buttons.xaml", key);
            foreach (var stateProperty in new[] { "IsMouseOver", "IsPressed" })
            {
                Assert.Contains(
                    style.Descendants().Where(element => element.Name.LocalName == "MultiTrigger"),
                    trigger =>
                        trigger.Descendants().Any(condition =>
                            condition.Name.LocalName == "Condition" &&
                            condition.Attribute("Property")?.Value == stateProperty &&
                            condition.Attribute("Value")?.Value == "True") &&
                        trigger.Descendants().Any(condition =>
                            condition.Name.LocalName == "Condition" &&
                            condition.Attribute("Property")?.Value == "IsEnabled" &&
                            condition.Attribute("Value")?.Value == "True"));
            }
        }
    }


    [Fact]
    public void ButtonVariants_Use36And32PixelSizeTokens()
    {
        var defaultBase = FindStyle("Buttons.xaml", "Style.Button.Base").ToString(SaveOptions.DisableFormatting);
        var compactBase = FindStyle("Buttons.xaml", "Style.Button.Base.Compact").ToString(SaveOptions.DisableFormatting);

        Assert.Contains("{StaticResource Size.Control.Default}", defaultBase, StringComparison.Ordinal);
        Assert.Contains("{StaticResource Thickness.Control.Button.Default}", defaultBase, StringComparison.Ordinal);
        Assert.Contains("{StaticResource Size.Control.Compact}", compactBase, StringComparison.Ordinal);
        Assert.Contains("{StaticResource Thickness.Control.Button.Compact}", compactBase, StringComparison.Ordinal);
        Assert.Contains("{StaticResource Typography.Body.Large.FontSize}", defaultBase, StringComparison.Ordinal);
        Assert.Contains("{StaticResource Radius.Control}", defaultBase, StringComparison.Ordinal);
    }

    [Fact]
    public void TextBoxTemplate_UsesPaddingAtContentHost_AndCoversFocusErrorAndDisabled()
    {
        var style = FindStyle("Inputs.xaml", "Style.Input.Base");
        var xaml = style.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("x:Name=\"PART_ContentHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"{TemplateBinding Padding}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{StaticResource Thickness.Control.Input}", xaml, StringComparison.Ordinal);
        AssertHasEnabledMultiTrigger(style, "IsKeyboardFocused");
        AssertHasEnabledMultiTrigger(style, "Validation.HasError");
        AssertHasTrigger(style, "IsEnabled", "False");
    }

    [Fact]
    public void InputInteractiveStates_RequireEnabled_AndDisabledDirectlyOverridesTemplateParts()
    {
        var textBox = FindStyle("Inputs.xaml", "Style.Input.Base");
        foreach (var property in new[] { "IsMouseOver", "IsKeyboardFocused", "Validation.HasError" })
            AssertHasEnabledMultiTrigger(textBox, property);

        var select = FindStyle("Inputs.xaml", "Style.Select.Default");
        foreach (var property in new[] { "IsMouseOver", "IsKeyboardFocusWithin", "IsDropDownOpen", "Validation.HasError" })
            AssertHasEnabledMultiTrigger(select, property);

        foreach (var style in new[] { textBox, select })
        {
            var disabled = style.Descendants().Single(element =>
                element.Name.LocalName == "Trigger" &&
                element.Attribute("Property")?.Value == "IsEnabled" &&
                element.Attribute("Value")?.Value == "False");
            Assert.Contains(disabled.Descendants(), setter =>
                setter.Name.LocalName == "Setter" &&
                setter.Attribute("TargetName")?.Value == "Root" &&
                setter.Attribute("Property")?.Value == "Background");
            Assert.Contains(disabled.Descendants(), setter =>
                setter.Name.LocalName == "Setter" &&
                setter.Attribute("TargetName")?.Value == "Root" &&
                setter.Attribute("Property")?.Value == "BorderBrush");
            Assert.Contains(disabled.Descendants(), setter =>
                setter.Name.LocalName == "Setter" &&
                setter.Attribute("TargetName")?.Value == "FocusRing" &&
                setter.Attribute("Property")?.Value == "Visibility" &&
                setter.Attribute("Value")?.Value == "Collapsed");
        }
    }

    [Fact]
    public void SelectTemplate_CoversFocusWithinErrorAndDisabled()
    {
        var style = FindStyle("Inputs.xaml", "Style.Select.Default");

        AssertHasEnabledMultiTrigger(style, "IsKeyboardFocusWithin");
        AssertHasEnabledMultiTrigger(style, "Validation.HasError");
        AssertHasTrigger(style, "IsEnabled", "False");
        Assert.Contains("PART_Popup", style.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal);
    }

    [Fact]
    public void SelectTemplate_UsesItemsPresenterVirtualizingPanelAndMaxDropDownHeight()
    {
        var style = FindStyle("Inputs.xaml", "Style.Select.Default");
        var xaml = style.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("<ItemsPanelTemplate", xaml, StringComparison.Ordinal);
        Assert.Contains("<VirtualizingStackPanel", xaml, StringComparison.Ordinal);
        Assert.Contains("<ItemsPresenter", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<StackPanel IsItemsHost=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"{TemplateBinding MaxDropDownHeight}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"{StaticResource Size.Select.DropDownArrow.Width}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"{StaticResource Size.Select.DropDownArrow.Height}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"8\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"5\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuItemStyle_PreservesNativeRoleAwareSubmenuTemplate()
    {
        var style = FindStyle("Dialogs.xaml", "Style.Menu.Item");
        Assert.DoesNotContain(
            style.Elements().Where(element => element.Name.LocalName == "Setter"),
            setter => setter.Attribute("Property")?.Value == "Template");
    }

    [Fact]
    public void Runtime_CompactPhraseRowTemplate_AlignsTitleAndContentColumns()
    {
        WpfTestApplicationHost.Invoke(application =>
        {
            var template = Assert.IsType<DataTemplate>(application.FindResource("Template.Phrase.CompactRow"));
            var listBox = new ListBox
            {
                ItemTemplate = template,
                ItemsSource = new[]
                {
                    new
                    {
                        Title = "标题",
                        Content = "正文",
                    },
                },
            };

            var window = ShowInTestWindow(listBox, new ResourceDictionary());
            try
            {
                var root = Assert.IsType<Grid>(FindVisualDescendant<Grid>(
                    listBox,
                    grid => grid.Name == "RowRoot"));
                Assert.Equal(GridLength.Auto, root.ColumnDefinitions[0].Width);
                Assert.Equal(new GridLength(4), root.ColumnDefinitions[1].Width);
                Assert.Equal(new GridLength(1, GridUnitType.Star), root.ColumnDefinitions[2].Width);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Runtime_ComboBoxDisplayMemberPath_RendersSelectedProperty()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var resources = CreateDesignSystemResources();
            var category = new CategoryDisplayProbe("客户跟进");
            var comboBox = new ComboBox
            {
                Style = Assert.IsType<Style>(resources["Style.Select.Default"]),
                ItemsSource = new[] { category },
                DisplayMemberPath = nameof(CategoryDisplayProbe.Name),
                SelectedIndex = 0,
            };
            var window = ShowInTestWindow(comboBox, resources);
            try
            {
                comboBox.ApplyTemplate();
                comboBox.UpdateLayout();
                PumpDispatcher();

                var presenter = Assert.IsType<ContentPresenter>(FindVisualDescendant<ContentPresenter>(
                    comboBox,
                    candidate => ReferenceEquals(candidate.Content, category)));
                var text = Assert.IsType<TextBlock>(FindVisualDescendant<TextBlock>(presenter));
                Assert.Equal(category.Name, text.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Runtime_ComboBoxTemplate_PreservesSelectionPopupItemsHostAndMaxHeight()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var resources = CreateDesignSystemResources();
            var comboBox = new ComboBox
            {
                Style = Assert.IsType<Style>(resources["Style.Select.Default"]),
                ItemsSource = Enumerable.Range(1, 200).Select(index => $"第 {index} 项").ToArray(),
                SelectedIndex = 1,
                MaxDropDownHeight = 123,
            };
            var window = ShowInTestWindow(comboBox, resources);
            try
            {
                var popup = Assert.IsType<Popup>(comboBox.Template.FindName("PART_Popup", comboBox));
                Assert.NotNull(FindVisualDescendant<ContentPresenter>(
                    comboBox,
                    presenter => Equals(presenter.Content, "第 2 项")));

                comboBox.IsDropDownOpen = true;
                comboBox.UpdateLayout();
                PumpDispatcher();

                Assert.True(popup.IsOpen);
                var itemsPresenter = Assert.IsType<ItemsPresenter>(FindVisualDescendant<ItemsPresenter>(popup.Child));
                itemsPresenter.ApplyTemplate();
                comboBox.UpdateLayout();
                PumpDispatcher();

                var virtualizingPanel = Assert.IsType<VirtualizingStackPanel>(
                    FindVisualDescendant<VirtualizingStackPanel>(popup.Child));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(comboBox));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(comboBox));
                Assert.IsType<ScrollViewer>(FindVisualDescendant<ScrollViewer>(popup.Child));

                var popupContent = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
                var popupBorder = Assert.IsType<Border>(FindVisualDescendant<Border>(popupContent));
                Assert.Equal(new Thickness(12), popupBorder.Padding);
                Assert.Equal(new Thickness(1), popupBorder.BorderThickness);
                Assert.Same(virtualizingPanel, FindVisualDescendant<VirtualizingStackPanel>(itemsPresenter));

                // Popup.Child 的 ActualHeight 不包含外部 Margin，必须检查独立弹出窗口的完整根视觉。
                var presentationSource = PresentationSource.FromVisual(popupContent);
                Assert.NotNull(presentationSource);
                var popupRoot = Assert.IsAssignableFrom<FrameworkElement>(presentationSource.RootVisual);
                Assert.True(
                    popupRoot.ActualHeight <= comboBox.MaxDropDownHeight,
                    $"ComboBox 完整 Popup 根视觉高度 {popupRoot.ActualHeight} 超过 MaxDropDownHeight {comboBox.MaxDropDownHeight}。");
            }
            finally
            {
                comboBox.IsDropDownOpen = false;
                window.Close();
            }
        });
    }

    [Fact]
    public void Runtime_MenuItemWithChildren_GeneratesSubmenuContainers()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var resources = CreateDesignSystemResources();
            var itemStyle = Assert.IsType<Style>(resources["Style.Menu.Item"]);
            var parent = new MenuItem { Header = "父菜单", Style = itemStyle };
            parent.Items.Add(new MenuItem { Header = "子菜单", Style = itemStyle });
            var contextMenu = new ContextMenu
            {
                Style = Assert.IsType<Style>(resources["Style.Menu.Context"]),
            };
            contextMenu.Items.Add(parent);
            var placementTarget = new Button { Content = "打开菜单", ContextMenu = contextMenu };
            var window = ShowInTestWindow(placementTarget, resources);
            try
            {
                contextMenu.PlacementTarget = placementTarget;
                contextMenu.IsOpen = true;
                PumpDispatcher();
                parent.ApplyTemplate();
                parent.IsSubmenuOpen = true;
                PumpDispatcher();

                Assert.Equal(MenuItemRole.SubmenuHeader, parent.Role);
                Assert.IsType<Popup>(parent.Template.FindName("PART_Popup", parent));
                Assert.NotNull(parent.ItemContainerGenerator.ContainerFromIndex(0));
            }
            finally
            {
                parent.IsSubmenuOpen = false;
                contextMenu.IsOpen = false;
                window.Close();
            }
        });
    }

    [Fact]
    public void Runtime_DisabledValidationAndCheckedCombinationsUseDisabledVisuals()
    {
        WpfTestApplicationHost.Invoke(_ =>
        {
            var resources = CreateDesignSystemResources();
            var panel = new StackPanel();
            var textBox = new TextBox { Style = Assert.IsType<Style>(resources["Style.Input.Default"]) };
            var comboBox = new ComboBox
            {
                Style = Assert.IsType<Style>(resources["Style.Select.Default"]),
                ItemsSource = new[] { "项目" },
                SelectedIndex = 0,
            };
            var switchControl = new CheckBox
            {
                Style = Assert.IsType<Style>(resources["Style.Switch.Default"]),
                IsChecked = true,
            };
            panel.Children.Add(textBox);
            panel.Children.Add(comboBox);
            panel.Children.Add(switchControl);
            var window = ShowInTestWindow(panel, resources);
            try
            {
                MarkInvalid(textBox, TextBox.TextProperty);
                MarkInvalid(comboBox, Selector.SelectedValueProperty);
                textBox.IsEnabled = false;
                comboBox.IsEnabled = false;
                switchControl.IsEnabled = false;
                textBox.ApplyTemplate();
                comboBox.ApplyTemplate();
                switchControl.ApplyTemplate();
                window.UpdateLayout();
                PumpDispatcher();

                var textRoot = Assert.IsType<Border>(textBox.Template.FindName("Root", textBox));
                var textFocusRing = Assert.IsType<Border>(textBox.Template.FindName("FocusRing", textBox));
                AssertBrushColor(textBox, "Brush.Surface.Hover", textRoot.Background);
                AssertBrushColor(textBox, "Brush.Border.Default", textRoot.BorderBrush);
                Assert.Equal(Visibility.Collapsed, textFocusRing.Visibility);

                var comboRoot = Assert.IsType<Border>(comboBox.Template.FindName("Root", comboBox));
                var comboFocusRing = Assert.IsType<Border>(comboBox.Template.FindName("FocusRing", comboBox));
                AssertBrushColor(comboBox, "Brush.Surface.Hover", comboRoot.Background);
                AssertBrushColor(comboBox, "Brush.Border.Default", comboRoot.BorderBrush);
                Assert.Equal(Visibility.Collapsed, comboFocusRing.Visibility);

                var track = Assert.IsType<Border>(switchControl.Template.FindName("Track", switchControl));
                var thumb = Assert.IsType<System.Windows.Shapes.Ellipse>(switchControl.Template.FindName("Thumb", switchControl));
                var switchFocusRing = Assert.IsType<Border>(switchControl.Template.FindName("FocusRing", switchControl));
                AssertBrushColor(switchControl, "Brush.Surface.Hover", track.Background);
                AssertBrushColor(switchControl, "Brush.Border.Default", track.BorderBrush);
                AssertBrushColor(switchControl, "Brush.Text.Disabled", thumb.Fill);
                Assert.Equal(HorizontalAlignment.Right, thumb.HorizontalAlignment);
                Assert.Equal(Visibility.Collapsed, switchFocusRing.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SwitchTemplate_UsesApprovedGeometryAndCoversAllStates()
    {
        var style = FindStyle("SelectionControls.xaml", "Style.Switch.Default");
        var xaml = style.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("{StaticResource Size.Switch.Width}", xaml, StringComparison.Ordinal);
        Assert.Contains("{StaticResource Size.Switch.Height}", xaml, StringComparison.Ordinal);
        Assert.Contains("{StaticResource Size.Switch.Thumb}", xaml, StringComparison.Ordinal);
        AssertHasTrigger(style, "IsMouseOver", "True");
        AssertHasTrigger(style, "IsChecked", "True");
        AssertHasTrigger(style, "IsPressed", "True");
        AssertHasTrigger(style, "IsEnabled", "False");
        AssertHasTrigger(style, "IsKeyboardFocused", "True");
    }

    [Fact]
    public void ListItemStyles_RemainVirtualizationFriendly()
    {
        var document = LoadStyleDocument("Lists.xaml");
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "ItemsControl");

        foreach (var key in new[] { "Style.ListItem.Navigation", "Style.ListItem.Phrase" })
        {
            var style = FindStyle("Lists.xaml", key);
            AssertHasTrigger(style, "IsMouseOver", "True");
            AssertHasTrigger(style, "IsSelected", "True");
            AssertHasTrigger(style, "IsKeyboardFocused", "True");
            AssertHasTrigger(style, "IsEnabled", "False");
        }
    }

    [Fact]
    public void SurfaceAndDialogShadows_AreLimitedToApprovedElevatedPopupAndDialogStyles()
    {
        var defaultCard = FindStyle("Surfaces.xaml", "Style.Card.Default");
        Assert.DoesNotContain(
            defaultCard.Descendants().Where(element => element.Name.LocalName == "Setter"),
            setter => setter.Attribute("Property")?.Value == "Effect");

        Assert.Contains("{DynamicResource Effect.Shadow.Elevated}", FindStyle("Surfaces.xaml", "Style.Card.Elevated").ToString(), StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Effect.Shadow.Popup}", FindStyle("Surfaces.xaml", "Style.Popup.Surface").ToString(), StringComparison.Ordinal);
        Assert.Contains("{DynamicResource Effect.Shadow.Dialog}", FindStyle("Dialogs.xaml", "Style.Dialog.Window").ToString(), StringComparison.Ordinal);

        var allowedShadowStyles = new HashSet<string>(StringComparer.Ordinal)
        {
            "Style.Card.Elevated",
            "Style.Popup.Surface",
            "Style.Dialog.Window",
            "Style.Menu.Context",
        };

        foreach (var fileName in new[] { "Surfaces.xaml", "Dialogs.xaml" })
        {
            foreach (var style in LoadStyleDocument(fileName).Descendants().Where(element => element.Name.LocalName == "Style"))
            {
                var usesShadow = style.Descendants()
                    .Any(element => element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value == "Effect");
                if (usesShadow)
                    Assert.Contains(style.Attribute(XamlNamespace + "Key")?.Value ?? string.Empty, allowedShadowStyles);
            }
        }
    }

    [Fact]
    public void StyleDictionaries_ContainNoHex_AndUseCorrectResourceLookupMode()
    {
        var hexPattern = new Regex("#[0-9a-fA-F]{3,8}(?![0-9a-fA-F])", RegexOptions.CultureInvariant);
        var staticThemePattern = new Regex(@"\{StaticResource\s+(?:Color\.|Brush\.|Effect\.Shadow\.)", RegexOptions.CultureInvariant);
        var dynamicMetricPattern = new Regex(@"\{DynamicResource\s+(?:Typography\.|Thickness\.|Radius\.|Size\.|Motion\.)", RegexOptions.CultureInvariant);

        foreach (var fileName in StyleFiles)
        {
            var xaml = File.ReadAllText(StylePath(fileName));
            Assert.DoesNotMatch(hexPattern, xaml);
            Assert.DoesNotMatch(staticThemePattern, xaml);
            Assert.DoesNotMatch(dynamicMetricPattern, xaml);
        }
    }

    private sealed record CategoryDisplayProbe(string Name);

    private sealed class ValidationProbe
    {
        public string Value { get; set; } = string.Empty;
    }
}
