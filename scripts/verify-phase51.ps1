param([switch]$IncludeDesktopSmoke)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location $workspace

function Invoke-Step([string]$name, [scriptblock]$action) {
  Write-Host "== $name ==" -ForegroundColor Cyan
  & $action
  if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
}

Invoke-Step "Phase 5.1 tests" { dotnet test QuickPhrase.sln -c Release --no-restore --verbosity minimal }
Invoke-Step "Release build" { dotnet build QuickPhrase.sln -c Release --no-restore --verbosity minimal }

Invoke-Step "Launcher performance smoke" {
  dotnet run --no-build -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -- --smoke-launcher-performance
}
if ($IncludeDesktopSmoke) {
  Invoke-Step "Native Launcher smoke" { dotnet run --no-build -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -- --smoke-native-launcher }
}

Write-Host "PHASE5_1_INFRA_PASS" -ForegroundColor Green
