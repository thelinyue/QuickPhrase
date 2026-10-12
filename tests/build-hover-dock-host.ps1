param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
# 独立编译同名进程，不访问用户真实企业微信或剪贴板。
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,System.Web.Extensions,UIAutomationProvider,UIAutomationTypes,WindowsBase
Add-Type -LiteralPath @((Join-Path $PSScriptRoot 'hover-dock-host.cs'),(Join-Path $PSScriptRoot 'chat-input-box.cs')) -ReferencedAssemblies System.Windows.Forms,System.Drawing,System.Web.Extensions,UIAutomationProvider,UIAutomationTypes,WindowsBase -OutputAssembly $OutputPath -OutputType WindowsApplication
