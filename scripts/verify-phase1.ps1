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

Invoke-Step "Check .NET SDK 10.0.400" { dotnet --version; if ((dotnet --version) -ne "10.0.400") { exit 1 } }

Invoke-Step ".NET build" { dotnet build QuickPhrase.sln --no-restore }
Invoke-Step ".NET tests" { dotnet test QuickPhrase.sln --no-build --verbosity minimal }

if ($IncludeDesktopSmoke) {
  Invoke-Step "Native Launcher smoke" { dotnet run --no-build --project desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -- --smoke-native-launcher }
}

Write-Host "PHASE1_VERIFY_PASS" -ForegroundColor Green
