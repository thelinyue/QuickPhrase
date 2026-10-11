param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
# 构造同名进程用于吸附模块的自动化检查，不启动或操作真实企业微信会话。
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,System.Web.Extensions
Add-Type -LiteralPath (Join-Path $PSScriptRoot 'native-host.cs') -ReferencedAssemblies System.Windows.Forms,System.Drawing,System.Web.Extensions -OutputAssembly $OutputPath -OutputType WindowsApplication
