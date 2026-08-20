param(
  [ValidatePattern('^\d+\.\d+\.\d+$')]
  [string]$Version = '0.0.1'
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $workspace

if ($env:QUICKPHRASE_WECOM_ACCEPTANCE -ne 'passed') { throw 'QUICKPHRASE_WECOM_ACCEPTANCE=passed 是正式发布门禁。' }
if ($env:QUICKPHRASE_WIN11_ACCEPTANCE -ne 'passed') { throw 'QUICKPHRASE_WIN11_ACCEPTANCE=passed 是 Windows 11 安装矩阵门禁。' }

$releaseRoot = Join-Path $workspace "artifacts\release\$Version"
$required = @(
  "QuickPhrase-Setup-$Version.exe",
  "QuickPhrase-$Version-win-x64.zip",
  'SHA256SUMS.txt',
  'release-manifest.json'
)
foreach ($name in $required) {
  $path = if ($name.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { Join-Path $releaseRoot "installers\$name" } else { Join-Path $releaseRoot $name }
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "正式发布资产缺失：$path" }
}

$manifest = Get-Content -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version -or $manifest.signed -ne $true -or $manifest.releaseChannel -ne 'stable') {
  throw 'release-manifest.json 未声明匹配版本的 signed stable 正式资产。'
}

$archive = Join-Path $releaseRoot "QuickPhrase-$Version-win-x64.zip"
$installer = Join-Path $releaseRoot "installers\QuickPhrase-Setup-$Version.exe"
$hashText = Get-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Raw
foreach ($asset in @($archive, $installer)) {
  $expected = '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $asset).Hash.ToUpperInvariant(), (Split-Path $asset -Leaf)
  if (-not $hashText.Contains($expected)) { throw "SHA256SUMS.txt 与资产不一致：$asset" }
}

$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or -not $signature.SignerCertificate -or -not $signature.TimeStamperCertificate) {
  throw "安装器签名或时间戳无效：$installer"
}

$temp = Join-Path $env:TEMP ("QuickPhrase-Phase6-{0}" -f [Guid]::NewGuid().ToString('N'))
try {
  Expand-Archive -LiteralPath $archive -DestinationPath $temp
  foreach ($name in @('QuickPhrase.exe', 'QuickPhrase.dll', 'QuickPhrase.Core.dll', 'QuickPhrase.Platform.Windows.dll')) {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $temp $name)
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or -not $signature.SignerCertificate -or -not $signature.TimeStamperCertificate) {
      throw "应用签名或时间戳无效：$name"
    }
  }
}
finally {
  if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}

Write-Host "PHASE6_VERIFY_PASS_WIN11：$releaseRoot" -ForegroundColor Green