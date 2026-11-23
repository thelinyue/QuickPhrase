//! 商店只提供发现与传输；安装信任仍来自内置 Minisign 公钥。
use super::*;

pub(super) struct Failure {
    pub message: String,
    pub retryable: bool,
}
impl From<String> for Failure {
    fn from(message: String) -> Self {
        Self {
            message,
            retryable: false,
        }
    }
}
fn network(error: reqwest::Error) -> Failure {
    Failure {
        // reqwest 将响应流截断归为 decode；JSON 解析和验签错误单独返回，不进入网络回退。
        retryable: error.is_connect() || error.is_timeout() || error.is_body() || error.is_decode(),
        message: format!("工具商店连接失败：{error}"),
    }
}
pub(super) fn root(value: &str) -> Result<Url> {
    let url = Url::parse(&format!("{}/", value.trim().trim_end_matches('/')))
        .map_err(|_| "工具商店地址无效。")?;
    if !matches!(url.scheme(), "http" | "https")
        || url.host_str().is_none()
        || credentials(&url)
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err("工具商店须为无凭据、查询参数及片段的 HTTP/HTTPS 根地址。".into());
    }
    Ok(url)
}
fn connection(base: &Url) -> Result<Client> {
    let base = base.clone();
    Client::builder()
        .connect_timeout(Duration::from_secs(5))
        .read_timeout(Duration::from_secs(30))
        .redirect(reqwest::redirect::Policy::custom(move |attempt| {
            if attempt.previous().len() >= 5 || !within(&base, attempt.url()) {
                attempt.error("工具商店重定向越界")
            } else {
                attempt.follow()
            }
        }))
        .build()
        .map_err(|_| "无法创建工具商店连接。".into())
}
fn within(base: &Url, url: &Url) -> bool {
    base.origin() == url.origin()
        && url.path().starts_with(base.path())
        && url.query().is_none()
        && url.fragment().is_none()
        && !credentials(url)
}
fn credentials(url: &Url) -> bool {
    url.as_str()
        .split("://")
        .nth(1)
        .unwrap_or_default()
        .split('/')
        .next()
        .unwrap_or_default()
        .contains('@')
}
fn address(base: &Url, value: &str) -> Result<Url> {
    let url = base.join(value).map_err(|_| "工具商店附件地址无效。")?;
    if !within(base, &url) {
        return Err("工具商店附件超出软件目录。".into());
    }
    Ok(url)
}
async fn response(client: &Client, url: Url) -> std::result::Result<reqwest::Response, Failure> {
    let response = client.get(url).send().await.map_err(network)?;
    if response.status() != reqwest::StatusCode::OK {
        return Err(Failure {
            retryable: response.status().is_server_error(),
            message: format!(
                "工具商店返回 HTTP {}，请检查商店地址和匿名访问设置。",
                response.status()
            ),
        });
    }
    Ok(response)
}
async fn material(client: &Client, url: Url) -> std::result::Result<Vec<u8>, Failure> {
    let mut chunks = response(client, url).await?.bytes_stream();
    let mut data = Vec::new();
    while let Some(chunk) = chunks.next().await {
        let chunk = chunk.map_err(network)?;
        if data.len() + chunk.len() > 512 * 1024 {
            return Err("工具商店元数据过大。".to_string().into());
        }
        data.extend_from_slice(&chunk);
    }
    Ok(data)
}
#[derive(Deserialize)]
struct File {
    name: String,
    url: String,
}
#[derive(Deserialize)]
struct Installer {
    name: String,
    url: String,
    size: u64,
    sha256: String,
}
#[derive(Deserialize)]
struct Verification {
    r#type: String,
    files: Vec<File>,
}
#[derive(Deserialize)]
struct Metadata {
    app_id: String,
    version: String,
    platform: String,
    arch: String,
    installer: Installer,
    verification: Verification,
}

