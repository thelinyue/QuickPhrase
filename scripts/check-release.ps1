param(
  [string]$Version,
  [ValidateSet('build', 'prerelease', 'stable')][string]$Channel = 'build',
  [string]$GitRef = $env:GITHUB_REF,
  [switch]$ConfirmWeComAcceptance,
  [switch]$ConfirmWin11Acceptance
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repo = Split-Path -Parent $PSScriptRoot

# 构建和两条手动工作流共用版本校验，避免安装器、运行时和源码使用不同版本。
# build 只生成本地产物；人工验收和标签限制在手动候选／正式发布入口执行。
$baseVersion = (Get-Content -Raw -LiteralPath (Join-Path $repo 'package.json') | ConvertFrom-Json).version
$tauriVersion = (Get-Content -Raw -LiteralPath (Join-Path $repo 'src-tauri\tauri.conf.json') | ConvertFrom-Json).version
$cargo = Get-Content -Raw -LiteralPath (Join-Path $repo 'src-tauri\Cargo.toml')
$package = [regex]::Match($cargo, '(?ms)^\[package\][ \t]*\r?\n(.*?)(?=^\[|\z)').Groups[1].Value
$cargoVersion = [regex]::Match($package, '(?m)^version\s*=\s*"([^"]+)"').Groups[1].Value
if ($baseVersion -notmatch '^\d+\.\d+\.\d+$' -or $baseVersion -ne $tauriVersion -or $baseVersion -ne $cargoVersion) {
  throw 'package.json、Cargo.toml 和 tauri.conf.json 的稳定版本必须一致。'
}
if (-not $Version) {
  if ($Channel -ne 'build') { throw '手动发布必须提供版本。' }
  $Version = $baseVersion
}
$candidatePattern = '^' + [regex]::Escape($baseVersion) + '-rc\.[1-9]\d*$'
if ($Version -ne $baseVersion -and $Version -notmatch $candidatePattern) {
  throw "版本必须是 $baseVersion 或 $baseVersion-rc.N（N 为正整数）。"
}
if ($Channel -ne 'build' -and -not $ConfirmWeComAcceptance) { throw '尚未确认企业微信人工矩阵。' }
if ($Channel -eq 'prerelease') {
  if ($GitRef -ne 'refs/heads/master') { throw '候选构建只能从 master 手动运行。' }
  if ($Version -notmatch $candidatePattern) { throw '候选构建必须使用 rc.N 版本。' }
}
if ($Channel -eq 'stable') {
  if ($Version -ne $baseVersion) { throw '正式发布版本必须与源码稳定版本一致。' }
  if ($GitRef -ne "refs/tags/v$Version") { throw "正式发布必须从 refs/tags/v$Version 手动运行。" }
  if (-not $ConfirmWin11Acceptance) { throw '尚未确认 Windows 11 安装矩阵。' }
  $headRevision = & git -C $repo rev-parse HEAD
  if ($LASTEXITCODE -ne 0) { throw '无法读取源码提交。' }
  $tagRevision = & git -C $repo rev-parse --verify --quiet "$GitRef^{commit}"
  if ($LASTEXITCODE -ne 0 -or $tagRevision -ne $headRevision) { throw '正式标签不存在或没有指向当前源码提交。' }
}
[pscustomobject]@{ Version = $Version; FileVersion = "$baseVersion.0"; ReleaseChannel = $(if ($Version -eq $baseVersion) { 'stable' } else { 'prerelease' }) }
