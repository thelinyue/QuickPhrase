param(
  [switch]$IncludeDesktopSmoke,
  [switch]$IncludeWeComAcceptance
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location $workspace

function Invoke-Step([string]$name, [scriptblock]$action) {
  Write-Host "== $name ==" -ForegroundColor Cyan
  & $action
  if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
}

Invoke-Step "Phase 4 regression gate" {
  $phase4Args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $workspace "scripts/verify-phase4.ps1"))
  if ($IncludeDesktopSmoke) { $phase4Args += "-IncludeDesktopSmoke" }
  & powershell @phase4Args
}
Invoke-Step "Phase 5 tests" { dotnet test QuickPhrase.sln --filter FullyQualifiedName~Phase5DeliveryTests --verbosity minimal }
Invoke-Step "Debug build" { dotnet build QuickPhrase.sln --no-restore --verbosity minimal }
Invoke-Step "Debug tests" { dotnet test QuickPhrase.sln --no-build --verbosity minimal }
Invoke-Step "Release build" { dotnet build QuickPhrase.sln -c Release --no-restore --verbosity minimal }
Invoke-Step "Release tests" { dotnet test QuickPhrase.sln -c Release --no-build --verbosity minimal }

Invoke-Step "Launcher performance smoke" {
  dotnet run --no-build -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -- --smoke-launcher-performance
}
if ($IncludeDesktopSmoke) {
  Invoke-Step "Fake Target delivery smoke" {
    dotnet run --no-build -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -- --smoke-native-launcher
  }
}

if ($IncludeWeComAcceptance) {
  if ($env:QUICKPHRASE_WECOM_ACCEPTANCE -ne "passed") {
    throw "Manual WXWork acceptance requires a prepared WXWork 5.0.9.6065 test session and QUICKPHRASE_WECOM_ACCEPTANCE=passed. The script never takes over a real window."
  }
  Write-Host "WXWork manual acceptance was confirmed by the user." -ForegroundColor Yellow
}

Write-Host "PHASE5_INFRA_PASS" -ForegroundColor Green
if ($IncludeWeComAcceptance) { Write-Host "PHASE5_VERIFY_PASS" -ForegroundColor Green }
