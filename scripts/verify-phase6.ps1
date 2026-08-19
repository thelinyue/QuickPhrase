param([switch]$IncludeDesktopSmoke)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $workspace

if ($env:QUICKPHRASE_WECOM_ACCEPTANCE -ne "passed") {
  throw "QUICKPHRASE_WECOM_ACCEPTANCE=passed is required before PHASE6_VERIFY_PASS_WIN11."
}

& powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-release.ps1
if ($LASTEXITCODE -ne 0) { throw "Phase 6 release gate failed." }

$releaseRoot = Join-Path $workspace "artifacts\release\1.0.0"
$required = @(
  "QuickPhrase-Setup-1.0.0-online.exe",
  "QuickPhrase-Setup-1.0.0-offline.exe",
  "SHA256SUMS.txt",
  "release-manifest.json"
)
foreach ($name in $required) {
  if (-not (Test-Path (Join-Path $releaseRoot $name)) -and -not (Test-Path (Join-Path $releaseRoot "installers\$name"))) {
    throw "Release artifact missing: $name"
  }
}

$validationPath = Join-Path $workspace "docs\phase6-validation.md"
if (-not (Test-Path $validationPath)) {
  @"
# QuickPhrase Phase 6 Windows 11 Validation

Status: `PHASE6_INFRA_PASS` (upgrade to `PHASE6_VERIFY_PASS_WIN11` only after the full manual matrix)

- Release script: `scripts/build-release.ps1`
- Artifact directory: `artifacts/release/1.0.0`
- Target: Windows 11 x64
- Windows 10: `UNVERIFIED / NOT SUPPORTED IN V1.0.0`
- Signing: unsigned; SmartScreen warning is a known release limitation
"@ | Set-Content -LiteralPath $validationPath -Encoding utf8
}
else {
  Add-Content -LiteralPath $validationPath -Value "`r`n- Phase 6 gate script completed artifact and checksum verification at $(Get-Date -Format o)." -Encoding utf8
}

Write-Host "PHASE6_INFRA_PASS" -ForegroundColor Green
