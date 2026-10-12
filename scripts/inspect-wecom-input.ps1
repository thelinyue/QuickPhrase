param([long]$Handle = 0)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
# 只读消息编辑器的候选元数据；不读取 Name、Value、Text，不切换焦点、不调用控件操作。
# Handle 仅用于隔离宿主调试；默认检查已运行企业微信的主窗口。
if (-not $Handle) {
    $wecomProcess = Get-Process -Name WXWork -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $wecomProcess) { throw '企业微信未运行或没有可检查的主窗口，请先打开目标页面。' }
    $Handle = $wecomProcess.MainWindowHandle.ToInt64()
}
$root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Handle)
$condition = New-Object System.Windows.Automation.OrCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document))
)
$elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
$metadata = foreach ($element in $elements) {
    $current = $element.Current
    $valuePattern = $null
    $hasValuePattern = $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)
    [PSCustomObject]@{
        AutomationId = $current.AutomationId
        ClassName = $current.ClassName
        ControlType = $current.ControlType.ProgrammaticName
        Enabled = $current.IsEnabled
        Offscreen = $current.IsOffscreen
        Focusable = $current.IsKeyboardFocusable
        Password = $current.IsPassword
        ReadOnly = if ($hasValuePattern) { $valuePattern.Current.IsReadOnly } else { $null }
        Bounds = $current.BoundingRectangle.ToString()
    }
}
ConvertTo-Json -InputObject @($metadata) -Depth 3
