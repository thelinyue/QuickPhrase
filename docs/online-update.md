# GitHub 在线更新

设置 → 关于提供自动检查、手动检查、下载、取消和退出安装。默认启动30秒后检查，每24小时至多自动检查一次；手动检查不受间隔限制。自动检查失败不弹窗，发现已验证新版时在主窗口显示可关闭提示，不抢焦点。关闭设置窗口不会取消下载，退出整个应用后需重新检查和下载。

下载线路包括默认 `https://gh-proxy.com/`、备用 `https://ghfast.top/`、自定义和不使用 GitHub 代理。自定义代理采用 `https://代理地址/https://github.com/...` 格式，可包含路径前缀，不支持账号密码、查询参数或片段。修改后点击“保存并检查”。检查和下载期间不能修改线路，可取消下载后更换。公共代理不保证持续可用，不自动轮换线路、不做断点续传；失败后手动重试。线路只影响更新请求，不修改系统代理或 Hub 连接。直连仍遵循网络库支持的环境代理设置。

## 来源与验证

客户端只读取 `thelinyue/QuickPhrase` 的 Release 文件，不调用 GitHub API。`releases/latest/download/release-manifest.json` 仅用于发现版本；显示新版说明之前，重新读取固定 `v版本` 下的清单、签名和 manifest，并核对版本、平台、正式通道与资产名。所有实际下载固定到已选版本，不能因 latest 变化混用资产；当前或较旧版本不触发下载。

既有 `SHA256SUMS.txt` 覆盖安装器、ZIP和 `release-manifest.json`，由 Minisign 生成 `SHA256SUMS.txt.minisig`。产品公钥保存在 `src-tauri/update.pub` 并嵌入程序。客户端先验证清单签名，再核对文件SHA256，安装前再次读取安装包校验，并在Windows保持禁止写入／删除的读取句柄直到进程创建成功。缺少签名、签名错误、文件变化均禁止安装。

签名用于防止第三方代理同时替换安装包和校验清单，不等于Windows Authenticode代码签名。代理可能缓存旧版或不可用；签名不能保证代理总能提供最新版本。历史无签名资产保持原样，不允许应用内安装；首个带更新能力的版本仍需从现有下载入口安装，之后的新签名版本才能通过应用内升级。

## 安装与数据

下载完成后用户选择“退出并安装”。存在编辑、排序、管理窗口或正在发送、导入导出、云文件传输时拒绝安装。更新交接与这些操作互斥，不强制结束未完成任务。安装器启动失败保留应用，恢复可操作状态；成功启动后退出，不提前宣称安装成功。

安装版使用现有Inno Setup安装记录沿用目录；免安装版转为安装版，原文件夹保留。安装器仍提供正常向导和完成后启动选项，升级保留数据及自定义数据目录。取消下载清理未完成文件；交接成功的临时安装器保留供安装器运行，由系统临时文件清理机制后续回收。

## 发布维护

负责人手动发布时沿用已有源码版本／标签一致校验，新增以下 GitHub Actions Secrets：

- `QUICKPHRASE_UPDATE_PRIVATE_KEY`：加密的Minisign私钥全文。
- `QUICKPHRASE_UPDATE_KEY_PASSWORD`：解密该私钥的密码。

私钥与密码不得进入仓库、日志或客户端。首次配置使用 `cargo run --manifest-path src-tauri/Cargo.toml --example update-sign -- generate <受限密钥目录>`，密码从 `QUICKPHRASE_UPDATE_KEY_PASSWORD` 环境变量读取；生成后仅将 `update.pub` 复制到 `src-tauri/update.pub`。保管加密私钥及密码的独立备份，不覆盖已使用的密钥。丢失或更换密钥会影响已安装客户端识别后续更新，必须先安排可信迁移版本。

本地打包前在当前进程注入上述两个环境变量，再执行既有 `scripts/package-release.ps1`。脚本从 `docs/releases/v版本.md` 读取更新说明写入manifest，候选版本沿用对应稳定版本说明；生成原有资产及签名，并以产品公钥验证签名。密钥缺失或不匹配时打包失败，不产生可发布更新包。正式版及候选工作流上传签名文件，仍不覆盖旧标签或资产。

## 验证命令

常规检查沿用README。额外的真实Rust下载检查：

```powershell
cargo build --locked --manifest-path src-tauri/Cargo.toml --example update-probe --example update-sign
src-tauri/target/debug/examples/update-probe.exe direct
src-tauri/target/debug/examples/update-probe.exe default
src-tauri/target/debug/examples/update-probe.exe backup
src-tauri/target/debug/examples/update-probe.exe custom https://gh-proxy.com/
src-tauri/target/debug/examples/update-probe.exe local artifacts/release/0.2.3
```

网络探测只比对直连官方清单并删除临时下载，不执行安装器，也不把无签名历史包标记为可安装。`local` 模式使用产品公钥验证本地签名清单、manifest及安装器，发布时将示例目录换为实际新版本目录。

`node scripts/verify-updater.mjs` 使用隐藏Debug客户端、独立数据库和实际签名夹具，需要当前进程提供签名环境变量。密钥只传给签名工具，不传给测试客户端。Debug夹具入口仅在 `SHANYU_TEST_DATA_DIR` 匹配实际数据目录时存在且仍验证产品公钥；Release完全不编译该入口。成功交接使用只写标记的测试程序，不能替代实际Inno向导升级、企业微信或DPI验收。
