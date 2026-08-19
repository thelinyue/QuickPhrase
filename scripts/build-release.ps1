param(
  [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$workspace = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $workspace

function Invoke-Step([string]$Name, [scriptblock]$Action) {
  Write-Host "== $Name ==" -ForegroundColor Cyan
  & $Action
  if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

function Assert-Command([string]$Name, [string]$Hint) {
  if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "$Name not found: $Hint" }
}

if ((dotnet --version).Trim() -ne "10.0.400") { throw "Requires .NET SDK 10.0.400." }
$isccCandidates = @(
  (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
  (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
  (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_) }
$isccCandidates = @($isccCandidates)
if (-not $isccCandidates) { throw "Inno Setup 6.7.3 ISCC.exe not found." }
$iscc = $isccCandidates[0]
$winget = Get-Command winget.exe -ErrorAction SilentlyContinue
if ($winget) {
  $innoListing = (& $winget.Source list --id JRSoftware.InnoSetup --exact --accept-source-agreements 2>$null | Out-String)
  if ($innoListing -and $innoListing -notmatch "6\.7\.3") { throw "Inno Setup 6.7.3 is required; detected package listing: $innoListing" }
}

$releaseRoot = Join-Path $workspace "artifacts\release\$Version"
$publishRoot = Join-Path $releaseRoot "publish"
$prereqRoot = Join-Path $releaseRoot "prerequisites"
$installerRoot = Join-Path $releaseRoot "installers"
$fileVersion = "$Version.0"

if ($env:QUICKPHRASE_WECOM_ACCEPTANCE -ne "passed") {
  throw "Phase 5 manual WeCom gate is not marked passed; set QUICKPHRASE_WECOM_ACCEPTANCE=passed before release."
}
Invoke-Step "Phase 5/5.1 verification" { powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase51.ps1 -IncludeDesktopSmoke }

$resolvedRoot = [IO.Path]::GetFullPath($releaseRoot)
$resolvedWorkspace = [IO.Path]::GetFullPath($workspace)
if (-not $resolvedRoot.StartsWith($resolvedWorkspace, [StringComparison]::OrdinalIgnoreCase)) { throw "Release directory is outside workspace." }
if (Test-Path $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishRoot, $prereqRoot, $installerRoot | Out-Null

Invoke-Step "Debug build" { dotnet build QuickPhrase.sln -c Debug --no-restore --verbosity minimal }
Invoke-Step "Debug tests" { dotnet test QuickPhrase.sln -c Debug --no-build --verbosity minimal }
Invoke-Step "Release build" { dotnet build QuickPhrase.sln -c Release --no-restore --verbosity minimal }
Invoke-Step "Release tests" { dotnet test QuickPhrase.sln -c Release --no-build --verbosity minimal }

Invoke-Step "Self-contained ReadyToRun publish" {
  dotnet publish desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -o $publishRoot `
    -p:RuntimeIdentifier=win-x64 -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=None -p:Version=$Version -p:FileVersion=$fileVersion -p:AssemblyVersion=$fileVersion
}

$exePath = Join-Path $publishRoot "QuickPhrase.exe"
if (-not (Test-Path $exePath)) { throw "QuickPhrase.exe missing from publish directory." }
if (Get-ChildItem $publishRoot -Recurse -File -Filter "quickphrase-wallpaper.png" -ErrorAction SilentlyContinue) { throw "Management package must not include demo wallpaper." }

Invoke-Step "Installer" { & $iscc "installer\QuickPhrase.iss" }

$assets = @(
  Get-ChildItem $installerRoot -File -Filter "QuickPhrase-Setup-1.0.0-*.exe"
  Get-Item $exePath
)
$hashLines = foreach ($asset in $assets) {
  "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 -LiteralPath $asset.FullName).Hash.ToLowerInvariant(), $asset.Name
}
$hashLines | Set-Content -LiteralPath (Join-Path $releaseRoot "SHA256SUMS.txt") -Encoding utf8
$manifest = [ordered]@{
  version = $Version
  rid = "win-x64"
  buildTimeUtc = [DateTime]::UtcNow.ToString("O")
  dotnetSdk = (dotnet --version).Trim()
  innoSetupVersion = "6.7.3"
  signed = $false
  supportedOs = "Windows 11 x64"
  windows10Status = "unverified / not supported in V1.0.0"
  sourceState = "workspace-no-git"
  artifacts = $assets.Name
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $releaseRoot "release-manifest.json") -Encoding utf8
Write-Host "PHASE6_ARTIFACTS_READY: $releaseRoot" -ForegroundColor Green
