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
$archive = Join-Path $releaseRoot "QuickPhrase-$Version-win-x64.zip"
$installer = Join-Path $releaseRoot "installers\QuickPhrase-Setup-$Version.exe"
$hashPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$manifestPath = Join-Path $releaseRoot 'release-manifest.json'

foreach ($path in @($archive, $installer, $hashPath, $manifestPath)) {
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "正式发布资产缺失：$path" }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version -or $manifest.signed -ne $false -or $manifest.releaseChannel -ne 'stable') {
  throw 'release-manifest.json 必须声明匹配版本的未签名 stable 正式资产。'
}

$expectedAssets = @((Split-Path $archive -Leaf), (Split-Path $installer -Leaf))
foreach ($assetName in $expectedAssets) {
  if ($manifest.artifacts -notcontains $assetName) { throw "release-manifest.json 缺少资产声明：$assetName" }
}

$hashText = Get-Content -LiteralPath $hashPath -Raw
foreach ($asset in @($archive, $installer)) {
  $expected = '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $asset).Hash.ToUpperInvariant(), (Split-Path $asset -Leaf)
  if (-not $hashText.Contains($expected)) { throw "SHA256SUMS.txt 与资产不一致：$asset" }
}

$temp = Join-Path $env:TEMP ("QuickPhrase-Phase6-{0}" -f [Guid]::NewGuid().ToString('N'))
try {
  Expand-Archive -LiteralPath $archive -DestinationPath $temp
  foreach ($name in @('QuickPhrase.exe', 'QuickPhrase.dll', 'QuickPhrase.Core.dll', 'QuickPhrase.Platform.Windows.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $temp $name) -PathType Leaf)) { throw "应用压缩包缺少自有程序集：$name" }
  }

  $forbiddenEntries = Get-ChildItem -LiteralPath $temp -Recurse -Force -File | Where-Object {
    $_.FullName -match '\\(wwwroot|node_modules)\\' -or
    $_.Name -match '(?i)webview2' -or
    $_.Extension -in @('.html', '.htm', '.js', '.mjs', '.jsx', '.tsx', '.css')
  }
  if ($forbiddenEntries) {
    $names = ($forbiddenEntries | ForEach-Object { $_.FullName.Substring($temp.Length).TrimStart('\') }) -join '，'
    throw "正式 WPF 发布包不得包含网页或 WebView2 资源：$names"
  }
}
finally {
  if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}

Write-Host "PHASE6_VERIFY_PASS_WIN11：$releaseRoot" -ForegroundColor Green
