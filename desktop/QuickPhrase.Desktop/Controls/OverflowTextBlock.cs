using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfToolTip = System.Windows.Controls.ToolTip;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace QuickPhrase.Desktop.Controls;

/// <summary>
/// 话术列表中的单行溢出文本控件。
///
/// 设计目标是让列表保持紧凑固定行高，同时不牺牲长文案的可读性：
/// 控件使用实际字体和可用宽度测量文本，只有确认发生视觉溢出时，
/// 才启用省略号、键盘焦点和完整内容浮层。这样短文案不会产生多余的
/// Tooltip 或 Tab 停留点，也避免用固定字符数推断不同字体下的布局结果。
/// </summary>
public sealed class OverflowTextBlock : TextBlock
{
    private static readonly DependencyPropertyKey HasOverflowPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasOverflow),
            typeof(bool),
            typeof(OverflowTextBlock),
            new FrameworkPropertyMetadata(false));

    private TextBlock? _tooltipText;
    private bool _updateQueued;
    private int _tooltipAnimationVersion;
    public static readonly DependencyProperty HasOverflowProperty = HasOverflowPropertyKey.DependencyProperty;

    /// <summary>
    /// 当前文本是否超出控件的单行可视宽度。
    /// </summary>
    public bool HasOverflow => (bool)GetValue(HasOverflowProperty);

    public OverflowTextBlock()
    {
        TextWrapping = TextWrapping.NoWrap;
        TextTrimming = TextTrimming.CharacterEllipsis;
        Focusable = false;
        KeyboardNavigation.SetIsTabStop(this, false);
        SubscribeToLayoutInputs();

        Loaded += (_, _) => QueueOverflowUpdate();
        Unloaded += (_, _) => CloseTooltip(immediate: true);
        GotKeyboardFocus += OnGotKeyboardFocus;
        LostKeyboardFocus += OnLostKeyboardFocus;
        MouseLeave += OnMouseLeave;
    }

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        QueueOverflowUpdate();
    }

    private void SubscribeToLayoutInputs()
    {
        // 这些属性会改变实际排版宽度或浮层的字体外观，使控件实例在列表
        // 虚拟化和回收期间仍能稳定地响应尺寸、字体和文本变化。
        foreach (var property in new[]
        {
            TextProperty,
            FontFamilyProperty,
            FontSizeProperty,
            FontStretchProperty,
            FontStyleProperty,
            FontWeightProperty,
            TextDecorationsProperty,
            PaddingProperty,
            FlowDirectionProperty,
            ForegroundProperty,
        })
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, typeof(OverflowTextBlock));
            descriptor?.AddValueChanged(this, (_, _) => QueueOverflowUpdate());
        }
    }
    private void QueueOverflowUpdate()
    {
        if (_updateQueued || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _updateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _updateQueued = false;
            UpdateOverflowState();
        }));
    }

    private void UpdateOverflowState()
    {
        var availableWidth = Math.Max(0, (ActualWidth > 0 ? ActualWidth : RenderSize.Width) - Padding.Left - Padding.Right);
        var isOverflowing = availableWidth > 0 && OverflowTextMeasure.IsOverflowing(this, availableWidth);

        SetValue(HasOverflowPropertyKey, isOverflowing);
        Focusable = isOverflowing;
        KeyboardNavigation.SetIsTabStop(this, isOverflowing);
        AutomationProperties.SetName(this, Text ?? string.Empty);
        AutomationProperties.SetHelpText(
            this,
            isOverflowing
                ? $"内容已截断，可悬停或聚焦查看完整文案：{Text}"
                : string.Empty);

        if (isOverflowing)
        {
            EnsureTooltip();
        }
        else
        {
            CloseTooltip(immediate: true);
            ToolTip = null;
            _tooltipText = null;
        }
    }

    private void EnsureTooltip()
    {
        if (ToolTip is WpfToolTip existingTooltip)
        {
            UpdateTooltipContent(existingTooltip);
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(1, workArea.Width - 32);
        var maxHeight = Math.Max(1, workArea.Height - 32);
        var tooltipText = new TextBlock();
        _tooltipText = tooltipText;

        var scrollHost = new ScrollViewer
        {
            Content = tooltipText,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var border = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Background = TryFindResource("SurfaceBrush") as WpfBrush ?? WpfBrushes.White,
            BorderBrush = TryFindResource("DividerBrush") as WpfBrush ?? WpfBrushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = scrollHost,
        };

        var tooltip = new WpfToolTip
        {
            Content = border,
            PlacementTarget = this,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = PlaceTooltip,
            StaysOpen = false,
            HasDropShadow = true,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
        };
        tooltip.Opened += (_, _) => FadeInTooltip(tooltip);
        UpdateTooltipContent(tooltip);

        // ToolTip 的自定义候选位置按控件附近的可用空间排序；Popup 宿主会继续
        // 处理工作区边缘修正，避免完整文案被窗口或屏幕边界裁掉。
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetShowDuration(this, 30000);
        ToolTipService.SetPlacement(this, PlacementMode.Custom);
        ToolTipService.SetShowsToolTipOnKeyboardFocus(this, true);

        ToolTip = tooltip;
    }

    private void UpdateTooltipContent(WpfToolTip tooltip)
    {
        if (_tooltipText is null)
        {
            return;
        }

        _tooltipText.Text = Text ?? string.Empty;
        _tooltipText.TextWrapping = TextWrapping.Wrap;
        _tooltipText.TextTrimming = TextTrimming.None;
        _tooltipText.FontFamily = FontFamily;
        _tooltipText.FontSize = FontSize;
        _tooltipText.FontStretch = FontStretch;
        _tooltipText.FontStyle = FontStyle;
        _tooltipText.FontWeight = FontWeight;
        _tooltipText.TextDecorations = TextDecorations;
        _tooltipText.Foreground = Foreground;
        _tooltipText.MaxWidth = Math.Max(1, tooltip.MaxWidth - 32);
    }

    private CustomPopupPlacement[] PlaceTooltip(System.Windows.Size popupSize, System.Windows.Size targetSize, System.Windows.Point offset)
    {
        const double gap = 6;
        var centeredX = (targetSize.Width - popupSize.Width) / 2;
        var centeredY = (targetSize.Height - popupSize.Height) / 2;

        try
        {
            var targetTopLeft = PointToScreen(new System.Windows.Point(0, 0));
            var workArea = SystemParameters.WorkArea;
            var spaceBelow = workArea.Bottom - targetTopLeft.Y - targetSize.Height;
            var spaceAbove = targetTopLeft.Y - workArea.Top;
            var preferBelow = spaceBelow >= popupSize.Height + gap || spaceBelow >= spaceAbove;

            return preferBelow
                ?
                [
                    new CustomPopupPlacement(new System.Windows.Point(centeredX, targetSize.Height + gap), PopupPrimaryAxis.Horizontal),
                    new CustomPopupPlacement(new System.Windows.Point(centeredX, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal),
                    new CustomPopupPlacement(new System.Windows.Point(targetSize.Width + gap, centeredY), PopupPrimaryAxis.Vertical),
                    new CustomPopupPlacement(new System.Windows.Point(-popupSize.Width - gap, centeredY), PopupPrimaryAxis.Vertical),
                ]
                :
                [
                    new CustomPopupPlacement(new System.Windows.Point(centeredX, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal),
                    new CustomPopupPlacement(new System.Windows.Point(centeredX, targetSize.Height + gap), PopupPrimaryAxis.Horizontal),
                    new CustomPopupPlacement(new System.Windows.Point(targetSize.Width + gap, centeredY), PopupPrimaryAxis.Vertical),
                    new CustomPopupPlacement(new System.Windows.Point(-popupSize.Width - gap, centeredY), PopupPrimaryAxis.Vertical),
                ];
        }
        catch (InvalidOperationException)
        {
            return
            [
                new CustomPopupPlacement(new System.Windows.Point(centeredX, targetSize.Height + gap), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new System.Windows.Point(centeredX, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal),
            ];
        }
    }

    private void OnGotKeyboardFocus(object sender, RoutedEventArgs e)
    {
        if (!HasOverflow || ToolTip is not WpfToolTip tooltip)
        {
            return;
        }

        CancelTooltipAnimation(tooltip);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsKeyboardFocusWithin && HasOverflow)
            {
                tooltip.IsOpen = true;
            }
        }));
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsMouseOver)
        {
            CloseTooltip();
        }
    }

    private void OnMouseLeave(object sender, WpfMouseEventArgs e)
    {
        if (!IsKeyboardFocusWithin)
        {
            CloseTooltip();
        }
    }

    private void FadeInTooltip(WpfToolTip tooltip)
    {
        CancelTooltipAnimation(tooltip);
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase(),
        };
        tooltip.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void CloseTooltip(bool immediate = false)
    {
        if (ToolTip is not WpfToolTip tooltip)
        {
            return;
        }

        CancelTooltipAnimation(tooltip);
        if (immediate || !tooltip.IsOpen)
        {
            tooltip.IsOpen = false;
            tooltip.Opacity = 1;
            return;
        }

        var animationVersion = ++_tooltipAnimationVersion;
        var animation = new DoubleAnimation(tooltip.Opacity, 0, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new QuadraticEase(),
        };
        animation.Completed += (_, _) =>
        {
            if (animationVersion != _tooltipAnimationVersion || IsMouseOver || IsKeyboardFocusWithin)
            {
                return;
            }

            tooltip.IsOpen = false;
            tooltip.BeginAnimation(UIElement.OpacityProperty, null);
            tooltip.Opacity = 1;
        };
        tooltip.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void CancelTooltipAnimation(WpfToolTip tooltip)
    {
        _tooltipAnimationVersion++;
        tooltip.BeginAnimation(UIElement.OpacityProperty, null);
        tooltip.Opacity = 1;
    }
}

/// <summary>
/// 使用 WPF 实际字体参数判断单行文本是否发生布局溢出。
/// </summary>
internal static class OverflowTextMeasure
{
    public static bool IsOverflowing(TextBlock source, double availableWidth)
    {
        var text = source.Text ?? string.Empty;
        if (text.Length == 0 || availableWidth <= 0)
        {
            return false;
        }

        // 列表行固定为单行；换行符会导致内容无法在当前行完整显示，因此也视为溢出。
        if (text.Contains('\r') || text.Contains('\n'))
        {
            return true;
        }

        var dpi = VisualTreeHelper.GetDpi(source);
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            source.FlowDirection,
            new Typeface(source.FontFamily, source.FontStyle, source.FontWeight, source.FontStretch),
            source.FontSize,
            WpfBrushes.Transparent,
            dpi.PixelsPerDip);

        return formattedText.WidthIncludingTrailingWhitespace > availableWidth + 0.5;
    }
}