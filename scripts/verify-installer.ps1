param([Parameter(Mandatory=$true)][string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repo = Split-Path -Parent $PSScriptRoot
$qaName = 'installer-' + [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
$qaRoot = [IO.Path]::GetFullPath((Join-Path $repo ('.build\' + $qaName)))
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repo '.build')) + '\'
if (-not $qaRoot.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase)) { throw '安装验收目录超出工作区。' }
$version = (Get-Content -Raw -LiteralPath (Join-Path $repo 'package.json') | ConvertFrom-Json).version
$testId = '{76389415-706C-47DD-9EA9-9E907B75DA80}'
$testKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $testId + '_is1'
if (Test-Path -LiteralPath $testKey) { throw '存在上次安装验收记录，请先处理该测试安装后重试。' }
$installDir = Join-Path $qaRoot 'installed'
$dataDir = Join-Path $qaRoot 'data'
$sentinel = Join-Path $dataDir '用户数据.txt'
$group = '闪语安装验收-' + $qaName
$results = New-Object System.Collections.Generic.List[string]
$startupValue = 'QuickPhrase-' + $qaName
$legacyStartupValue = 'Shanyu-' + $qaName
$runKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
New-Item -ItemType Directory -Force -Path $qaRoot,$dataDir | Out-Null
[IO.File]::WriteAllText($sentinel,'安装、升级和卸载应当保留用户数据。',[Text.UTF8Encoding]::new($false))
$uninstall = { if ((Test-Path -LiteralPath $testKey) -and (Test-Path -LiteralPath (Join-Path $installDir 'unins000.exe'))) {
  $process = Start-Process -FilePath (Join-Path $installDir 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait -PassThru -WindowStyle Hidden
  if ($process.ExitCode -ne 0) { throw '测试安装卸载失败。' }
} }
try {
  # 使用独立 AppId、菜单组、安装目录及两个启动项；不运行客户端，不访问真实用户数据。
  $compilerArgs = @('/Q','/DAppId={{76389415-706C-47DD-9EA9-9E907B75DA80}',"/DReleaseRoot=$qaRoot",('/DSourceDir=' + (Join-Path $repo 'src-tauri\target\release')),('/DWebViewBootstrap=' + (Join-Path $repo '.build\deps\MicrosoftEdgeWebview2Setup.exe')),('/DChineseLanguage=' + (Join-Path $repo 'installer\Languages\ChineseSimplified.isl')))
  & $InnoCompiler @compilerArgs "/DAppVersion=$version" "/DAppGroup=$group" "/DStartupValue=$startupValue" "/DLegacyStartupValue=$legacyStartupValue" (Join-Path $repo 'installer\QuickPhrase.iss')
  if ($LASTEXITCODE -ne 0) { throw '新安装包编译失败。' }
  $setup = Join-Path $qaRoot "QuickPhrase-Setup-$version-x64.exe"
  $setupArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $installDir + '"'),('/GROUP="' + $group + '"'),('/LOG="' + (Join-Path $qaRoot 'install.log') + '"'))
  $process = Start-Process -FilePath $setup -ArgumentList $setupArgs -Wait -PassThru -WindowStyle Hidden
  if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $installDir 'QuickPhrase.exe'))) { throw '新装验收失败。' }
  if ((Get-ItemProperty -LiteralPath $testKey).DisplayVersion -ne $version) { throw '安装版本注册错误。' }
  $results.Add('新装 QuickPhrase.exe、中文每用户安装与独立注册通过。')
  & $uninstall
  if ((Test-Path -LiteralPath $testKey) -or (Test-Path -LiteralPath (Join-Path $installDir 'QuickPhrase.exe'))) { throw '新装卸载验收失败。' }
  $results.Add('新装卸载清理程序和注册，保留隔离用户数据。')

  # 从已发布标签读取旧安装器，只替换测试资源位置，保留旧文件名及升级识别方式。
  $legacyScript = (& git -C $repo show 'v0.2.0:installer/Shanyu.iss') -join "`r`n"
  if ($LASTEXITCODE -ne 0) { throw '无法读取已发布0.2.0安装器。' }
  $legacyScript = $legacyScript.Replace('..\src-tauri\icons\icon.ico',(Join-Path $repo 'src-tauri\icons\icon.ico')).Replace('..\LICENSE',(Join-Path $repo 'LICENSE'))
  $legacyScript = $legacyScript.Replace('DefaultGroupName=闪语',("DefaultGroupName=$group"))
  $legacyPath = Join-Path $qaRoot 'legacy.iss'
  [IO.File]::WriteAllText($legacyPath,$legacyScript,[Text.UTF8Encoding]::new($false))
  & $InnoCompiler @compilerArgs '/DAppVersion=0.2.0' "/DStartupValue=$legacyStartupValue" $legacyPath
  if ($LASTEXITCODE -ne 0) { throw '旧安装器夹具编译失败，请保留0.2.0的 shanyu.exe 构建产物。' }
  $process = Start-Process -FilePath (Join-Path $qaRoot 'Shanyu-Setup-0.2.0-x64.exe') -ArgumentList $setupArgs -Wait -PassThru -WindowStyle Hidden
  if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $installDir 'shanyu.exe'))) { throw '旧安装夹具失败。' }
  $runKey.SetValue($legacyStartupValue,('"' + (Join-Path $installDir 'shanyu.exe') + '" --autostart'))
  # 升级不指定 /DIR，验证同一 AppId 沿用先前安装目录。
  $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $qaRoot 'upgrade.log') + '"')) -Wait -PassThru -WindowStyle Hidden
  if ($process.ExitCode -ne 0 -or (Get-ItemProperty -LiteralPath $testKey).DisplayVersion -ne $version -or -not (Test-Path -LiteralPath (Join-Path $installDir 'QuickPhrase.exe')) -or (Test-Path -LiteralPath (Join-Path $installDir 'shanyu.exe'))) { throw '改名升级或旧程序清理失败。' }
  $expectedRun = '"' + (Join-Path $installDir 'QuickPhrase.exe') + '" --autostart'
  if ($runKey.GetValue($startupValue) -cne $expectedRun -or $null -ne $runKey.GetValue($legacyStartupValue)) { throw '开机启动项没有迁移到唯一的新入口。' }
  $shortcutPath = Join-Path $env:APPDATA ("Microsoft\Windows\Start Menu\Programs\$group\闪语.lnk")
  $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
  [IO.File]::WriteAllText((Join-Path $qaRoot 'shortcut.json'),(@{path=$shortcutPath;exists=(Test-Path -LiteralPath $shortcutPath);target=$shortcut.TargetPath;registeredGroup=(Get-ItemProperty -LiteralPath $testKey).'Inno Setup: Icon Group'} | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
  if ($shortcut.TargetPath -ine (Join-Path $installDir 'QuickPhrase.exe')) { throw '开始菜单快捷方式仍指向旧程序。' }
  if ([IO.File]::ReadAllText($sentinel) -cne '安装、升级和卸载应当保留用户数据。') { throw '升级修改了用户数据。' }
  $results.Add('0.2.0旧安装器 → 当前安装器改名升级：沿用原目录、移除旧程序、迁移开机启动、更新快捷方式并保留数据。')
  & $uninstall
  if ((Test-Path -LiteralPath $testKey) -or (Test-Path -LiteralPath (Join-Path $installDir 'QuickPhrase.exe')) -or $null -ne $runKey.GetValue($startupValue) -or $null -ne $runKey.GetValue($legacyStartupValue) -or -not (Test-Path -LiteralPath $sentinel)) { throw '升级后的卸载或数据保留失败。' }
  $results.Add('改名升级后卸载清理程序、测试注册及两个启动项，保留数据。')
  $results | ForEach-Object { Write-Host "通过：$_" }
} finally {
  & $uninstall
  $runKey.DeleteValue($startupValue,$false)
  $runKey.DeleteValue($legacyStartupValue,$false)
  $runKey.Dispose()
  $report = @{date=[DateTimeOffset]::Now.ToString('o');results=$results.ToArray();limits=@('仅验证静默安装与改名升级／卸载，未启动安装后的程序；真实企业微信、uTools启动分组、DPI及交互安装仍需验收。','WebView2 缺失及失败、Windows10、其他用户权限级别未模拟。')}
  [IO.File]::WriteAllText((Join-Path $qaRoot 'report.json'),($report | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
  Write-Host ('安装验收记录：' + (Join-Path $qaRoot 'report.json'))
}
