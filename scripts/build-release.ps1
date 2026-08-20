param(
  [ValidatePattern('^\d+\.\d+\.\d+(?:-rc\.\d+)?$')]
  [string]$Version = '0.0.1',
  [ValidateSet('Publish', 'Installer', 'All')]
  [string]$Stage = 'All',
  [switch]$UnsignedCandidate,
  [string]$PublishRootOverride
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $workspace

function Invoke-Step([string]$Name, [scriptblock]$Action) {
  Write-Host "== $Name ==" -ForegroundColor Cyan
  & $Action
  if ($LASTEXITCODE -ne 0) { throw "$Name 失败，退出码 $LASTEXITCODE。" }
}

function Resolve-InnoCompiler {
  $candidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
  ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
  $candidates = @($candidates)
  if (-not $candidates) { throw '未找到 Inno Setup 6.7.3 ISCC.exe。' }

  $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
  if ($winget) {
    $listing = (& $winget.Source list --id JRSoftware.InnoSetup --exact --accept-source-agreements 2>$null | Out-String)
    if ($listing -and $listing -notmatch '6\.7\.3') { throw "要求 Inno Setup 6.7.3，当前检测结果：$listing" }
  }
  $candidates[0]
}

function Assert-WorkspacePath([string]$Path, [string]$Label) {
  $resolved = [IO.Path]::GetFullPath($Path)
  if (-not $resolved.StartsWith($workspace.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "$Label 必须位于工作区内：$resolved"
  }
  $resolved
}

function Write-ReleaseMetadata {
  $assets = @(
    Get-ChildItem -LiteralPath $releaseRoot -File -Filter '*.zip' -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $installerRoot -File -Filter '*.exe' -ErrorAction SilentlyContinue
  ) | Sort-Object Name
  $hashLines = foreach ($asset in $assets) {
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $asset.FullName).Hash.ToUpperInvariant(), $asset.Name
  }
  $hashLines | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding utf8

  $revision = (& git rev-parse HEAD 2>$null | Select-Object -First 1)
  if (-not $revision) { $revision = 'unknown' }
  [ordered]@{
    version = $Version
    fileVersion = $fileVersion
    rid = 'win-x64'
    buildTimeUtc = [DateTime]::UtcNow.ToString('O')
    dotnetSdk = (dotnet --version).Trim()
    innoSetupVersion = '6.7.3'
    signed = $false
    releaseChannel = if ($Version.Contains('-')) { 'prerelease' } else { 'stable' }
    supportedOs = 'Windows 11 x64'
    windows10Status = 'unverified / not supported in 0.0.1'
    sourceRevision = $revision.Trim()
    artifacts = @($assets.Name)
  } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') -Encoding utf8
}

if ((dotnet --version).Trim() -ne '10.0.400') { throw '要求 .NET SDK 10.0.400。' }
if ($UnsignedCandidate -and -not $Version.Contains('-rc.')) { throw '-UnsignedCandidate 仅用于 RC 候选版本。' }

$numericVersion = ($Version -split '-')[0]
$fileVersion = "$numericVersion.0"
$releaseRoot = Assert-WorkspacePath (Join-Path $workspace "artifacts\release\$Version") '发布目录'
$defaultPublishRoot = Join-Path $releaseRoot 'publish'
$publishRoot = if ($PublishRootOverride) { [IO.Path]::GetFullPath($PublishRootOverride) } else { $defaultPublishRoot }
$installerRoot = Join-Path $releaseRoot 'installers'
$suffix = if ($UnsignedCandidate) { '-unsigned' } else { '' }
$installerBase = "QuickPhrase-Setup-$Version$suffix"
$archiveName = "QuickPhrase-$Version-win-x64$suffix.zip"
$archivePath = Join-Path $releaseRoot $archiveName

if ($Stage -in @('Publish', 'All')) {
  if ($env:QUICKPHRASE_WECOM_ACCEPTANCE -ne 'passed') {
    throw '发布前必须设置 QUICKPHRASE_WECOM_ACCEPTANCE=passed，以确认企业微信人工矩阵已通过。'
  }
  if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $defaultPublishRoot, $installerRoot | Out-Null
  $publishRoot = $defaultPublishRoot

  Invoke-Step 'Phase 5/5.1 verification' { powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase51.ps1 -IncludeDesktopSmoke }
  Invoke-Step 'Debug build' { dotnet build QuickPhrase.sln -c Debug --no-restore --verbosity minimal }
  Invoke-Step 'Debug Desktop tests' { dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj -c Debug --no-build --verbosity minimal }
  Invoke-Step 'Debug Architecture tests' { dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj -c Debug --no-build --verbosity minimal }
  Invoke-Step 'Release build' { dotnet build QuickPhrase.sln -c Release --no-restore --verbosity minimal }
  Invoke-Step 'Release Desktop tests' { dotnet test tests/QuickPhrase.Desktop.Tests/QuickPhrase.Desktop.Tests.csproj -c Release --no-build --verbosity minimal }
  Invoke-Step 'Release Architecture tests' { dotnet test tests/QuickPhrase.Architecture.Tests/QuickPhrase.Architecture.Tests.csproj -c Release --no-build --verbosity minimal }
  Invoke-Step 'win-x64 restore' { dotnet restore desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -r win-x64 -p:PublishReadyToRun=true }
  Invoke-Step 'Self-contained ReadyToRun publish' {
    dotnet publish desktop/QuickPhrase.Desktop/QuickPhrase.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -o $publishRoot `
      -p:RuntimeIdentifier=win-x64 -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:DebugType=None `
      -p:Version=$Version -p:FileVersion=$fileVersion -p:AssemblyVersion=$fileVersion -p:InformationalVersion=$Version `
      -p:IncludeSourceRevisionInInformationalVersion=false
  }

  $exePath = Join-Path $publishRoot 'QuickPhrase.exe'
  if (-not (Test-Path -LiteralPath $exePath)) { throw '发布目录缺少 QuickPhrase.exe。' }
  if (Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter 'quickphrase-wallpaper.png' -ErrorAction SilentlyContinue) {
    throw '正式 WPF 发布包不得包含原型壁纸。'
  }
  if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
  Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
  Write-ReleaseMetadata
}

if ($Stage -in @('Installer', 'All')) {
  if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'QuickPhrase.exe') -PathType Leaf)) {
    throw "安装器输入目录缺少 QuickPhrase.exe：$publishRoot"
  }
  New-Item -ItemType Directory -Force -Path $releaseRoot, $installerRoot | Out-Null
  $innoPublishRoot = Join-Path $releaseRoot 'publish'
  if ([IO.Path]::GetFullPath($publishRoot) -ne [IO.Path]::GetFullPath($innoPublishRoot)) {
    if (Test-Path -LiteralPath $innoPublishRoot) { Remove-Item -LiteralPath $innoPublishRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $innoPublishRoot | Out-Null
    Copy-Item -Path (Join-Path $publishRoot '*') -Destination $innoPublishRoot -Recurse -Force
  }
  $iscc = Resolve-InnoCompiler
  Invoke-Step 'Installer' {
    & $iscc "/DAppVersion=$Version" "/DReleaseRoot=$releaseRoot" "/DOutputBase=$installerBase" 'installer\QuickPhrase.iss'
  }
  $installerPath = Join-Path $installerRoot "$installerBase.exe"
  if (-not (Test-Path -LiteralPath $installerPath)) { throw "安装器未生成：$installerPath" }
  Write-ReleaseMetadata
}

Write-Host "RELEASE_STAGE_READY：Version=$Version Stage=$Stage Root=$releaseRoot" -ForegroundColor Green