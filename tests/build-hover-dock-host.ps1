param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
# 独立编译 Debug 专用宿主，不访问用户真实企业微信或剪贴板。
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,System.Web.Extensions
Add-Type -LiteralPath @((Join-Path $PSScriptRoot 'hover-dock-host.cs'),(Join-Path $PSScriptRoot 'dock-window.cs')) -ReferencedAssemblies System.Windows.Forms,System.Drawing,System.Web.Extensions -OutputAssembly $OutputPath -OutputType WindowsApplication
