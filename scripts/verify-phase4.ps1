param(
  [switch]$IncludeDesktopSmoke
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location $workspace

function Invoke-Step([string]$name, [scriptblock]$action) {
  Write-Host "== $name ==" -ForegroundColor Cyan
  & $action
  if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE." }
}

# Phase 4 depends on the Phase 3 data, search, React/Sites and browser gates.
Invoke-Step "Phase 3 regression gate" {
  $args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $workspace "scripts/verify-phase3.ps1"))
  if ($IncludeDesktopSmoke) { $args += "-IncludeDesktopSmoke" }
  & powershell @args
}

Invoke-Step "Phase 4 tests" {
  & dotnet test QuickPhrase.sln --filter FullyQualifiedName~Phase4LauncherTests --verbosity minimal
}

Invoke-Step "Release build" {
  & dotnet build QuickPhrase.sln -c Release --no-restore
}

Invoke-Step "Release regression tests" {
  & dotnet test QuickPhrase.sln -c Release --no-build --verbosity minimal
}

Invoke-Step "Native launcher performance smoke" {
  & dotnet run --no-build -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -- --smoke-launcher-performance
}

if ($IncludeDesktopSmoke) {
  Invoke-Step "Native launcher smoke" {
    & dotnet run --no-build -c Release --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -- --smoke-native-launcher
  }
}

Write-Host "PHASE4_VERIFY_PASS" -ForegroundColor Green
