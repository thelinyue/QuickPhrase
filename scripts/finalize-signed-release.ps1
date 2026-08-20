param(
  [ValidatePattern('^\d+\.\d+\.\d+$')]
  [string]$Version = '0.0.1',
  [Parameter(Mandatory = $true)]
  [string]$SignedPublishRoot,
  [Parameter(Mandatory = $true)]
  [string]$SignedInstallerPath,
  [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $workspace

function Assert-SignedAndTimestamped([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "签名输入不存在：$Path" }
  $signature = Get-AuthenticodeSignature -LiteralPath $Path
  if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) { throw "Authenticode 签名无效：$Path；状态=$($signature.Status)" }
  if (-not $signature.SignerCertificate) { throw "签名证书缺失：$Path" }
  if (-not $signature.TimeStamperCertificate) { throw "可信时间戳缺失：$Path" }
}

$signedPublish = [IO.Path]::GetFullPath($SignedPublishRoot)
$signedInstaller = [IO.Path]::GetFullPath($SignedInstallerPath)
$releaseRoot = [IO.Path]::GetFullPath($(if ($OutputRoot) { $OutputRoot } else { Join-Path $workspace "artifacts\release\$Version" }))
if (-not (Test-Path -LiteralPath $signedPublish -PathType Container)) { throw "已签名 publish 目录不存在：$signedPublish" }

# 只校验 QuickPhrase 自有 PE；.NET/Windows 运行库由各自发布者维护签名。
$ownedPeNames = @('QuickPhrase.exe', 'QuickPhrase.dll', 'QuickPhrase.Core.dll', 'QuickPhrase.Platform.Windows.dll')
foreach ($name in $ownedPeNames) { Assert-SignedAndTimestamped (Join-Path $signedPublish $name) }
Assert-SignedAndTimestamped $signedInstaller

$installerRoot = Join-Path $releaseRoot 'installers'
$archivePath = Join-Path $releaseRoot "QuickPhrase-$Version-win-x64.zip"
$installerPath = Join-Path $installerRoot "QuickPhrase-Setup-$Version.exe"
New-Item -ItemType Directory -Force -Path $releaseRoot, $installerRoot | Out-Null
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
if (Test-Path -LiteralPath $installerPath) { Remove-Item -LiteralPath $installerPath -Force }
Copy-Item -LiteralPath $signedInstaller -Destination $installerPath
Compress-Archive -Path (Join-Path $signedPublish '*') -DestinationPath $archivePath -CompressionLevel Optimal

$assets = @($archivePath, $installerPath)
$hashLines = foreach ($asset in $assets) {
  '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $asset).Hash.ToUpperInvariant(), (Split-Path $asset -Leaf)
}
$hashLines | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding utf8

$revision = (& git rev-parse HEAD 2>$null | Select-Object -First 1)
if (-not $revision) { $revision = 'unknown' }
[ordered]@{
  version = $Version
  fileVersion = "$Version.0"
  rid = 'win-x64'
  finalizedAtUtc = [DateTime]::UtcNow.ToString('O')
  signed = $true
  releaseChannel = 'stable'
  sourceRevision = $revision.Trim()
  artifacts = @($assets | ForEach-Object { Split-Path $_ -Leaf })
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') -Encoding utf8

Write-Host "SIGNED_RELEASE_READY：$releaseRoot" -ForegroundColor Green