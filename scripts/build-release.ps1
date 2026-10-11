param([string]$InnoCompiler = $env:INNOSETUP_ISCC, [switch]$SkipInstall)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $InnoCompiler) {
  $innoCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($innoCommand) { $InnoCompiler = $innoCommand.Source }
  else { $InnoCompiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
}
if (-not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) { throw '未找到 Inno Setup 6 编译器，请通过 -InnoCompiler 指定 ISCC.exe 完整路径。' }
$version = (Get-Content -Raw -LiteralPath (Join-Path $repo 'package.json') | ConvertFrom-Json).version
$deps = Join-Path $repo '.build\deps'
$releaseRoot = Join-Path $repo 'artifacts'
$bootstrap = Join-Path $deps 'MicrosoftEdgeWebview2Setup.exe'
$language = Join-Path $repo 'installer\Languages\ChineseSimplified.isl'
New-Item -ItemType Directory -Force -Path $deps,$releaseRoot | Out-Null
if (-not (Test-Path -LiteralPath $language)) { throw '缺少简体中文 Inno Setup 语言文件，请检查 installer/Languages/ChineseSimplified.isl。' }
if (-not (Test-Path -LiteralPath $bootstrap)) {
  & curl.exe --fail --location --silent --show-error --retry 2 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' --output $bootstrap
  if ($LASTEXITCODE -ne 0) { throw 'WebView2 官方引导程序下载失败，请检查网络后重新构建。' }
}
# 安装器只嵌入微软签名的 WebView2 引导程序，避免下载错误页面后生成不可用安装包。
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrap
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'WebView2 引导程序的微软签名校验失败。请删除 .build/deps 中该文件后重新下载。' }
Push-Location $repo
try {
  if (-not $SkipInstall) { & npm.cmd ci --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw '前端依赖安装失败。' } }
  & npm.cmd run tauri -- build --no-bundle
  if ($LASTEXITCODE -ne 0) { throw 'Tauri/Rust 程序构建失败。' }
  $sourceDir = Join-Path $repo 'src-tauri\target\release'
  & $InnoCompiler "/DAppVersion=$version" "/DReleaseRoot=$releaseRoot" "/DSourceDir=$sourceDir" "/DWebViewBootstrap=$bootstrap" "/DChineseLanguage=$language" (Join-Path $repo 'installer\Shanyu.iss')
  if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 安装包编译失败。' }
  Write-Host "安装包已生成：$(Join-Path $releaseRoot "Shanyu-Setup-$version-x64.exe")"
} finally { Pop-Location }
