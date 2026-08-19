using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using QuickPhrase.Desktop.Controls;

namespace QuickPhrase.Desktop.Tests;

/// <summary>
/// 溢出文本控件的行为回归：只有真实溢出时才增加完整内容提示与键盘焦点入口。
/// </summary>
public sealed class OverflowTextBlockTests
{
    [Fact]
    public void ShortText_DoesNotExposeOverflowInteraction()
    {
        var state = RunSta(() =>
        {
            var control = CreateMeasuredControl("短文案", 240);
            return new
            {
                control.HasOverflow,
                control.ToolTip,
                control.Focusable,
                IsTabStop = KeyboardNavigation.GetIsTabStop(control),
                HelpText = AutomationProperties.GetHelpText(control),
            };
        });

        Assert.False(state.HasOverflow);
        Assert.Null(state.ToolTip);
        Assert.False(state.Focusable);
        Assert.False(state.IsTabStop);
        Assert.Empty(state.HelpText);
    }

    [Fact]
    public void LongText_ExposesCompleteTextThroughTooltipAndKeyboardFocus()
    {
        const string text = "这是需要在列表中折叠显示、并在聚焦或悬停时查看完整内容的长文案。";
        var state = RunSta(() =>
        {
            var control = CreateMeasuredControl(text, 120);
            return new
            {
                control.HasOverflow,
                ToolTipText = ExtractTooltipText(control.ToolTip as System.Windows.Controls.ToolTip),
                control.Focusable,
                IsTabStop = KeyboardNavigation.GetIsTabStop(control),
                HelpText = AutomationProperties.GetHelpText(control),
            };
        });

        Assert.True(state.HasOverflow);
        Assert.NotEmpty(state.ToolTipText);
        Assert.Contains(text, state.ToolTipText);
        Assert.True(state.Focusable);
        Assert.True(state.IsTabStop);
        Assert.Contains(text, state.HelpText);
    }

    [Fact]
    public void ResizingFromWideToNarrow_EnablesOverflowInteraction()
    {
        var state = RunSta(() =>
        {
            var control = CreateMeasuredControl("一段较长的中文、English、12345 混合文案", 500);
            var wasShort = !control.HasOverflow;

            control.Width = 120;
            MeasureAndArrange(control, 120);

            return new
            {
                WasShort = wasShort,
                control.HasOverflow,
                ToolTipText = ExtractTooltipText(control.ToolTip as System.Windows.Controls.ToolTip),
            };
        });

        Assert.True(state.WasShort);
        Assert.True(state.HasOverflow);
        Assert.Contains("English", state.ToolTipText);
    }

    [Fact]
    public void NewlineAndContinuousText_UseActualLayoutOverflow()
    {
        var state = RunSta(() =>
        {
            var newlineControl = CreateMeasuredControl("第一行\n第二行", 500);
            var continuousControl = CreateMeasuredControl(new string('a', 80), 120);

            return new
            {
                NewlineOverflow = newlineControl.HasOverflow,
                ContinuousOverflow = continuousControl.HasOverflow,
                ContinuousTooltip = ExtractTooltipText(continuousControl.ToolTip as System.Windows.Controls.ToolTip),
            };
        });

        Assert.True(state.NewlineOverflow);
        Assert.True(state.ContinuousOverflow);
        Assert.Contains(new string('a', 80), state.ContinuousTooltip);
    }

    [Fact]
    public void ChangingTextWhileStillOverflowing_RefreshesCompleteTooltip()
    {
        const string firstText = "第一条很长的完整文案，用于验证浮层内容会同步更新。";
        const string secondText = "第二条很长的完整文案，更新后浮层不能继续保留旧文本。";
        var tooltipText = RunSta(() =>
        {
            var control = CreateMeasuredControl(firstText, 120);
            control.Text = secondText;
            MeasureAndArrange(control, 120);
            return ExtractTooltipText(control.ToolTip as System.Windows.Controls.ToolTip);
        });

        Assert.Equal(secondText, tooltipText);
    }
    [Fact]
    public void ResizingFromNarrowToWide_RemovesOverflowInteraction()
    {
        var state = RunSta(() =>
        {
            var control = CreateMeasuredControl("这是一段会在窄容器中溢出的完整中文文案。", 120);
            var wasOverflowing = control.HasOverflow;

            control.Width = 500;
            MeasureAndArrange(control, 500);

            return new { WasOverflowing = wasOverflowing, control.HasOverflow, control.ToolTip };
        });

        Assert.True(state.WasOverflowing);
        Assert.False(state.HasOverflow);
        Assert.Null(state.ToolTip);
    }

    private static OverflowTextBlock CreateMeasuredControl(string text, double width)
    {
        var control = new OverflowTextBlock
        {
            Text = text,
            Width = width,
            Height = 32,
            FontSize = 15,
        };
        MeasureAndArrange(control, width);
        return control;
    }

    private static void MeasureAndArrange(OverflowTextBlock control, double width)
    {
        control.Measure(new Size(width, 32));
        control.Arrange(new Rect(0, 0, width, 32));
        control.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static string ExtractTooltipText(System.Windows.Controls.ToolTip? toolTip)
    {
        if (toolTip?.Content is not Border { Child: ScrollViewer { Content: TextBlock text } })
        {
            return string.Empty;
        }

        return text.Text;
    }

    private static T RunSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null) throw new Xunit.Sdk.XunitException(exception.ToString());
        return result!;
    }
}




