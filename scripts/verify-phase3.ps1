param(
  [switch]$IncludeDesktopSmoke
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location $workspace

Write-Host "== Phase 2 regression gate ==" -ForegroundColor Cyan
$phase2Args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $workspace "scripts/verify-phase2.ps1"))
if ($IncludeDesktopSmoke) { $phase2Args += "-IncludeDesktopSmoke" }
& powershell @phase2Args
if ($LASTEXITCODE -ne 0) { throw "Phase 2 regression gate failed with exit code $LASTEXITCODE." }

Write-Host "== Phase 3 Debug search tests ==" -ForegroundColor Cyan
& dotnet test QuickPhrase.sln --filter FullyQualifiedName~Phase3SearchTests --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Phase 3 Debug search tests failed with exit code $LASTEXITCODE." }

Write-Host "== Phase 3 Release build ==" -ForegroundColor Cyan
& dotnet build QuickPhrase.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Phase 3 Release build failed with exit code $LASTEXITCODE." }

Write-Host "== Phase 3 Release search and performance tests ==" -ForegroundColor Cyan
& dotnet test QuickPhrase.sln -c Release --no-build --filter FullyQualifiedName~Phase3SearchTests --logger "console;verbosity=detailed"
if ($LASTEXITCODE -ne 0) { throw "Phase 3 Release search tests failed with exit code $LASTEXITCODE." }

Write-Host "PHASE3_VERIFY_PASS" -ForegroundColor Green
