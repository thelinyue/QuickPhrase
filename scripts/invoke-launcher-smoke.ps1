param(
  [Parameter(Mandatory = $true)]
  [ValidateSet('Native', 'Performance')]
  [string]$Mode,
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe = [IO.Path]::GetFullPath((Join-Path $workspace "desktop\QuickPhrase.Desktop\bin\$Configuration\net10.0-windows10.0.19041.0\QuickPhrase.exe"))
if (-not (Test-Path -LiteralPath $exe)) {
  throw "Launcher smoke EXE 不存在：$exe。请先构建 $Configuration。"
}

$timeouts = @{
  Native = 30
  Performance = 60
}
$argument = if ($Mode -eq 'Native') { '--smoke-native-launcher' } else { '--smoke-launcher-performance' }
$runDirectory = Join-Path $env:TEMP ("QuickPhrase-Smoke\{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'), $PID)
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$stdout = Join-Path $runDirectory 'stdout.log'
$stderr = Join-Path $runDirectory 'stderr.log'

$process = Start-Process -FilePath $exe `
  -ArgumentList @($argument, '--smoke-output', ('"{0}"' -f $runDirectory)) `
  -WindowStyle Hidden `
  -PassThru `
  -RedirectStandardOutput $stdout `
  -RedirectStandardError $stderr

$completed = $process.WaitForExit($timeouts[$Mode] * 1000)
if (-not $completed) {
  Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
  $process.WaitForExit()
  "LAUNCHER_SMOKE_TIMEOUT：$Mode smoke 超过 $($timeouts[$Mode]) 秒；PID=$($process.Id)" |
    Set-Content -LiteralPath (Join-Path $runDirectory 'watchdog-timeout.txt') -Encoding utf8
  Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue
  Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue
  Write-Error "Launcher smoke 超时，诊断目录：$runDirectory"
  exit 124
}

Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue
Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue
if ($process.ExitCode -ne 0) {
  Write-Error "Launcher smoke 失败，退出码 $($process.ExitCode)，诊断目录：$runDirectory"
}
exit $process.ExitCode
