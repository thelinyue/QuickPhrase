param(
  [switch]$IncludeDesktopSmoke
)

$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location $workspace

Write-Host "== Phase 1 regression gate ==" -ForegroundColor Cyan
$phase1Args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $workspace "scripts/verify-phase1.ps1"))
if ($IncludeDesktopSmoke) { $phase1Args += "-IncludeDesktopSmoke" }
& powershell @phase1Args
if ($LASTEXITCODE -ne 0) { throw "Phase 1 regression gate failed with exit code $LASTEXITCODE." }

Write-Host "== Phase 2 data tests ==" -ForegroundColor Cyan
& dotnet test QuickPhrase.sln --filter FullyQualifiedName~Phase2DataTests --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Phase 2 data tests failed with exit code $LASTEXITCODE." }

Write-Host "== Phase 2 release build ==" -ForegroundColor Cyan
& dotnet build QuickPhrase.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Phase 2 release build failed with exit code $LASTEXITCODE." }

Write-Host "== Phase 2 release tests ==" -ForegroundColor Cyan
& dotnet test QuickPhrase.sln -c Release --no-build --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Phase 2 release tests failed with exit code $LASTEXITCODE." }

Write-Host "PHASE2_VERIFY_PASS" -ForegroundColor Green
