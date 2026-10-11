param([string]$Version)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$repo = Split-Path -Parent $PSScriptRoot
$release = & (Join-Path $PSScriptRoot 'check-release.ps1') -Version $Version
$Version = $release.Version
$program = Join-Path $repo 'src-tauri\target\release\shanyu.exe'
$installer = Join-Path $repo "artifacts\Shanyu-Setup-$Version-x64.exe"

# 两条工作流仅打包明确指定的当前产物，不扫描 artifacts，防止混入旧版本或用户文件。
foreach ($asset in @($program, $installer)) {
  if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { throw "缺少构建产物：$asset" }
  $info = (Get-Item -LiteralPath $asset).VersionInfo
  $numericVersion = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
  # Inno 为版本文本预留空间，读取的字符串可能包含尾部填充；数值版本仍精确比较。
  if (([string]$info.ProductVersion).Trim() -ne $Version -or $numericVersion -ne $release.FileVersion) { throw "构建产物版本不一致：$asset" }
  if ((Get-AuthenticodeSignature -LiteralPath $asset).Status -ne 'NotSigned') { throw '当前发布策略仅支持未签名资产，请先更新签名政策及打包流程。' }
}
$revision = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw '无法读取发布来源提交。' }
$output = Join-Path $repo "artifacts\release\$Version"
$archive = Join-Path $output "Shanyu-$Version-win-x64.zip"
$setup = Join-Path $output "Shanyu-Setup-$Version-x64.exe"
New-Item -ItemType Directory -Force -Path $output | Out-Null
$policies = @('LICENSE', 'PRIVACY.md', 'SECURITY.md', 'CODE_SIGNING.md') | ForEach-Object { Join-Path $repo $_ }
Compress-Archive -LiteralPath (@($program) + $policies) -DestinationPath $archive -CompressionLevel Optimal -Force
Copy-Item -LiteralPath $installer -Destination $setup -Force
$assets = @($archive, $setup)
$hashLines = foreach ($asset in $assets) { '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $asset).Hash, (Split-Path -Leaf $asset) }
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), [string[]]$hashLines, [Text.UTF8Encoding]::new($false))
$manifest = [ordered]@{
  version = $Version
  fileVersion = $release.FileVersion
  rid = 'win-x64'
  buildTimeUtc = [DateTime]::UtcNow.ToString('O')
  rustVersion = (& rustc --version)
  nodeVersion = (& node --version)
  signed = $false
  releaseChannel = $release.ReleaseChannel
  supportedOs = 'Windows 11 x64'
  windows10Status = 'Windows 10 1607+ 可运行，尚待实机验收'
  sourceRevision = $revision.Trim()
  artifacts = @($assets | ForEach-Object { Split-Path -Leaf $_ })
}
[IO.File]::WriteAllText((Join-Path $output 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Host "发布资产已打包：$output（未签名；尚未发布）"