pub(super) async fn release(
    store: &str,
    version: Option<&str>,
) -> std::result::Result<Release, Failure> {
    release_with_key(store, version, PUBLIC_KEY).await
}
pub(super) async fn release_with_key(
    store: &str,
    version: Option<&str>,
    key: &str,
) -> std::result::Result<Release, Failure> {
    tokio::time::timeout(Duration::from_secs(5), read_release(store, version, key))
        .await
        .unwrap_or_else(|_| {
            Err(Failure {
                message: "工具商店检查超时，请重试或修改地址。".into(),
                retryable: true,
            })
        })
}
async fn read_release(
    store: &str,
    version: Option<&str>,
    key: &str,
) -> std::result::Result<Release, Failure> {
    let base = root(store)?
        .join("api/updates/v1/quickphrase/")
        .map_err(|_| "工具商店路径无效。".to_string())?;
    let client = connection(&base)?;
    let route = version.map_or_else(
        || "latest".to_string(),
        |version| format!("versions/{version}"),
    );
    let url = base
        .join(&format!("{route}?platform=windows&arch=x64"))
        .map_err(|_| "工具商店查询路径无效。".to_string())?;
    let data = material(&client, url).await?;
    let metadata: Metadata =
        serde_json::from_slice(&data).map_err(|_| "工具商店响应格式错误。".to_string())?;
    if metadata.app_id != "quickphrase"
        || metadata.platform != "windows"
        || metadata.arch != "x64"
        || metadata.verification.r#type != "minisign-sha256sums"
        || version.is_some_and(|version| version != metadata.version)
    {
        return Err("工具商店的软件、版本或验证协议不匹配。".to_string().into());
    }
    let parsed = Version::parse(&metadata.version).map_err(|_| "工具商店版本无效。".to_string())?;
    if !parsed.pre.is_empty() || !parsed.build.is_empty() {
        return Err("工具商店版本不是正式版。".to_string().into());
    }
    let mut materials = Vec::new();
    for name in [
        "SHA256SUMS.txt",
        "SHA256SUMS.txt.minisig",
        "release-manifest.json",
    ] {
        let files: Vec<_> = metadata
            .verification
            .files
            .iter()
            .filter(|file| file.name == name)
            .collect();
        if files.len() != 1 {
            return Err("工具商店缺少唯一的原始签名附件。".to_string().into());
        }
        let url = address(&base, &files[0].url)?;
        if url != address(&base, &format!("{}/{name}", metadata.version))? {
            return Err("签名附件未固定到同一版本。".to_string().into());
        }
        materials.push(material(&client, url).await?);
    }
    let mut release = verify_metadata(
        key,
        &materials[0],
        &materials[1],
        &materials[2],
        &metadata.version,
    )?;
    if metadata.installer.name != release.manifest.installer()
        || metadata.installer.sha256 != release.hash
        || metadata.installer.size == 0
        || metadata.installer.size > MAX_INSTALLER
        || address(&base, &metadata.installer.url)?
            != address(
                &base,
                &format!("{}/{}", metadata.version, release.manifest.installer()),
            )?
    {
        return Err("工具商店安装器与原始签名材料不一致。".to_string().into());
    }
    release.store = Some(store.to_string());
    Ok(release)
}

pub(super) async fn download(
    store: &str,
    release: &Release,
    path: &Path,
    cancel: &CancellationToken,
    progress: &mut impl FnMut(u64, Option<u64>),
) -> std::result::Result<(), Failure> {
    let base = root(store)?
        .join("api/updates/v1/quickphrase/")
        .map_err(|_| "工具商店路径无效。".to_string())?;
    let client = connection(&base)?;
    let request = response(
        &client,
        address(
            &base,
            &format!(
                "{}/{}",
                release.manifest.version,
                release.manifest.installer()
            ),
        )?,
    );
    let response = tokio::select! { biased; _ = cancel.cancelled() => return Err("下载已取消。".to_string().into()), result = request => result? };
    let total = response.content_length();
    if total.is_some_and(|size| size > MAX_INSTALLER) {
        return Err("安装包过大。".to_string().into());
    }
    let mut output = tokio::fs::File::create(path)
        .await
        .map_err(|_| "无法创建安装包临时文件。".to_string())?;
    let mut chunks = response.bytes_stream();
    let mut count = 0u64;
    let mut digest = Sha256::new();
    loop {
        let next = tokio::select! { biased; _ = cancel.cancelled() => { let _ = output.flush().await; return Err("下载已取消。".to_string().into()); }, next = chunks.next() => next };
        let Some(chunk) = next else {
            break;
        };
        let chunk = chunk.map_err(network)?;
        count += chunk.len() as u64;
        if count > MAX_INSTALLER {
            return Err("安装包过大。".to_string().into());
        }
        output
            .write_all(&chunk)
            .await
            .map_err(|_| "安装包写入失败，请检查磁盘空间。".to_string())?;
        digest.update(&chunk);
        progress(count, total);
    }
    output
        .flush()
        .await
        .map_err(|_| "安装包写入失败。".to_string())?;
    output
        .sync_all()
        .await
        .map_err(|_| "安装包保存失败，请检查磁盘空间。".to_string())?;
    if cancel.is_cancelled() {
        return Err("下载已取消。".to_string().into());
    }
    if total.is_some_and(|total| total != count)
        || format!("{:x}", digest.finalize()) != release.hash
    {
        return Err("安装包长度或签名摘要不符，已停止安装。".to_string().into());
    }
    Ok(())
}
