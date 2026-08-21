# QuickPhrase Phase 6 Windows 11 验证记录

状态：Windows 11 人工矩阵已由发布负责人明确确认通过。
最终门禁：正式签名资产尚未生成和验证，因此暂不写入 `PHASE6_VERIFY_PASS_WIN11`。

## 已通过的工程门禁

| 项目 | 结果 |
| --- | --- |
| .NET SDK | 10.0.400 |
| 正式产品边界 | `.NET 10 LTS + Pure WPF`；生产项目不依赖 WebView2、React、网页资源或外部运行环境 |
| 主分支 CI | `windows-ci` run `32404969586` 通过；Desktop 232/232，Architecture 245/245，0 warning / 0 error |
| 候选构建 | `release-candidate-build` run `32404984862` 通过，源代码修订为 `b8c2e66bfc6b54f2dd13124cf7ec4eaae3d13e78` |
| Self-contained 发布 | `win-x64`、ReadyToRun、非裁剪、非单文件 |
| Launcher smoke | Native smoke 通过；真实 `LauncherWindow` 生命周期复用，独立测试数据，结束后无残留测试进程 |
| Launcher 热呼出性能 | 预热后 200 次：P50 `59.722ms`、P95 `84.789ms`、P99 `95.665ms`；P95 满足 `≤120ms` 门槛 |
| 冷启动 | `3465.420ms`，单独记录，不计入 Launcher 热呼出发布门槛 |
| 未签名候选 | `v0.0.1-rc.1` 已公开为 Pre-release，资产哈希与 Actions artifact 一致 |

## 人工验收结论

### 企业微信

当前主流版本企业微信人工安全矩阵已由发布负责人明确确认通过，验收范围和安全边界见 [phase5-validation.md](phase5-validation.md)。自动化测试不替代真实企业微信 GUI 验收，也不把 `SendTriggered` 误写为目标应用最终 `Sent`。

### Windows 11

2026-08-21，发布负责人明确确认 Windows 11 人工矩阵通过。该确认覆盖本阶段定义的 Windows 11 x64 安装与运行范围：

- 当前用户安装与首次启动。
- 自包含运行，不依赖预装 .NET Runtime。
- 升级路径及升级前数据备份。
- 开机启动、单实例、托盘和 Launcher 冷/热启动。
- 卸载后保留 `Data`、`Backups` 与 `Logs`，重装后恢复使用原数据。
- 发布目录不包含 WebView2 Runtime、网页 bundle 或其他生产外部环境依赖。

本记录只保存发布负责人的明确验收结论，不伪造 TraceId、截图编号或自动化无法取得的人工观察数据。Windows 10 固定记录为 `UNVERIFIED / NOT SUPPORTED IN V0.0.1`。

## 未签名候选证据

- Release：https://github.com/thelinyue/QuickPhrase/releases/tag/v0.0.1-rc.1
- Candidate workflow：https://github.com/thelinyue/QuickPhrase/actions/runs/32404984862
- CI workflow：https://github.com/thelinyue/QuickPhrase/actions/runs/32404969586
- 应用包：`QuickPhrase-0.0.1-rc.1-win-x64-unsigned.zip`
- 安装器：`QuickPhrase-Setup-0.0.1-rc.1-unsigned.exe`

候选版未签名，Windows SmartScreen 可能显示未知发布者警告；它只用于候选测试和 SignPath Foundation 申请，不是正式版。

## 最终门禁仍待完成

在以下事项完成前，不创建或发布正式版 `v0.0.1`，也不运行最终 Phase 6 验证：

1. SignPath Foundation 批准 QuickPhrase Open Source Project。
2. GitHub App、Secret、Variables、Signing Policy 和两个 Artifact Configuration 配置完成。
3. 应用四个 QuickPhrase 自有 PE 与安装器均取得有效 Authenticode 签名和可信时间戳。
4. 签名资产的 manifest、SHA-256、来源 revision 和 GitHub Actions provenance 核对通过。
5. 发布负责人对签名正式版 tag、commit 和最终资产给予新的明确发布批准。

完整门禁命令只允许对已经生成的 signed stable `0.0.1` 资产执行：

```powershell
$env:QUICKPHRASE_WECOM_ACCEPTANCE = "passed"
$env:QUICKPHRASE_WIN11_ACCEPTANCE = "passed"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase6.ps1 -Version 0.0.1
```

该脚本还会验证正式资产存在、`release-manifest.json` 声明 `signed=true` 和 `releaseChannel=stable`、哈希一致，以及应用与安装器均具有有效签名和时间戳。只有脚本在真实签名资产上通过后，才允许记录 `PHASE6_VERIFY_PASS_WIN11`。
