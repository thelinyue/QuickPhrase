param([Parameter(Mandatory=$true)][string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$qaName = 'installer-' + [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
$qaRoot = [IO.Path]::GetFullPath((Join-Path $repo ('.build\' + $qaName)))
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repo '.build')) + '\'
if (-not $qaRoot.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase)) { throw '安装验收目录超出工作区。' }
if ([Diagnostics.Process]::GetProcessesByName('shanyu').Length -ne 0) { throw '请先退出正在运行的闪语，再运行安装验收。' }
$testId = '{76389415-706C-47DD-9EA9-9E907B75DA80}'
$testKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $testId + '_is1'
if (Test-Path -LiteralPath $testKey) { throw '存在上次安装验收记录，请先处理该测试安装后重试。' }
$installDir = Join-Path $qaRoot 'installed'
$dataDir = Join-Path $env:LOCALAPPDATA 'com.quickphrase.desktop'
$sentinel = Join-Path $dataDir ($qaName + '.txt')
$group = '闪语安装验收-' + $qaName
$results = New-Object System.Collections.Generic.List[string]
$clientProcess = $null
$runKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
$runExisted = $runKey.GetValueNames() -contains 'Shanyu'
$runOriginal = $runKey.GetValue('Shanyu',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
$runKind = if ($runExisted) { $runKey.GetValueKind('Shanyu') } else { [Microsoft.Win32.RegistryValueKind]::String }
$testRun = '"' + (Join-Path $installDir 'shanyu.exe') + '" --autostart'
New-Item -ItemType Directory -Force -Path $qaRoot,$dataDir | Out-Null
[IO.File]::WriteAllText($sentinel,'安装、升级和卸载应当保留用户数据。',[Text.UTF8Encoding]::new($false))
try {
  # 独立 AppId、开始菜单组和启动项名称，验收不覆盖用户安装和注册信息。
  foreach ($version in @('0.1.0','0.1.1')) {
    $compilerArgs = @('/Q',"/DAppVersion=$version",'/DAppId={{76389415-706C-47DD-9EA9-9E907B75DA80}',"/DReleaseRoot=$qaRoot",('/DSourceDir=' + (Join-Path $repo 'src-tauri\target\release')),('/DWebViewBootstrap=' + (Join-Path $repo '.build\deps\MicrosoftEdgeWebview2Setup.exe')),('/DChineseLanguage=' + (Join-Path $repo 'installer\Languages\ChineseSimplified.isl')),'/DStartupValue=ShanyuInstallerQA',(Join-Path $repo 'installer\Shanyu.iss'))
    & $InnoCompiler @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw '隔离验收安装包编译失败。' }
  }
  $first = Join-Path $qaRoot 'Shanyu-Setup-0.1.0-x64.exe'
  $setupArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $installDir + '"'),('/GROUP="' + $group + '"'),('/LOG="' + (Join-Path $qaRoot 'install.log') + '"'))
  $installer = Start-Process -FilePath $first -ArgumentList $setupArgs -Wait -PassThru -WindowStyle Hidden
  if ($installer.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $installDir 'shanyu.exe'))) { throw '新装验收失败。' }
  if ((Get-ItemProperty -LiteralPath $testKey).DisplayVersion -ne '0.1.0') { throw '安装版本注册错误。' }
  $results.Add('中文 x64 每用户安装与独立注册通过；本机 WebView2 已安装。')
  $oldBrowserArgs = $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
  try {
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=9333'
    $clientProcess = Start-Process -FilePath (Join-Path $installDir 'shanyu.exe') -PassThru -WindowStyle Hidden
  } finally { $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = $oldBrowserArgs }
  & node.exe (Join-Path $repo 'scripts\inspect-installed.mjs') $installDir (Join-Path $qaRoot 'runtime.json')
  if ($LASTEXITCODE -ne 0) { throw '安装后客户端启动检查失败。' }
  $results.Add('安装后的实际 Release 程序启动通过；界面为闪语，数据目录在安装目录之外。')
  $upgrade = Join-Path $qaRoot 'Shanyu-Setup-0.1.1-x64.exe'
  $upgradeArgs = $setupArgs[0..2] + @('/CLOSEAPPLICATIONS','/FORCECLOSEAPPLICATIONS',('/DIR="' + $installDir + '"'),('/GROUP="' + $group + '"'),('/LOG="' + (Join-Path $qaRoot 'upgrade.log') + '"'))
  $installer = Start-Process -FilePath $upgrade -ArgumentList $upgradeArgs -Wait -PassThru -WindowStyle Hidden
  if ($installer.ExitCode -ne 0 -or (Get-ItemProperty -LiteralPath $testKey).DisplayVersion -ne '0.1.1') { throw '升级验收失败。' }
  $clientProcess.Refresh()
  if (-not $clientProcess.HasExited) { throw '升级没有关闭正在运行的测试客户端。' }
  if (-not (Test-Path -LiteralPath $sentinel)) { throw '升级移除了用户数据。' }
  $results.Add('0.1.0 → 0.1.1 安装版本升级、关闭运行中的客户端与数据保留通过；升级样包使用相同程序二进制。')
  $uninstaller = Start-Process -FilePath (Join-Path $installDir 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $qaRoot 'uninstall.log') + '"')) -Wait -PassThru -WindowStyle Hidden
  if ($uninstaller.ExitCode -ne 0 -or (Test-Path -LiteralPath $testKey) -or (Test-Path -LiteralPath (Join-Path $installDir 'shanyu.exe'))) { throw '卸载验收失败。' }
  if (-not (Test-Path -LiteralPath $sentinel) -or -not (Test-Path -LiteralPath (Join-Path $dataDir 'shanyu.sqlite3'))) { throw '卸载没有保留用户数据。' }
  $results.Add('卸载入口可用；程序与测试注册删除，用户数据库和数据文件保留。')
  $results | ForEach-Object { Write-Host "通过：$_" }
} finally {
  if ($clientProcess) { $clientProcess.Refresh(); if (-not $clientProcess.HasExited) { Stop-Process -Id $clientProcess.Id } }
  if ((Test-Path -LiteralPath $testKey) -and (Test-Path -LiteralPath (Join-Path $installDir 'unins000.exe'))) {
    Start-Process -FilePath (Join-Path $installDir 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -WindowStyle Hidden
  }
  if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel }
  # Release 使用真实用户设置；只回退本次程序路径或删除造成的启动项变更，保留其他新修改。
  $currentRun = $runKey.GetValue('Shanyu',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
  if ($null -eq $currentRun -or $currentRun -ceq $testRun) {
    if ($runExisted) { $runKey.SetValue('Shanyu',$runOriginal,$runKind) } else { $runKey.DeleteValue('Shanyu',$false) }
  }
  $runKey.Dispose()
  $report = @{date=[DateTimeOffset]::Now.ToString('o');results=$results.ToArray();limits=@('WebView2 缺失及安装失败未实机模拟；没有卸载系统依赖。','Windows 10 与非当前用户权限级别仍需实机验收；交互向导另由 capture-installer.ps1 检查。')}
  [IO.File]::WriteAllText((Join-Path $qaRoot 'report.json'),($report | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
  Write-Host ('安装验收记录：' + (Join-Path $qaRoot 'report.json'))
}
