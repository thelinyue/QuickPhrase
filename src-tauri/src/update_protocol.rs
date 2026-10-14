//! GitHub 更新协议：代理仅改写固定官方地址，所有可执行内容由内置公钥认证。
//! latest 是不可信的发现线索，固定版本的清单、说明和安装器验证后才交给界面。
use futures_util::StreamExt;
use reqwest::{Client, Url};
use semver::Version;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{collections::HashMap, path::Path, time::Duration};
use tokio::io::AsyncWriteExt;
use tokio_util::sync::CancellationToken;

pub type Result<T> = std::result::Result<T, String>;
pub const RELEASES: &str = "https://github.com/thelinyue/QuickPhrase/releases";
pub const PUBLIC_KEY: &str = include_str!("../update.pub");
pub const MAX_INSTALLER: u64 = 512 * 1024 * 1024;

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", default)]
pub struct Config {
    pub automatic: bool,
    pub route: String,
    pub custom_proxy: String,
}
impl Default for Config {
    fn default() -> Self {
        Self {
            automatic: true,
            route: "default".into(),
            custom_proxy: String::new(),
        }
    }
}
impl Config {
    pub fn normalize(mut self) -> Result<Self> {
        if !["default", "backup", "custom", "direct"].contains(&self.route.as_str()) {
            return Err("请选择有效的 GitHub 下载线路。".into());
        }
        self.custom_proxy = self.custom_proxy.trim().into();
        if !self.custom_proxy.is_empty() {
            let mut url =
                Url::parse(&self.custom_proxy).map_err(|_| "代理地址无效，请填写 HTTPS 地址。")?;
            if url.scheme() != "https"
                || url.host_str().is_none()
                || !url.username().is_empty()
                || url.password().is_some()
                || url.query().is_some()
                || url.fragment().is_some()
            {
                return Err(
                    "代理地址必须使用 HTTPS，且不能包含账号、密码、查询参数或片段。".into(),
                );
            }
            if !url.path().ends_with('/') {
                url.set_path(&format!("{}/", url.path()));
            }
            self.custom_proxy = url.to_string();
        }
        if self.route == "custom" && self.custom_proxy.is_empty() {
            return Err("请填写自定义代理地址。".into());
        }
        Ok(self)
    }
    pub fn url(&self, original: &str) -> Result<String> {
        if !original.starts_with(&format!("{RELEASES}/")) {
            return Err("更新地址不属于官方发布仓库。".into());
        }
        let config = self.clone().normalize()?;
        let prefix = match config.route.as_str() {
            "default" => "https://gh-proxy.com/",
            "backup" => "https://ghfast.top/",
            "custom" => &config.custom_proxy,
            _ => "",
        };
        Ok(format!("{prefix}{original}"))
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Manifest {
    pub version: String,
    pub rid: String,
    pub release_channel: String,
    pub artifacts: Vec<String>,
    #[serde(default)]
    pub notes: String,
}
impl Manifest {
    pub fn version(&self) -> Result<Version> {
        let v = Version::parse(&self.version).map_err(|_| "更新版本号无效。")?;
        if !v.pre.is_empty()
            || !v.build.is_empty()
            || self.release_channel != "stable"
            || self.rid != "win-x64"
        {
            return Err("更新不是 Windows x64 正式版。".into());
        }
        Ok(v)
    }
    pub fn installer(&self) -> String {
        format!("QuickPhrase-Setup-{}-x64.exe", self.version)
    }
}

/// 已验证的清单只保留本次固定版本的安装器摘要，不接受前端提供的地址或摘要。
#[derive(Clone)]
pub struct Release {
    pub manifest: Manifest,
    pub hash: String,
}
pub fn sha256(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
pub fn verify_metadata(
    public_key: &str,
    checksums: &[u8],
    signature: &[u8],
    manifest: &[u8],
    version: &str,
) -> Result<Release> {
    let public = minisign_verify::PublicKey::decode(public_key)
        .map_err(|_| "更新公钥无效，请联系项目维护者。")?;
    let signature = std::str::from_utf8(signature)
        .ok()
        .and_then(|s| minisign_verify::Signature::decode(s).ok())
        .ok_or("更新签名缺失或格式错误，不能安装。")?;
    public
        .verify(checksums, &signature, false)
        .map_err(|_| "更新签名验证失败，不能安装。请更换下载线路重试。")?;
    let mut entries = HashMap::new();
    for line in std::str::from_utf8(checksums)
        .map_err(|_| "更新校验清单格式错误。")?
        .lines()
    {
        let (hash, name) = line.split_once("  ").ok_or("更新校验清单格式错误。")?;
        if hash.len() != 64
            || !hash.bytes().all(|c| c.is_ascii_hexdigit())
            || entries.insert(name, hash.to_ascii_lowercase()).is_some()
        {
            return Err("更新校验清单存在无效或重复条目。".into());
        }
    }
    if entries.get("release-manifest.json") != Some(&sha256(manifest)) {
        return Err("更新说明校验失败，不能安装。".into());
    }
    let manifest: Manifest =
        serde_json::from_slice(manifest).map_err(|_| "更新说明不是有效的 JSON 文件。")?;
    manifest.version()?;
    if manifest.version != version || !manifest.artifacts.contains(&manifest.installer()) {
        return Err("更新资产与版本不匹配。".into());
    }
    let hash = entries
        .get(manifest.installer().as_str())
        .ok_or("更新校验清单缺少安装器。")?
        .clone();
    Ok(Release { manifest, hash })
}

pub fn client() -> Result<Client> {
    Client::builder()
        .user_agent(concat!("QuickPhrase/", env!("CARGO_PKG_VERSION")))
        .connect_timeout(Duration::from_secs(15))
        .read_timeout(Duration::from_secs(30))
        .https_only(true)
        .redirect(reqwest::redirect::Policy::limited(5))
        .build()
        .map_err(|_| "无法创建更新网络连接。".into())
}
fn network_error(e: reqwest::Error) -> String {
    if e.is_timeout() {
        "更新连接超时，请切换线路后重试。".into()
    } else {
        "更新连接失败，请检查网络或切换下载线路。".into()
    }
}
pub async fn bytes(client: &Client, url: &str, limit: usize) -> Result<Vec<u8>> {
    let response = client
        .get(url)
        .timeout(Duration::from_secs(45))
        .send()
        .await
        .map_err(network_error)?;
    if !response.status().is_success() {
        return Err(format!(
            "更新文件无法获取（HTTP {}），请切换线路重试；旧版可能尚未提供更新签名。",
            response.status().as_u16()
        ));
    }
    let mut chunks = response.bytes_stream();
    let mut result = Vec::new();
    while let Some(chunk) = chunks.next().await {
        let chunk = chunk.map_err(network_error)?;
        if result.len() + chunk.len() > limit {
            return Err("更新信息过大，已停止读取。".into());
        }
        result.extend_from_slice(&chunk);
    }
    Ok(result)
}
pub async fn check(config: &Config, current: &str) -> Result<Option<Release>> {
    let client = client()?;
    let discovery = bytes(
        &client,
        &config.url(&format!("{RELEASES}/latest/download/release-manifest.json"))?,
        512 * 1024,
    )
    .await?;
    let candidate: Manifest = serde_json::from_slice(&discovery)
        .map_err(|_| "更新线路返回了无效内容，请切换线路重试。")?;
    let current = Version::parse(current).map_err(|_| "当前应用版本无效。")?;
    if candidate.version()? <= current {
        return Ok(None);
    }
    fixed_release(&client, config, &candidate.version)
        .await
        .map(Some)
}
pub async fn fixed_release(client: &Client, config: &Config, version: &str) -> Result<Release> {
    let v = Version::parse(version).map_err(|_| "更新版本号无效。")?;
    if !v.pre.is_empty() || !v.build.is_empty() {
        return Err("只能下载正式版本。".into());
    }
    let base = format!("{RELEASES}/download/v{version}");
    let sums = bytes(
        client,
        &config.url(&format!("{base}/SHA256SUMS.txt"))?,
        32 * 1024,
    )
    .await?;
    let signature = bytes(
        client,
        &config.url(&format!("{base}/SHA256SUMS.txt.minisig"))?,
        4096,
    )
    .await?;
    let manifest = bytes(
        client,
        &config.url(&format!("{base}/release-manifest.json"))?,
        512 * 1024,
    )
    .await?;
    verify_metadata(PUBLIC_KEY, &sums, &signature, &manifest, version)
}
pub async fn download(
    config: &Config,
    release: &Release,
    path: &Path,
    cancel: &CancellationToken,
    mut progress: impl FnMut(u64, Option<u64>),
) -> Result<()> {
    let client = client()?;
    let original = format!(
        "{RELEASES}/download/v{}/{}",
        release.manifest.version,
        release.manifest.installer()
    );
    let request = client.get(config.url(&original)?).send();
    let response = tokio::select! { biased; _ = cancel.cancelled() => return Err("下载已取消。".into()), response = request => response }
        .map_err(network_error)?
        .error_for_status()
        .map_err(network_error)?;
    save_download(response, path, cancel, &release.hash, &mut progress).await
}

/// 响应读取与落盘共用实现，网络验收与故障测试均经过实际流式校验。
async fn save_download(
    response: reqwest::Response,
    path: &Path,
    cancel: &CancellationToken,
    expected: &str,
    progress: &mut impl FnMut(u64, Option<u64>),
) -> Result<()> {
    let total = response.content_length();
    if total.is_some_and(|n| n > MAX_INSTALLER) {
        return Err("更新安装包过大，已停止下载。".into());
    }
    let mut file = tokio::fs::File::create(path)
        .await
        .map_err(|_| "无法创建更新临时文件，请检查磁盘空间和权限。")?;
    let mut chunks = response.bytes_stream();
    let mut done = 0;
    let mut hash = Sha256::new();
    while let Some(chunk) = tokio::select! { biased; _ = cancel.cancelled() => {
        // 等待已提交的异步写入结束再关闭句柄，Windows 才能可靠清理半份文件。
        let _ = file.flush().await;
        return Err("下载已取消。".into());
    }, next = chunks.next() => next }
    {
        let chunk = chunk.map_err(network_error)?;
        done += chunk.len() as u64;
        if done > MAX_INSTALLER {
            return Err("更新安装包过大，已停止下载。".into());
        }
        file.write_all(&chunk)
            .await
            .map_err(|_| "更新文件写入失败，请检查磁盘空间。")?;
        hash.update(&chunk);
        progress(done, total);
    }
    file.flush().await.map_err(|_| "更新文件保存失败。")?;
    file.sync_all().await.map_err(|_| "更新文件保存失败。")?;
    if cancel.is_cancelled() {
        return Err("下载已取消。".into());
    }
    if format!("{:x}", hash.finalize()) != expected {
        return Err("安装包校验失败，已拒绝安装。请切换线路重新下载。".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture() -> (String, Vec<u8>, Vec<u8>, Vec<u8>) {
        let pair = minisign::KeyPair::generate_unencrypted_keypair().unwrap();
        let manifest = serde_json::to_vec(&Manifest {
            version: "1.2.3".into(),
            rid: "win-x64".into(),
            release_channel: "stable".into(),
            artifacts: vec!["QuickPhrase-Setup-1.2.3-x64.exe".into()],
            notes: "新版说明".into(),
        })
        .unwrap();
        let sums = format!(
            "{}  release-manifest.json\n{}  QuickPhrase-Setup-1.2.3-x64.exe\n",
            sha256(&manifest),
            sha256(b"installer")
        )
        .into_bytes();
        let signature = minisign::sign(
            Some(&pair.pk),
            &pair.sk,
            std::io::Cursor::new(&sums),
            None,
            None,
        )
        .unwrap()
        .to_string()
        .into_bytes();
        (
            pair.pk.to_box().unwrap().to_string(),
            sums,
            signature,
            manifest,
        )
    }
    #[test]
    fn proxy_routes_normalize_and_reject_unsafe_urls() {
        let origin = format!("{RELEASES}/latest/download/release-manifest.json");
        for (route, prefix) in [
            ("default", "https://gh-proxy.com/"),
            ("backup", "https://ghfast.top/"),
            ("direct", ""),
            ("custom", "https://example.com/prefix/"),
        ] {
            let config = Config {
                route: route.into(),
                custom_proxy: " https://example.com/prefix ".into(),
                ..Config::default()
            };
            assert_eq!(config.url(&origin).unwrap(), format!("{prefix}{origin}"));
        }
        for address in [
            "http://example.com",
            "https://user:pass@example.com",
            "https://example.com/?token=x",
            "https://example.com/#x",
            "not a url",
            "",
        ] {
            assert!(Config {
                route: "custom".into(),
                custom_proxy: address.into(),
                ..Config::default()
            }
            .normalize()
            .is_err());
        }
        assert!(Config::default()
            .url("https://github.com/attacker/release")
            .is_err());
    }
    #[test]
    fn signature_authenticates_both_manifest_and_installer() {
        let (key, sums, sig, manifest) = fixture();
        let release = verify_metadata(&key, &sums, &sig, &manifest, "1.2.3").unwrap();
        assert_eq!(release.hash, sha256(b"installer"));
        assert_eq!(release.manifest.notes, "新版说明");
        assert!(verify_metadata(&key, &sums, b"", &manifest, "1.2.3").is_err());
        let wrong_key = fixture().0;
        assert!(verify_metadata(&wrong_key, &sums, &sig, &manifest, "1.2.3").is_err());
        let forged_sums = String::from_utf8(sums.clone())
            .unwrap()
            .replace(&sha256(b"installer"), &sha256(b"evil installer"));
        assert!(verify_metadata(&key, forged_sums.as_bytes(), &sig, &manifest, "1.2.3").is_err());
        let mut bad_signature = sig.clone();
        bad_signature[50] ^= 1;
        assert!(verify_metadata(&key, &sums, &bad_signature, &manifest, "1.2.3").is_err());
        assert!(verify_metadata(&key, &sums, &sig, b"forged manifest", "1.2.3").is_err());
        assert!(verify_metadata(&key, &sums, &sig, &manifest, "1.2.4").is_err());
    }
    #[test]
    fn semver_filters_prerelease_platform_and_lexical_order() {
        let mut m: Manifest = serde_json::from_slice(&fixture().3).unwrap();
        m.version = "0.10.0".into();
        assert!(m.version().unwrap() > Version::parse("0.9.9").unwrap());
        assert!(Version::parse("1.0.0").unwrap() > Version::parse("1.0.0-rc.1").unwrap());
        for version in ["1.2.3-rc.1", "1.2.3+build", "../outside", "v1.2.3"] {
            m.version = version.into();
            assert!(m.version().is_err());
        }
        m.version = "1.2.3".into();
        m.rid = "linux-x64".into();
        assert!(m.version().is_err());
        m.rid = "win-x64".into();
        m.release_channel = "prerelease".into();
        assert!(m.version().is_err());
    }
    #[test]
    fn embedded_public_key_is_valid() {
        minisign_verify::PublicKey::decode(PUBLIC_KEY).unwrap();
    }

    fn server(
        body: &'static [u8],
        declared: usize,
        delay: u64,
    ) -> (String, std::thread::JoinHandle<()>) {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        let thread = std::thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            let _ = socket.read(&mut [0; 4096]);
            write!(
                socket,
                "HTTP/1.1 200 OK\r\nContent-Length: {declared}\r\nConnection: close\r\n\r\n"
            )
            .unwrap();
            socket.flush().unwrap();
            std::thread::sleep(Duration::from_millis(delay));
            let _ = socket.write_all(body);
        });
        (url, thread)
    }
    #[tokio::test]
    async fn stream_download_checks_bytes_cancellation_truncation_and_disk_error() {
        for (body, declared, cancelled, expected_error) in [
            (&b"installer"[..], 9, false, ""),
            (&b"evil"[..], 4, false, "校验失败"),
            (&b"cut"[..], 99, false, "连接失败"),
            (&b"installer"[..], 9, true, "已取消"),
        ] {
            let (url, thread) = server(body, declared, 20);
            let response = Client::new().get(url).send().await.unwrap();
            let temp = tempfile::tempdir().unwrap();
            let output = temp.path().join("setup.exe");
            let cancel = CancellationToken::new();
            if cancelled {
                cancel.cancel();
            }
            let mut progress = Vec::new();
            let result = save_download(
                response,
                &output,
                &cancel,
                &sha256(b"installer"),
                &mut |done, _| progress.push(done),
            )
            .await;
            if expected_error.is_empty() {
                assert!(result.is_ok());
                assert_eq!(progress.last(), Some(&9));
            } else {
                assert!(result.unwrap_err().contains(expected_error));
            }
            thread.join().unwrap();
            drop(temp);
            assert!(!output.exists());
        }
        let (url, thread) = server(b"installer", 9, 0);
        let response = Client::new().get(url).send().await.unwrap();
        let dir = tempfile::tempdir().unwrap();
        let error = save_download(
            response,
            dir.path(),
            &CancellationToken::new(),
            &sha256(b"installer"),
            &mut |_, _| {},
        )
        .await
        .unwrap_err();
        assert!(error.contains("创建更新临时文件"));
        thread.join().unwrap();
    }
    #[tokio::test]
    async fn metadata_limits_and_timeout_do_not_accept_partial_or_html_response() {
        let (url, thread) = server(b"too large", 9, 0);
        assert!(bytes(&Client::new(), &url, 2)
            .await
            .unwrap_err()
            .contains("过大"));
        thread.join().unwrap();
        let (url, thread) = server(b"<html>proxy error</html>", 24, 0);
        let html = bytes(&Client::new(), &url, 100).await.unwrap();
        assert!(serde_json::from_slice::<Manifest>(&html).is_err());
        thread.join().unwrap();
        let (url, thread) = server(b"delay", 5, 100);
        let client = Client::builder()
            .read_timeout(Duration::from_millis(20))
            .build()
            .unwrap();
        assert!(bytes(&client, &url, 100)
            .await
            .unwrap_err()
            .contains("超时"));
        thread.join().unwrap();
    }
}
