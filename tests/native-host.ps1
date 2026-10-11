param([Parameter(Mandatory=$true)][string]$EventsPath,[Parameter(Mandatory=$true)][string]$ControlPath)
$ErrorActionPreference = 'Stop'
# 独立通用输入宿主只读取标准 Windows 剪贴板格式并记录结果，不连接任何聊天软件。
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,System.Web.Extensions
Add-Type -LiteralPath (Join-Path $PSScriptRoot 'native-host.cs') -ReferencedAssemblies System.Windows.Forms,System.Drawing,System.Web.Extensions
[ShanyuTestHost]::Run($EventsPath,$ControlPath)
