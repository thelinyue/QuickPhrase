param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
# 构造 Debug 专用宿主用于吸附模块检查，不启动或操作真实企业微信会话。
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,System.Web.Extensions
Add-Type -LiteralPath @((Join-Path $PSScriptRoot 'native-host.cs'),(Join-Path $PSScriptRoot 'dock-window.cs')) -ReferencedAssemblies System.Windows.Forms,System.Drawing,System.Web.Extensions -OutputAssembly $OutputPath -OutputType WindowsApplication
