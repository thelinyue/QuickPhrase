param([long]$Handle = 0)
$ErrorActionPreference = 'Stop'
Add-Type -LiteralPath (Join-Path $PSScriptRoot 'native-input-metadata.cs')
# 只读消息编辑器的候选元数据；不读取 Name、Value、Text，不切换焦点、不调用控件操作。
# Handle 仅用于隔离宿主调试；默认检查已运行企业微信的主窗口。
if (-not $Handle) {
    $wecomProcess = Get-Process -Name WXWork -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $wecomProcess) { throw '企业微信未运行或没有可检查的主窗口，请先打开目标页面。' }
    $Handle = $wecomProcess.MainWindowHandle.ToInt64()
}
# 原生 COM 保留系统默认代理；旧 .NET UIA 混用会漏掉标准控件的文本能力。
# TextPattern 只能证明文本接口存在，不能代替 TextEditPattern 的组字范围证明。
try {
    ConvertTo-Json -InputObject @([ShanyuNativeInputMetadata]::Read($Handle)) -Depth 4
} catch {
    throw "读取输入区能力失败，请确认目标窗口仍然打开：$($_.Exception.GetBaseException().Message)"
}
