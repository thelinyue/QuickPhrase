# QuickPhrase Phase 6 Windows 11 验证记录

状态：`PHASE6_INFRA_PASS`  
最终门禁：尚未写入 `PHASE6_VERIFY_PASS_WIN11`。企业微信人工矩阵与 Windows 11 VM/Sandbox 安装矩阵仍需由发布负责人完成。

## 已通过的工程门禁

| 项目 | 结果 |
| --- | --- |
| .NET SDK | 10.0.400 |
| Debug build/test | 通过，0 warning，63/63 |
| Release build/test | 通过，0 warning，63/63 |
| React build | 通过 |
| Management-only build | 通过，独立 `dist/management` |
| Sites tests | 通过，4/4 |
| Inno Setup | 6.7.3，online/offline 均编译成功 |
| Self-contained 发布 | `win-x64`、ReadyToRun、非裁剪、非单文件 |
| EXE 版本 | FileVersion `1.0.0.0`，ProductVersion `1.0.0` |
| 管理资源 | 仅包含 `Web/management.html` 及实际依赖，不含原型 `index.html` 或演示壁纸 |
| 发布清单 | 已生成 `release-manifest.json` 与 `SHA256SUMS.txt` |

## 产物

- [在线安装器](../artifacts/release/1.0.0/installers/QuickPhrase-Setup-1.0.0-online.exe)
- [离线安装器](../artifacts/release/1.0.0/installers/QuickPhrase-Setup-1.0.0-offline.exe)
- [SHA256SUMS](../artifacts/release/1.0.0/SHA256SUMS.txt)
- [release-manifest.json](../artifacts/release/1.0.0/release-manifest.json)

发布版中文产品名为“闪语”；程序文件、安装目录、AppId 和数据路径保留 QuickPhrase，确保升级兼容。安装器未签名，SmartScreen 未知发布者提示属于已知限制。

## 尚待人工确认

1. 企业微信 `5.0.9.6065` 关闭全局兼容模式后仍固定走 Clipboard 路径。
2. 5 条连续真实插入按 FIFO、每条只插入一次且不发送；累计至少 30 次，单条执行 P95 ≤ 300ms。
3. Windows 11 x64 冷启动 10 次、热打开/关闭 20 次，Management Ready 分别满足 P95 ≤ 2s / ≤ 1s。
4. 在线/离线安装、无 Runtime、已有 Runtime、升级前备份、卸载保留数据和重装恢复话术。
5. 管理窗口关闭后无活跃 WebView2 Controller，并在无其他 WebView 时收到 BrowserProcessExited。
6. 稳定空闲五分钟内无 QuickPhrase 可归因的周期性持久化写入。

完整门禁命令：

```powershell
$env:QUICKPHRASE_WECOM_ACCEPTANCE = "passed"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-phase6.ps1 -IncludeDesktopSmoke
```

只有上述人工项目全部有时间、TraceId 或安装矩阵证据，才允许将状态改为 `PHASE6_VERIFY_PASS_WIN11`。Windows 10 固定记录为 `UNVERIFIED / NOT SUPPORTED IN V1.0.0`。
