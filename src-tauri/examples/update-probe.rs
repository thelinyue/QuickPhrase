//! 人工网络验收：调用产品相同的 Rust 请求和流式下载；不启动安装器或改动用户数据。
//! 历史正式版尚无更新签名时，仅用直连官方清单作本次下载比对，不授予产品安装权限。
#[allow(dead_code)]
#[path = "../src/update_protocol.rs"]
mod update_protocol;
use update_protocol::*;
#[tokio::main(flavor = "current_thread")]
async fn main() -> std::result::Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = std::env::args().collect();
    if args.get(1).is_some_and(|v| v == "local") {
        let root = std::path::Path::new(args.get(2).ok_or("请提供本地发布资产目录")?);
        let manifest = std::fs::read(root.join("release-manifest.json"))?;
        let candidate: Manifest = serde_json::from_slice(&manifest)?;
        let release = verify_metadata(
            PUBLIC_KEY,
            &std::fs::read(root.join("SHA256SUMS.txt"))?,
            &std::fs::read(root.join("SHA256SUMS.txt.minisig"))?,
            &manifest,
            &candidate.version,
        )?;
        let executable = std::fs::read(root.join(release.manifest.installer()))?;
        if sha256(&executable) != release.hash {
            return Err("安装包校验失败".into());
        }
        println!(
            "{}",
            serde_json::json!({"version": release.manifest.version, "signatureVerified": true, "manifestVerified": true, "installerVerified": true, "bytes": executable.len(), "installerExecuted": false})
        );
        return Ok(());
    }
    let config = Config {
        route: args.get(1).cloned().unwrap_or("direct".into()),
        custom_proxy: args.get(2).cloned().unwrap_or_default(),
        ..Config::default()
    }
    .normalize()?;
    let client = client()?;
    let manifest: Manifest = serde_json::from_slice(
        &bytes(
            &client,
            &config.url(&format!("{RELEASES}/latest/download/release-manifest.json"))?,
            512 * 1024,
        )
        .await?,
    )?;
    manifest.version()?;
    let official_sums = bytes(
        &client,
        &format!("{RELEASES}/download/v{}/SHA256SUMS.txt", manifest.version),
        32 * 1024,
    )
    .await?;
    let sums = std::str::from_utf8(&official_sums)?;
    let hash = sums
        .lines()
        .find_map(|line| {
            line.split_once("  ")
                .filter(|(_, name)| *name == manifest.installer())
                .map(|(hash, _)| hash.to_ascii_lowercase())
        })
        .ok_or("官方清单缺少安装器")?;
    let release = Release { manifest, hash };
    let dir = tempfile::tempdir()?;
    let path = dir.path().join(release.manifest.installer());
    download(
        &config,
        &release,
        &path,
        &tokio_util::sync::CancellationToken::new(),
        |_, _| {},
    )
    .await?;
    println!(
        "{}",
        serde_json::json!({ "route": config.route, "version": release.manifest.version, "bytes": std::fs::metadata(path)?.len(), "matchesOfficialChecksum": true, "installerExecuted": false })
    );
    Ok(())
}
