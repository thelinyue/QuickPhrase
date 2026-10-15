//! Rust 独占共享话术网络、授权、离线缓存和素材下载。UI 从不持有会话令牌。
//! 每组仅一个刷新任务；epoch 和 slot generation 使退出/移库前的旧响应永远失效。
use crate::{model as local, team_credentials, AppState};
use futures_util::StreamExt;
use local::Result;
use reqwest::{Client, Method, Response, Url};
use serde::{de::DeserializeOwned, Deserialize, Serialize};
use shanyu_team_model as wire;
use std::{
    collections::HashMap,
    fs,
    path::{Path, PathBuf},
    sync::{atomic::Ordering, Arc, Mutex},
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use tauri::{Emitter, Manager};
use tokio::{
    io::AsyncWriteExt,
    sync::{Mutex as AsyncMutex, Semaphore},
};
use tokio_util::sync::CancellationToken;

const LIMIT: u64 = 128 * 1024 * 1024;
#[path = "company_client.rs"]
mod company;
#[path = "member_client.rs"]
pub mod member;
pub use company::{CompanySelection, CompanyTransfer, CompanyUpload, CompanyView};
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Connection {
    server: String,
    username: String,
    partition: String,
}
#[derive(Clone)]
struct Active {
    connection: Connection,
    token: String,
    epoch: u64,
    authorization: wire::Authorization,
    offline: bool,
    cancel: CancellationToken,
}
#[derive(Clone)]
struct Context {
    active: Active,
    team: String,
    generation: String,
    lifecycle_version: u64,
    company_asset_ids: Vec<String>,
}
/// 每个共享话术独立保存最后一次成功核对时间；随账号缓存一同清理，不能跨授权复用。
#[derive(Serialize, Deserialize)]
struct SyncTime {
    version: u64,
    lifecycle_version: u64,
    timestamp: u64,
}
struct Slot {
    last_successful_sync_at: Option<u64>,
    snapshot: Option<wire::Snapshot>,
    wanted: u64,
    refreshing: bool,
    generation: String,
    cancel: CancellationToken,
}
#[derive(Default)]
struct Inner {
    epoch: u64,
    active: Option<Active>,
    slots: HashMap<String, Slot>,
    delivery_team: Option<(String, u64)>,
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub session_epoch: u64,
    pub server: String,
    pub username: String,
    pub user: Option<wire::User>,
    pub teams: Vec<wire::Team>,
    pub team_sync_times: HashMap<String, u64>,
    pub offline: bool,
    pub private_sync: bool,
    pub member_phrase_sync: bool,
    pub company_files: bool,
    pub company_file_permissions: bool,
}
/// 私人任务持有会话世代而非裸令牌；所有落盘都在当前世代锁内再次核验。
#[derive(Clone)]
pub struct AccountContext {
    pub server: String,
    pub user_id: String,
    pub username: String,
    epoch: u64,
    pub cancel: CancellationToken,
}
/// 确定的客户端错误允许重建待同步请求；网络中断和服务端错误必须保留原编号查结果。
pub enum PrivateCommit {
    Applied(wire::private::Commit),
    Rejected(String),
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientPhrase {
    #[serde(flatten)]
    pub phrase: local::Phrase,
    pub content_ref: wire::ContentRef,
    pub ready: bool,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct View {
    pub team: wire::Team,
    pub categories: Vec<local::Category>,
    pub phrases: Vec<ClientPhrase>,
    pub offline: bool,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Draft {
    pub team_id: String,
    pub expected_version: u64,
    pub expected_lifecycle_version: u64,
    pub phrase: local::Phrase,
    #[serde(default)]
    pub company_files: Vec<CompanySelection>,
}

pub struct TeamClient {
    dir: PathBuf,
    client: Client,
    inner: Mutex<Inner>,
    auth_refresh: AsyncMutex<()>,
    login_lock: AsyncMutex<()>,
    downloads: Semaphore,
    /// 云文件准备与上传共用两个名额，避免多选时预先复制整批素材；下载许可保持独立。
    uploads: Semaphore,
    download_locks: Mutex<HashMap<String, Arc<AsyncMutex<()>>>>,
    company_transfers: Mutex<HashMap<String, CancellationToken>>,
    company_history: Arc<Mutex<HashMap<String, CompanyTransfer>>>,
    member: Mutex<member::MemberState>,
    member_refresh: AsyncMutex<()>,
    member_wake: tokio::sync::Notify,
}
pub fn normalize_server(value: &str) -> Result<String> {
    let url = Url::parse(value.trim())
        .map_err(|_| "请输入完整的 http:// 或 https:// 服务器地址。".to_owned())?;
    if !["http", "https"].contains(&url.scheme())
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err("服务器地址需使用 HTTP 或 HTTPS，不能包含密码、查询或片段。".into());
    }
    Ok(url.as_str().trim_end_matches('/').to_owned())
}
fn io_error(_: impl std::fmt::Display) -> String {
    "共享话术缓存读写失败，请检查数据目录权限和可用空间。".into()
}
fn save_json<T: Serialize>(path: &Path, value: &T) -> Result<()> {
    fs::create_dir_all(path.parent().ok_or("缓存目录无效。")?).map_err(io_error)?;
    let mut temp = tempfile::NamedTempFile::new_in(path.parent().unwrap()).map_err(io_error)?;
    serde_json::to_writer(&mut temp, value).map_err(io_error)?;
    temp.persist(path).map_err(io_error)?;
    Ok(())
}
fn load_json<T: DeserializeOwned>(path: &Path) -> Option<T> {
    fs::File::open(path)
        .ok()
        .and_then(|f| serde_json::from_reader(f).ok())
}
/// 用正常的长度记录辨别未完成或后来截断的缓存，不为素材引入哈希或内容指纹。
fn ready_path(path: &Path) -> bool {
    let Some(size) = load_json::<u64>(&path.with_extension("ready.json")) else {
        return false;
    };
    size > 0 && fs::metadata(path).is_ok_and(|m| m.len() == size)
}
impl TeamClient {
    pub async fn account_commit(
        &self,
        ctx: &AccountContext,
        mutation: &wire::private::Mutation,
    ) -> Result<PrivateCommit> {
        let active = self.active()?;
        self.with_account(ctx, || Ok(()))?;
        let request = self
            .client
            .post(format!("{}/api/me/private/mutations", ctx.server))
            .bearer_auth(&active.token)
            .json(mutation)
            .timeout(Duration::from_secs(30));
        let response = tokio::select! {
            _ = ctx.cancel.cancelled() => return Err("旧同步已取消。".into()),
            r = request.send() => r.map_err(|_| "同步请求超时，稍后查询结果。".to_owned())?,
        };
        let status = response.status();
        if status.is_client_error() && status != reqwest::StatusCode::REQUEST_TIMEOUT {
            let message = self
                .check(response)
                .await
                .err()
                .unwrap_or_else(|| "同步请求被拒绝。".into());
            return self.with_account(ctx, || Ok(PrivateCommit::Rejected(message)));
        }
        let result = self
            .check(response)
            .await?
            .json()
            .await
            .map_err(|_| "同步结果无法读取，稍后查询结果。".to_owned())?;
        self.with_account(ctx, || Ok(PrivateCommit::Applied(result)))
    }
    pub async fn account_backup(&self, ctx: &AccountContext, path: &Path) -> Result<()> {
        let active = self.active()?;
        self.with_account(ctx, || Ok(()))?;
        let response = self
            .response(&active, Method::GET, "me/private/backup", None)
            .await?;
        let temp = tempfile::NamedTempFile::new_in(path.parent().ok_or("备份路径无效。")?)
            .map_err(|_| "备份文件无法创建。".to_owned())?;
        let mut file =
            tokio::fs::File::from_std(temp.reopen().map_err(|_| "备份文件无法写入。".to_owned())?);
        let mut stream = response.bytes_stream();
        loop {
            let chunk = tokio::select! {_=ctx.cancel.cancelled()=>return Err("备份已取消。".into()),r=stream.next()=>r};
            let Some(chunk) = chunk else { break };
            let chunk = chunk.map_err(|_| "备份下载中断。".to_owned())?;
            file.write_all(&chunk)
                .await
                .map_err(|_| "备份无法写入，请检查空间。".to_owned())?;
        }
        file.sync_all()
            .await
            .map_err(|_| "备份无法保存。".to_owned())?;
        drop(file);
        self.with_account(ctx, || {
            temp.persist(path)
                .map_err(|_| "备份无法保存。".to_owned())?;
            Ok(())
        })
    }
    pub fn account_context(&self) -> Result<AccountContext> {
        let active = self.active()?;
        if active.authorization.user.must_change_password {
            return Err("请先修改初始密码。".into());
        }
        if !active.authorization.capabilities.private_sync {
            return Err("服务端需升级后才能同步个人话术。".into());
        }
        Ok(AccountContext {
            server: active.connection.server,
            user_id: active.authorization.user.id,
            username: active.authorization.user.username,
            epoch: active.epoch,
            cancel: active.cancel,
        })
    }
    pub fn with_account<T>(
        &self,
        ctx: &AccountContext,
        work: impl FnOnce() -> Result<T>,
    ) -> Result<T> {
        let inner = self.inner.lock().unwrap();
        if !inner.active.as_ref().is_some_and(|a| {
            a.epoch == ctx.epoch
                && a.authorization.user.id == ctx.user_id
                && a.connection.server == ctx.server
                && !a.cancel.is_cancelled()
        }) {
            return Err("账号已变化，旧同步结果已丢弃。".into());
        }
        work()
    }
    pub async fn account_request<T: DeserializeOwned>(
        &self,
        ctx: &AccountContext,
        method: Method,
        path: &str,
        body: Option<serde_json::Value>,
    ) -> Result<T> {
        let active = self.active()?;
        self.with_account(ctx, || Ok(()))?;
        let result = self
            .request(&active, method, &format!("me/private/{path}"), body)
            .await?;
        self.with_account(ctx, || Ok(result))
    }
    pub async fn account_upload(
        &self,
        ctx: &AccountContext,
        id: &str,
        kind: &str,
        name: &str,
        path: &Path,
    ) -> Result<wire::private::Asset> {
        let active = self.active()?;
        self.with_account(ctx, || Ok(()))?;
        let file = tokio::fs::File::open(path)
            .await
            .map_err(|_| "素材缺失，请重新选择。".to_owned())?;
        let size = file
            .metadata()
            .await
            .map_err(|_| "素材无法读取。".to_owned())?
            .len();
        if size == 0 || size > LIMIT {
            return Err("素材不能为空或超过128MB。".into());
        }
        let request = self
            .client
            .put(format!("{}/api/me/private/assets/{id}", ctx.server))
            .bearer_auth(&active.token)
            .query(&[("kind", kind), ("name", name)])
            .header("content-length", size)
            .body(reqwest::Body::wrap_stream(
                tokio_util::io::ReaderStream::new(file),
            ))
            .timeout(Duration::from_secs(120));
        let response = tokio::select! {_=ctx.cancel.cancelled()=>return Err("旧上传已取消。".into()),r=request.send()=>r.map_err(|_|"素材上传失败或超时，稍后继续。".to_owned())?};
        let asset = self
            .check(response)
            .await?
            .json()
            .await
            .map_err(|_| "素材响应无法读取。".to_owned())?;
        self.with_account(ctx, || Ok(asset))
    }
    pub async fn account_download(
        &self,
        ctx: &AccountContext,
        id: &str,
        target: &Path,
        expected: u64,
        image: bool,
    ) -> Result<tempfile::NamedTempFile> {
        self.account_download_route(
            ctx,
            &format!("me/private/assets/{id}"),
            target,
            expected,
            image,
        )
        .await
    }
    /// 本人素材和成员素材共用流式下载、大小核对、图片解码及两个下载名额。
    async fn account_download_route(
        &self,
        ctx: &AccountContext,
        route: &str,
        target: &Path,
        expected: u64,
        image: bool,
    ) -> Result<tempfile::NamedTempFile> {
        let _permit = tokio::select! {_=ctx.cancel.cancelled()=>return Err("旧下载已取消。".into()),p=self.downloads.acquire()=>p.map_err(|_|"下载已停止。".to_owned())?};
        let active = self.active()?;
        self.with_account(ctx, || Ok(()))?;
        let response = self.response(&active, Method::GET, route, None).await?;
        let temp = tempfile::NamedTempFile::new_in(target.parent().ok_or("缓存目录无效。")?)
            .map_err(|_| "无法创建素材缓存。".to_owned())?;
        let mut file =
            tokio::fs::File::from_std(temp.reopen().map_err(|_| "缓存无法写入。".to_owned())?);
        let mut stream = response.bytes_stream();
        let mut size = 0;
        loop {
            let chunk = tokio::select! {_=ctx.cancel.cancelled()=>return Err("旧下载已取消。".into()),r=stream.next()=>r};
            let Some(chunk) = chunk else { break };
            let chunk = chunk.map_err(|_| "素材下载中断。".to_owned())?;
            size += chunk.len() as u64;
            if size > LIMIT || size > expected {
                return Err("素材大小无效。".into());
            }
            file.write_all(&chunk)
                .await
                .map_err(|_| "素材缓存写入失败。".to_owned())?;
        }
        file.flush()
            .await
            .map_err(|_| "素材缓存写入失败。".to_owned())?;
        drop(file);
        if size != expected {
            return Err("素材下载不完整。".into());
        }
        if image {
            let input = temp.path().to_owned();
            // 解码仍持共享下载许可，私人和共享话术素材合计只占两个下载／解码位置。
            tauri::async_runtime::spawn_blocking(move || {
                crate::backup::decode_image(&fs::read(input).map_err(io_error)?).map(|_| ())
            })
            .await
            .map_err(|_| "图片处理失败。".to_owned())??;
        }
        self.with_account(ctx, || Ok(temp))
    }
    pub fn new(dir: &Path) -> Result<Arc<Self>> {
        let dir = dir.join("teams");
        fs::create_dir_all(&dir).map_err(io_error)?;
        let client = Client::builder()
            .connect_timeout(Duration::from_secs(5))
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .map_err(|_| "无法创建共享话术网络连接。".to_owned())?;
        let this = Arc::new(Self {
            dir,
            client,
            inner: Mutex::new(Inner::default()),
            auth_refresh: AsyncMutex::new(()),
            login_lock: AsyncMutex::new(()),
            downloads: Semaphore::new(2),
            uploads: Semaphore::new(2),
            company_transfers: Mutex::new(HashMap::new()),
            company_history: Arc::new(Mutex::new(HashMap::new())),
            download_locks: Mutex::new(HashMap::new()),
            member: Mutex::new(member::MemberState::default()),
            member_refresh: AsyncMutex::new(()),
            member_wake: tokio::sync::Notify::new(),
        });
        if let Err(error) = this.clean_uncommitted_uploads() {
            log::warn!("未完成的上传副本暂时无法清理：{error}");
        }
        if let Some(connection) = load_json::<Connection>(&this.dir.join("connection.json")) {
            if uuid::Uuid::parse_str(&connection.partition).is_ok()
                && normalize_server(&connection.server).as_deref() == Ok(connection.server.as_str())
            {
                if let Some(token) = team_credentials::read(&connection.partition)? {
                    if let Some(authorization) = load_json::<wire::Authorization>(
                        &this
                            .dir
                            .join(&connection.partition)
                            .join("authorization.json"),
                    ) {
                        let mut inner = this.inner.lock().unwrap();
                        inner.epoch += 1;
                        let active = Active {
                            connection,
                            token,
                            epoch: inner.epoch,
                            authorization,
                            offline: true,
                            cancel: CancellationToken::new(),
                        };
                        for team in &active.authorization.teams {
                            let snapshot = load_json::<wire::Snapshot>(
                                &this.team_dir(&active, &team.team_id).join("snapshot.json"),
                            )
                            .filter(|s| {
                                s.team_id == team.team_id
                                    && s.lifecycle_version == team.lifecycle_version
                            });
                            inner.slots.insert(
                                team.team_id.clone(),
                                Slot {
                                    last_successful_sync_at: load_json::<SyncTime>(
                                        &this
                                            .team_dir(&active, &team.team_id)
                                            .join("sync-time.json"),
                                    )
                                    .filter(|t| {
                                        snapshot.as_ref().is_some_and(|s| {
                                            t.lifecycle_version == s.lifecycle_version
                                                && t.version <= s.version
                                        })
                                    })
                                    .map(|t| t.timestamp),
                                    snapshot,
                                    wanted: team.version,
                                    refreshing: false,
                                    generation: local::id(),
                                    cancel: active.cancel.child_token(),
                                },
                            );
                        }
                        inner.active = Some(active);
                    }
                }
            }
        }
        this.initialize_member();
        Ok(this)
    }
    fn team_dir(&self, active: &Active, team: &str) -> PathBuf {
        self.dir.join(&active.connection.partition).join(team)
    }
    fn active(&self) -> Result<Active> {
        self.inner
            .lock()
            .unwrap()
            .active
            .clone()
            .ok_or("请先在设置中登录共享话术服务。".into())
    }
    fn context(&self, team: &str) -> Result<Context> {
        let inner = self.inner.lock().unwrap();
        let active = inner.active.clone().ok_or("请先登录共享话术服务。")?;
        if !active.authorization.teams.iter().any(|t| t.team_id == team) {
            return Err("已没有该共享话术的访问权限。".into());
        }
        let slot = inner.slots.get(team).ok_or("共享话术正在加载。")?;
        Ok(Context {
            company_asset_ids: slot
                .snapshot
                .as_ref()
                .map(|s| s.company_asset_ids.clone())
                .unwrap_or_default(),
            lifecycle_version: active
                .authorization
                .teams
                .iter()
                .find(|t| t.team_id == team)
                .unwrap()
                .lifecycle_version,
            active,
            team: team.into(),
            generation: slot.generation.clone(),
        })
    }
    fn valid(inner: &Inner, ctx: &Context) -> bool {
        inner.active.as_ref().is_some_and(|a| {
            a.epoch == ctx.active.epoch
                && a.authorization.user.id == ctx.active.authorization.user.id
                && a.authorization
                    .teams
                    .iter()
                    .any(|t| t.team_id == ctx.team && t.lifecycle_version == ctx.lifecycle_version)
        }) && inner
            .slots
            .get(&ctx.team)
            .is_some_and(|s| s.generation == ctx.generation && !s.cancel.is_cancelled())
    }
    pub fn status(&self) -> Status {
        let inner = self.inner.lock().unwrap();
        if let Some(active) = &inner.active {
            Status {
                session_epoch: inner.epoch,
                server: active.connection.server.clone(),
                username: active.connection.username.clone(),
                user: Some(active.authorization.user.clone()),
                teams: active.authorization.teams.clone(),
                team_sync_times: inner
                    .slots
                    .iter()
                    .filter_map(|(id, slot)| {
                        slot.last_successful_sync_at.map(|time| (id.clone(), time))
                    })
                    .collect(),
                offline: active.offline,
                private_sync: active.authorization.capabilities.private_sync,
                member_phrase_sync: active.authorization.capabilities.member_phrase_sync,
                company_files: active.authorization.capabilities.company_files,
                company_file_permissions: active
                    .authorization
                    .capabilities
                    .company_file_permissions,
            }
        } else {
            let c = load_json::<Connection>(&self.dir.join("connection.json"));
            Status {
                session_epoch: inner.epoch,
                server: c.as_ref().map(|v| v.server.clone()).unwrap_or_default(),
                username: c.map(|v| v.username).unwrap_or_default(),
                user: None,
                teams: vec![],
                team_sync_times: HashMap::new(),
                offline: false,
                private_sync: false,
                member_phrase_sync: false,
                company_files: false,
                company_file_permissions: false,
            }
        }
    }
    fn emit(&self, app: &tauri::AppHandle) {
        let _ = app.emit("teams-changed", self.status());
        if let Some(state) = app.try_state::<AppState>() {
            state.private_sync.wake();
        }
    }
    async fn response(
        &self,
        active: &Active,
        method: Method,
        path: &str,
        body: Option<serde_json::Value>,
    ) -> Result<Response> {
        let mut request = self
            .client
            .request(method, format!("{}/api/{path}", active.connection.server))
            .bearer_auth(&active.token)
            .timeout(Duration::from_secs(30));
        if let Some(team) = path
            .strip_prefix("teams/")
            .and_then(|v| v.split('/').next())
        {
            if let Some(info) = active
                .authorization
                .teams
                .iter()
                .find(|t| t.team_id == team)
            {
                request = request.header("x-team-lifecycle", info.lifecycle_version);
            }
        }
        if let Some(body) = body {
            request = request.json(&body);
        }
        let response = tokio::select! { _=active.cancel.cancelled()=>return Err("账号已退出，旧请求已取消。".into()),response=request.send()=>response.map_err(|_|"服务器连接失败或请求超时。写入不会自动重试，请刷新确认结果。".to_owned())? };
        self.check(response).await
    }
    async fn check(&self, response: Response) -> Result<Response> {
        if response.status().is_success() {
            return Ok(response);
        }
        let status = response.status();
        let value = response.json::<serde_json::Value>().await.ok();
        let message = value
            .and_then(|v| v.get("message").and_then(|v| v.as_str()).map(str::to_owned))
            .unwrap_or_else(|| "服务器拒绝请求，请检查版本和权限。".into());
        // 授权和同步流程用状态码前缀识别撤权；统一 ASCII 冒号，中文正文继续用于错误提示。
        Err(format!("{}: {message}", status.as_u16()))
    }
    async fn request<T: DeserializeOwned>(
        &self,
        active: &Active,
        method: Method,
        path: &str,
        body: Option<serde_json::Value>,
    ) -> Result<T> {
        self.response(active, method, path, body)
            .await?
            .json()
            .await
            .map_err(|_| "服务器返回的内容无法读取。".into())
    }
    pub async fn login(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        server: String,
        username: String,
        password: String,
    ) -> Result<Status> {
        let _lock = self.login_lock.lock().await;
        let server = normalize_server(&server)?;
        let response = self
            .client
            .post(format!("{server}/api/login"))
            .timeout(Duration::from_secs(30))
            .json(&wire::Login {
                username: username.clone(),
                password,
            })
            .send()
            .await
            .map_err(|_| {
                "登录连接失败，请检查地址、证书和网络。HTTPS 错误不会降级为 HTTP。".to_owned()
            })?;
        let session = self
            .check(response)
            .await?
            .json::<wire::Session>()
            .await
            .map_err(|_| "登录响应无法读取。".to_owned())?;
        self.clear_session(app)?;
        let connection = Connection {
            server,
            username,
            partition: local::id(),
        };
        team_credentials::write(&connection.partition, &session.token)?;
        save_json(&self.dir.join("connection.json"), &connection)?;
        {
            let mut inner = self.inner.lock().unwrap();
            inner.epoch += 1;
            inner.active = Some(Active {
                connection,
                token: session.token,
                epoch: inner.epoch,
                authorization: wire::Authorization {
                    user: session.user,
                    teams: vec![],
                    capabilities: wire::Capabilities::default(),
                },
                offline: false,
                cancel: CancellationToken::new(),
            });
        }
        self.refresh_authorization(app).await?;
        self.start(app.clone());
        self.emit(app);
        Ok(self.status())
    }
    fn clear_session(&self, app: &tauri::AppHandle) -> Result<()> {
        let active = {
            let mut inner = self.inner.lock().unwrap();
            inner.epoch += 1;
            let active = inner.active.take();
            for slot in inner.slots.values() {
                slot.cancel.cancel();
            }
            inner.slots.clear();
            self.company_history.lock().unwrap().clear();
            active
        };
        let mut result = Ok(());
        self.clear_member(app);
        if let Some(active) = active {
            active.cancel.cancel();
            result = team_credentials::delete(&active.connection.partition);
            let dir = self.dir.join(active.connection.partition);
            if dir.exists() {
                let cleanup = fs::remove_dir_all(dir).map_err(io_error);
                if result.is_ok() {
                    result = cleanup;
                }
            }
        }
        let state = app.state::<AppState>();
        let transfer_dir = state.device_store.lock().unwrap().dir.join("transfer");
        state.transfer.invalidate_groups(&transfer_dir);
        self.invalidate_delivery(app);
        self.emit(app);
        result
    }
    pub async fn logout(&self, app: &tauri::AppHandle) -> Result<()> {
        let active = self.active().ok();
        // 先使本地 epoch 失效，网络响应或下载完成都不能再写入缓存。
        self.clear_session(app)?;
        if let Some(mut active) = active {
            active.cancel = CancellationToken::new();
            let _ = self
                .request::<serde_json::Value>(&active, Method::POST, "logout", None)
                .await;
        }
        Ok(())
    }
    fn invalidate_delivery(&self, app: &tauri::AppHandle) {
        if let Some(state) = app.try_state::<AppState>() {
            let cancel = {
                let inner = self.inner.lock().unwrap();
                inner
                    .delivery_team
                    .as_ref()
                    .is_some_and(|(team, lifecycle)| {
                        inner.active.as_ref().is_none_or(|a| {
                            !a.authorization
                                .teams
                                .iter()
                                .any(|t| &t.team_id == team && t.lifecycle_version == *lifecycle)
                        })
                    })
            };
            if cancel {
                state.delivery.cancelled.store(true, Ordering::Release);
            }
            let mut mapping = state.native.sequence_map.write().unwrap();
            if mapping
                .0
                .iter()
                .flatten()
                .any(|id| wire::ContentRef::from_key(id).is_some())
            {
                *mapping = (vec![], true);
            }
            state.native.hide_preview(None);
        }
    }
    pub async fn refresh_authorization(self: &Arc<Self>, app: &tauri::AppHandle) -> Result<()> {
        let _refresh = self.auth_refresh.lock().await;
        let active = self.active()?;
        let authorization = self
            .request::<wire::Authorization>(&active, Method::GET, "me", None)
            .await;
        let authorization = match authorization {
            Ok(a) => a,
            Err(e) => {
                if e.starts_with("401:") {
                    if self
                        .inner
                        .lock()
                        .unwrap()
                        .active
                        .as_ref()
                        .is_some_and(|a| a.epoch == active.epoch)
                    {
                        self.clear_session(app)?;
                    }
                } else {
                    let mut inner = self.inner.lock().unwrap();
                    if let Some(a) = inner.active.as_mut().filter(|a| a.epoch == active.epoch) {
                        a.offline = true;
                        let prefix = format!("{}:upload:", a.epoch);
                        for (key, cancel) in self.company_transfers.lock().unwrap().iter() {
                            if key.starts_with(&prefix) {
                                cancel.cancel();
                            }
                        }
                    }
                }
                self.emit(app);
                return Err(e);
            }
        };
        if uuid::Uuid::parse_str(&authorization.user.id).is_err()
            || authorization
                .teams
                .iter()
                .any(|t| uuid::Uuid::parse_str(&t.team_id).is_err())
        {
            return Err("服务器返回了无效的账号或共享话术ID。".into());
        }
        let (removed, cache_result) = {
            let mut inner = self.inner.lock().unwrap();
            if !inner
                .active
                .as_ref()
                .is_some_and(|a| a.epoch == active.epoch)
            {
                return Err("旧账号请求已失效。".into());
            }
            let removed: Vec<String> = inner
                .slots
                .keys()
                .filter(|id| {
                    !authorization.teams.iter().any(|t| {
                        &t.team_id == *id
                            && active.authorization.teams.iter().any(|old| {
                                old.team_id == t.team_id
                                    && old.lifecycle_version == t.lifecycle_version
                            })
                    })
                })
                .cloned()
                .collect();
            for id in &removed {
                if let Some(slot) = inner.slots.remove(id) {
                    slot.cancel.cancel();
                }
            }
            for team in &authorization.teams {
                let slot = inner
                    .slots
                    .entry(team.team_id.clone())
                    .or_insert_with(|| Slot {
                        last_successful_sync_at: None,
                        snapshot: None,
                        wanted: team.version,
                        refreshing: false,
                        generation: local::id(),
                        cancel: active.cancel.child_token(),
                    });
                if active
                    .authorization
                    .teams
                    .iter()
                    .any(|old| old.team_id == team.team_id && old.editable && !team.editable)
                {
                    // 降权后保留可读内容，但使旧编辑和下载上下文失效。
                    slot.cancel.cancel();
                    slot.cancel = active.cancel.child_token();
                    slot.generation = local::id();
                    slot.refreshing = false;
                }
                slot.wanted = slot.wanted.max(team.version);
            }
            // 只取消旧上传授权的任务；目录读取、下载及个人副本不因管理授权调整失效。
            if active
                .authorization
                .user
                .company_file_permissions
                .upload_version
                != authorization.user.company_file_permissions.upload_version
                || (active.authorization.user.admin && !authorization.user.admin)
            {
                let prefix = format!("{}:upload:", active.epoch);
                for (key, cancel) in self.company_transfers.lock().unwrap().iter() {
                    if key.starts_with(&prefix) {
                        cancel.cancel();
                    }
                }
            }
            let current = inner.active.as_mut().unwrap();
            current.authorization = authorization;
            current.offline = false;
            let current = current.clone();
            let cache_result = self.persist_authorization(&current).and_then(|_| {
                for id in &removed {
                    let dir = self.team_dir(&active, id);
                    if dir.exists() {
                        fs::remove_dir_all(dir).map_err(io_error)?;
                    }
                }
                for (id, slot) in &mut inner.slots {
                    // 授权响应确认版本未变且本机内容已齐备时，无需重新下载快照。
                    self.record_team_success(&current, id, slot)?;
                }
                Ok(())
            });
            (removed, cache_result)
        };
        if !removed.is_empty() {
            let state = app.state::<AppState>();
            let transfer_dir = state.device_store.lock().unwrap().dir.join("transfer");
            state
                .transfer
                .invalidate_removed_groups(&transfer_dir, &removed);
            self.invalidate_delivery(app);
        }
        self.emit(app);
        let current = self.status();
        if !current.member_phrase_sync
            || current
                .user
                .as_ref()
                .is_some_and(|u| u.must_change_password)
        {
            self.clear_member(app);
        } else {
            self.start_member(app.clone());
            if active.offline {
                self.member_wake.notify_one();
            }
        }
        let teams = self.status().teams;
        for t in teams {
            app.asset_protocol_scope()
                .allow_directory(self.team_dir(&active, &t.team_id).join("images"), true)
                .map_err(io_error)?;
            self.schedule(app.clone(), &t.team_id, t.version);
        }
        cache_result
    }
    /// 已知撤权必须先更新内存；持久化失败时删除凭据，禁止下次离线启动沿用旧授权文件。
    fn persist_authorization(&self, active: &Active) -> Result<()> {
        let path = self
            .dir
            .join(&active.connection.partition)
            .join("authorization.json");
        if let Err(e) = save_json(&path, &active.authorization) {
            let _ = fs::remove_file(path);
            team_credentials::delete(&active.connection.partition)?;
            return Err(e);
        }
        Ok(())
    }
    /// 设置 wanted 合并通知。refreshing 在同一锁内检查，保证每组最多一个网络快照任务。
    fn schedule(self: &Arc<Self>, app: tauri::AppHandle, team: &str, version: u64) {
        let ctx = match self.context(team) {
            Ok(c) => c,
            Err(_) => return,
        };
        if !self.claim_refresh(&ctx, version) {
            return;
        }
        let this = self.clone();
        tauri::async_runtime::spawn(async move {
            loop {
                let result = this
                    .request::<wire::Snapshot>(
                        &ctx.active,
                        Method::GET,
                        &format!("teams/{}/snapshot", ctx.team),
                        None,
                    )
                    .await;
                match result {
                    Ok(snapshot) if snapshot.team_id == ctx.team => {
                        if this.accept_snapshot(&ctx, snapshot).is_err() {
                            let mut inner = this.inner.lock().unwrap();
                            if Self::valid(&inner, &ctx) {
                                inner.slots.get_mut(&ctx.team).unwrap().refreshing = false;
                            }
                            return;
                        }
                        if !this.finish_refresh(&ctx) {
                            this.emit(&app);
                            return;
                        }
                    }
                    _ => {
                        let mut inner = this.inner.lock().unwrap();
                        if Self::valid(&inner, &ctx) {
                            inner.slots.get_mut(&ctx.team).unwrap().refreshing = false;
                        }
                        drop(inner);
                        this.emit(&app);
                        return;
                    }
                }
            }
        });
    }
    fn claim_refresh(&self, ctx: &Context, version: u64) -> bool {
        let mut inner = self.inner.lock().unwrap();
        if !Self::valid(&inner, ctx) {
            return false;
        }
        let slot = inner.slots.get_mut(&ctx.team).unwrap();
        slot.wanted = slot.wanted.max(version);
        if slot.refreshing
            || slot
                .snapshot
                .as_ref()
                .is_some_and(|s| s.version >= slot.wanted)
        {
            return false;
        }
        slot.refreshing = true;
        true
    }
    /// 返回 true 表示任务继续补齐期间合并到的新版本；判断和释放 refreshing 在同一锁内完成。
    fn finish_refresh(&self, ctx: &Context) -> bool {
        let mut inner = self.inner.lock().unwrap();
        if !Self::valid(&inner, ctx) {
            return false;
        }
        let slot = inner.slots.get_mut(&ctx.team).unwrap();
        let more = slot
            .snapshot
            .as_ref()
            .is_none_or(|s| s.version < slot.wanted);
        if !more {
            slot.refreshing = false;
        }
        more
    }
    fn record_team_success(&self, active: &Active, team: &str, slot: &mut Slot) -> Result<()> {
        let Some(snapshot) = &slot.snapshot else {
            return Ok(());
        };
        if active.offline
            || snapshot.version < slot.wanted
            || !active
                .authorization
                .teams
                .iter()
                .any(|t| t.team_id == team && t.lifecycle_version == snapshot.lifecycle_version)
        {
            return Ok(());
        }
        let timestamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(io_error)?
            .as_millis() as u64;
        save_json(
            &self.team_dir(active, team).join("sync-time.json"),
            &SyncTime {
                version: snapshot.version,
                lifecycle_version: snapshot.lifecycle_version,
                timestamp,
            },
        )?;
        slot.last_successful_sync_at = Some(timestamp);
        Ok(())
    }
    fn accept_snapshot(&self, ctx: &Context, snapshot: wire::Snapshot) -> Result<()> {
        let mut inner = self.inner.lock().unwrap();
        if !Self::valid(&inner, ctx) {
            return Err("旧账号或共享话术响应已失效。".into());
        }
        if snapshot.team_id != ctx.team || snapshot.lifecycle_version != ctx.lifecycle_version {
            return Err("共享话术快照归属无效。".into());
        }
        if snapshot
            .phrases
            .iter()
            .flat_map(|p| &p.segments)
            .flat_map(|s| &s.blocks)
            .filter_map(wire::Block::asset)
            .any(|(id, _)| uuid::Uuid::parse_str(id).is_err())
        {
            return Err("共享话术素材ID无效。".into());
        }
        let active = inner.active.as_ref().unwrap().clone();
        let slot = inner.slots.get_mut(&ctx.team).unwrap();
        if slot
            .snapshot
            .as_ref()
            .is_none_or(|old| snapshot.version >= old.version)
        {
            save_json(
                &self.team_dir(&ctx.active, &ctx.team).join("snapshot.json"),
                &snapshot,
            )?;
            slot.snapshot = Some(snapshot);
            self.record_team_success(&active, &ctx.team, slot)?;
        }
        Ok(())
    }
    pub fn start(self: &Arc<Self>, app: tauri::AppHandle) {
        let Ok(active) = self.active() else { return };
        self.start_member(app.clone());
        let this = self.clone();
        for team in &active.authorization.teams {
            let _ = app
                .asset_protocol_scope()
                .allow_directory(self.team_dir(&active, &team.team_id).join("images"), true);
        }
        tauri::async_runtime::spawn(async move {
            let mut last_check = tokio::time::Instant::now() - Duration::from_secs(60);
            loop {
                if active.cancel.is_cancelled() {
                    return;
                }
                if this.refresh_authorization(&app).await.is_err() {
                    tokio::select! {_=active.cancel.cancelled()=>return,_=tokio::time::sleep(Duration::from_secs(3))=>{}};
                    continue;
                }
                let request = this
                    .client
                    .get(format!("{}/api/events", active.connection.server))
                    .bearer_auth(&active.token);
                let response =
                    tokio::select! {_=active.cancel.cancelled()=>return,r=request.send()=>r};
                if let Ok(response) = response {
                    if response.status().is_success() {
                        let mut stream = response.bytes_stream();
                        let mut buffer = String::new();
                        loop {
                            let next = tokio::select! {_=active.cancel.cancelled()=>return,_=tokio::time::sleep(Duration::from_secs(60))=>None,r=stream.next()=>r};
                            match next {
                                Some(Ok(bytes)) => {
                                    buffer.push_str(
                                        &String::from_utf8_lossy(&bytes).replace("\r\n", "\n"),
                                    );
                                    while let Some(end) = buffer.find("\n\n") {
                                        let event: String = buffer.drain(..end + 2).collect();
                                        if event.lines().any(|l| l == "event: authorization") {
                                            let _ = this.refresh_authorization(&app).await;
                                        } else if event
                                            .lines()
                                            .any(|l| l == "event: member-phrases")
                                        {
                                            this.member_wake.notify_one();
                                        } else if event
                                            .lines()
                                            .any(|l| l == "event: private-version")
                                        {
                                            if let Some(state) = app.try_state::<AppState>() {
                                                state.private_sync.wake();
                                            }
                                        } else if event
                                            .lines()
                                            .any(|l| l == "event: company-files-version")
                                        {
                                            let _ = app.emit(
                                                "company-files-changed",
                                                this.status().session_epoch,
                                            );
                                        } else if let Some(notice) = event
                                            .lines()
                                            .find_map(|l| l.strip_prefix("data: "))
                                            .and_then(|v| {
                                                serde_json::from_str::<wire::Notice>(v).ok()
                                            })
                                        {
                                            if let (Some(team), Some(version)) =
                                                (notice.team_id, notice.version)
                                            {
                                                this.schedule(app.clone(), &team, version);
                                            }
                                        }
                                    }
                                    if buffer.len() > 65536 {
                                        break;
                                    }
                                }
                                _ => break,
                            }
                            if last_check.elapsed() >= Duration::from_secs(60) {
                                let _ = this.refresh_authorization(&app).await;
                                last_check = tokio::time::Instant::now();
                            }
                        }
                    }
                }
                tokio::select! {_=active.cancel.cancelled()=>return,_=tokio::time::sleep(Duration::from_secs(2))=>{}}
            }
        });
    }
    fn local_phrase(&self, ctx: &Context, p: &wire::Phrase) -> ClientPhrase {
        let content_ref = wire::ContentRef {
            source: "team".into(),
            team_id: ctx.team.clone(),
            id: p.id.clone(),
            revision: p.revision,
            lifecycle_version: ctx.lifecycle_version,
        };
        let mut ready = true;
        let segments = p
            .segments
            .iter()
            .map(|s| local::Segment {
                blocks: s
                    .blocks
                    .iter()
                    .map(|b| match b {
                        wire::Block::Text { text } => local::Block::Text { text: text.clone() },
                        wire::Block::Image { asset_id, name }
                        | wire::Block::File { asset_id, name } => {
                            let path = self.asset_path(
                                ctx,
                                asset_id,
                                name,
                                matches!(b, wire::Block::Image { .. }),
                            );
                            let available = ready_path(&path);
                            ready &= available;
                            let path = if available {
                                local::external_path(&path)
                            } else {
                                String::new()
                            };
                            if matches!(b, wire::Block::Image { .. }) {
                                local::Block::Image {
                                    path,
                                    name: name.clone(),
                                }
                            } else {
                                local::Block::File {
                                    path,
                                    name: name.clone(),
                                }
                            }
                        }
                    })
                    .collect(),
            })
            .collect();
        ClientPhrase {
            phrase: local::Phrase {
                id: content_ref.key(),
                root_id: p.root_id.clone(),
                group_id: p.group_id.clone(),
                title: p.title.clone(),
                color: p.color.clone(),
                background: p.background.clone(),
                shortcut: String::new(),
                position: p.position,
                segments,
            },
            content_ref,
            ready,
        }
    }
    pub fn view(&self, team: &str) -> Result<View> {
        let ctx = self.context(team)?;
        let inner = self.inner.lock().unwrap();
        if !Self::valid(&inner, &ctx) {
            return Err("共享话术授权已变化。".into());
        }
        let snapshot = inner
            .slots
            .get(team)
            .and_then(|s| s.snapshot.as_ref())
            .ok_or("正在加载共享话术，请稍后重试。")?;
        if snapshot.lifecycle_version != ctx.lifecycle_version {
            return Err("共享话术状态已变化，正在重新加载。".into());
        }
        let mut info = inner
            .active
            .as_ref()
            .unwrap()
            .authorization
            .teams
            .iter()
            .find(|t| t.team_id == team)
            .unwrap()
            .clone();
        info.version = snapshot.version;
        let mut categories: Vec<_> = snapshot
            .categories
            .iter()
            .map(|c| local::Category {
                id: c.id.clone(),
                name: c.name.clone(),
                kind: "phrase".into(),
                parent_id: c.parent_id.clone(),
                position: c.position,
                background: c.background.clone(),
            })
            .collect();
        categories.sort_by_key(|c| c.position);
        let mut phrases: Vec<_> = snapshot
            .phrases
            .iter()
            .map(|p| self.local_phrase(&ctx, p))
            .collect();
        phrases.sort_by_key(|p| p.phrase.position);
        Ok(View {
            team: info,
            categories,
            phrases,
            offline: inner.active.as_ref().unwrap().offline,
        })
    }
    pub fn phrase(&self, key: &str, require_ready: bool) -> Result<local::Phrase> {
        let reference = wire::ContentRef::from_key(key).ok_or("共享话术引用无效。")?;
        let view = self.view(&reference.team_id)?;
        let phrase = view
            .phrases
            .into_iter()
            .find(|p| p.content_ref.id == reference.id)
            .ok_or("话术已删除或授权已移除。")?;
        if phrase.content_ref.revision != reference.revision
            || phrase.content_ref.lifecycle_version != reference.lifecycle_version
        {
            return Err("屏幕上的话术版本已变化，请刷新后重新触发。".into());
        }
        if require_ready && !phrase.ready {
            return Err("整条话术的素材尚未就绪，请先下载，完成后重新点击投递。".into());
        }
        Ok(phrase.phrase)
    }
    fn asset_path(&self, ctx: &Context, asset: &str, name: &str, image: bool) -> PathBuf {
        if !image && ctx.company_asset_ids.iter().any(|id| id == asset) {
            return self.company_cache_path(&ctx.active, asset, name);
        }
        let safe: String = name
            .chars()
            .map(|c| {
                if c.is_control() || "<>:\"/\\|?*".contains(c) {
                    '_'
                } else {
                    c
                }
            })
            .take(120)
            .collect();
        self.team_dir(&ctx.active, &ctx.team)
            .join(if image { "images" } else { "files" })
            .join(format!(
                "{asset}-{}",
                if image {
                    "image.png"
                } else {
                    safe.trim_end_matches(['.', ' '])
                }
            ))
    }
    async fn download(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        ctx: &Context,
        asset: &str,
        name: &str,
        image: bool,
    ) -> Result<()> {
        if uuid::Uuid::parse_str(asset).is_err() {
            return Err("素材ID无效。".into());
        }
        let key = if ctx.company_asset_ids.iter().any(|id| id == asset) {
            format!("{}:company:{asset}", ctx.active.epoch)
        } else {
            format!("{}:{}:{asset}", ctx.active.epoch, ctx.generation)
        };
        let lock = self
            .download_locks
            .lock()
            .unwrap()
            .entry(key)
            .or_insert_with(|| Arc::new(AsyncMutex::new(())))
            .clone();
        let _lock = lock.lock().await;
        let path = self.asset_path(ctx, asset, name, image);
        if ready_path(&path) {
            return Ok(());
        }
        let cancellation = {
            let inner = self.inner.lock().unwrap();
            if !Self::valid(&inner, ctx) {
                return Err("旧下载已失效。".into());
            }
            inner.slots[&ctx.team].cancel.clone()
        };
        let _permit = tokio::select! {_=cancellation.cancelled()=>return Err("下载已取消。".into()),p=self.downloads.acquire()=>p.map_err(|_|"下载任务已关闭。")?};
        let response = self
            .response(
                &ctx.active,
                Method::GET,
                &format!("teams/{}/assets/{asset}", ctx.team),
                None,
            )
            .await?;
        let staging = self.dir.join("temp");
        fs::create_dir_all(&staging).map_err(io_error)?;
        let temp = tempfile::NamedTempFile::new_in(staging).map_err(io_error)?;
        let mut file = tokio::fs::File::from_std(temp.reopen().map_err(io_error)?);
        let mut size = 0;
        let mut stream = response.bytes_stream();
        loop {
            let chunk = tokio::select! {_=cancellation.cancelled()=>return Err("该共享话术下载已取消。".into()),c=stream.next()=>c};
            let Some(chunk) = chunk else { break };
            let chunk = chunk.map_err(|_| "素材下载中断。")?;
            size += chunk.len() as u64;
            if size > LIMIT {
                return Err("素材超过128MB。".into());
            }
            file.write_all(&chunk).await.map_err(io_error)?;
        }
        file.flush().await.map_err(io_error)?;
        drop(file);
        if size == 0 {
            return Err("素材内容为空。".into());
        }
        if image {
            let bytes = fs::read(temp.path()).map_err(io_error)?;
            crate::backup::decode_image(&bytes)?;
        }
        // 完成后重新检查服务端授权；断网可保留原授权，但收到401/移库时不能落盘。
        let _ = self.refresh_authorization(app).await;
        self.persist_download(ctx, temp, &path)?;
        if ctx.company_asset_ids.iter().any(|id| id == asset) {
            let _ = app.emit("company-files-changed", ctx.active.epoch);
        }
        self.emit(app);
        Ok(())
    }
    fn persist_download(
        &self,
        ctx: &Context,
        temp: tempfile::NamedTempFile,
        path: &Path,
    ) -> Result<()> {
        let inner = self.inner.lock().unwrap();
        if !Self::valid(&inner, ctx) {
            return Err("授权已变化，旧素材不会写入缓存。".into());
        }
        fs::create_dir_all(path.parent().ok_or("素材缓存目录无效。")?).map_err(io_error)?;
        temp.persist(path).map_err(io_error)?;
        save_json(
            &path.with_extension("ready.json"),
            &fs::metadata(path).map_err(io_error)?.len(),
        )?;
        Ok(())
    }
    pub async fn prepare(self: &Arc<Self>, app: &tauri::AppHandle, key: &str) -> Result<()> {
        let reference = wire::ContentRef::from_key(key).ok_or("话术引用无效。")?;
        let ctx = self.context(&reference.team_id)?;
        if reference.lifecycle_version != ctx.lifecycle_version {
            return Err("共享话术状态已变化，请重新选择话术。".into());
        }
        let phrase = {
            let inner = self.inner.lock().unwrap();
            inner.slots[&ctx.team]
                .snapshot
                .as_ref()
                .and_then(|s| s.phrases.iter().find(|p| p.id == reference.id))
                .cloned()
                .ok_or("话术已不存在。")?
        };
        if phrase.revision != reference.revision {
            return Err("话术已更新，请重新选择。".into());
        }
        let assets: Vec<_> = phrase
            .segments
            .iter()
            .flat_map(|s| &s.blocks)
            .filter_map(|b| match b {
                wire::Block::Image { asset_id, name } => {
                    Some((asset_id.clone(), name.clone(), true))
                }
                wire::Block::File { asset_id, name } => {
                    Some((asset_id.clone(), name.clone(), false))
                }
                _ => None,
            })
            .collect();
        let results = futures_util::stream::iter(assets.into_iter().map(|(id, name, image)| {
            let this = self.clone();
            let ctx = ctx.clone();
            async move { this.download(app, &ctx, &id, &name, image).await }
        }))
        .buffer_unordered(2)
        .collect::<Vec<_>>()
        .await;
        for r in results {
            r?;
        }
        self.phrase(key, true)?;
        Ok(())
    }
    pub fn preload(self: &Arc<Self>, app: tauri::AppHandle, team: String, ids: Vec<String>) {
        let Ok(ctx) = self.context(&team) else { return };
        let assets = {
            let inner = self.inner.lock().unwrap();
            let Some(snapshot) = inner.slots[&team].snapshot.as_ref() else {
                return;
            };
            let mut assets = vec![];
            for id in ids {
                let Some(reference) = wire::ContentRef::from_key(&id) else {
                    continue;
                };
                if let Some(p) = snapshot.phrases.iter().find(|p| p.id == reference.id) {
                    for b in p.segments.iter().flat_map(|s| &s.blocks) {
                        if let wire::Block::Image { asset_id, name } = b {
                            assets.push((asset_id.clone(), name.clone()));
                        }
                    }
                }
            }
            assets
        };
        let this = self.clone();
        tauri::async_runtime::spawn(async move {
            futures_util::stream::iter(assets.into_iter().map(|(id, name)| {
                let this = this.clone();
                let ctx = ctx.clone();
                let app = app.clone();
                async move {
                    let _ = this.download(&app, &ctx, &id, &name, true).await;
                }
            }))
            .buffer_unordered(2)
            .collect::<Vec<_>>()
            .await;
        });
    }
    pub async fn change_password(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        r: wire::PasswordChange,
    ) -> Result<()> {
        let active = self.active()?;
        self.request::<serde_json::Value>(
            &active,
            Method::PUT,
            "password",
            Some(serde_json::to_value(r).map_err(io_error)?),
        )
        .await?;
        self.refresh_authorization(app).await
    }
    pub async fn admin_request(
        &self,
        method: Method,
        path: &str,
        body: Option<serde_json::Value>,
        expected_epoch: Option<u64>,
    ) -> Result<(serde_json::Value, u64)> {
        let active = self.active()?;
        if expected_epoch.is_some_and(|epoch| epoch != active.epoch) {
            return Err("账号已切换，旧管理操作已失效。".into());
        }
        if active.offline || active.authorization.user.must_change_password {
            return Err("请连接服务器并完成首次改密后再管理共享话术。".into());
        }
        let result = self.request(&active, method, path, body).await?;
        if !self
            .inner
            .lock()
            .unwrap()
            .active
            .as_ref()
            .is_some_and(|a| a.epoch == active.epoch)
        {
            return Err("账号已切换，旧管理响应已失效。".into());
        }
        Ok((result, active.epoch))
    }
    /// 退出/删除共享话术成功即撤下本地授权。先等待此前授权刷新结束，防止其旧结果重新加回已退出组。
    pub async fn forget_team(&self, app: &tauri::AppHandle, team: &str, epoch: u64) -> Result<()> {
        let _refresh = self.auth_refresh.lock().await;
        let mut inner = self.inner.lock().unwrap();
        if !inner.active.as_ref().is_some_and(|a| a.epoch == epoch) {
            return Err("账号已切换，旧退出结果已失效。".into());
        }
        if let Some(slot) = inner.slots.remove(team) {
            slot.cancel.cancel();
        }
        let cleanup = if let Some(active) = inner.active.as_mut() {
            active.authorization.teams.retain(|t| t.team_id != team);
            (|| {
                self.persist_authorization(active)?;
                let dir = self.team_dir(active, team);
                if dir.exists() {
                    fs::remove_dir_all(dir).map_err(io_error)?;
                }
                Ok(())
            })()
        } else {
            Ok(())
        };
        drop(inner);
        self.invalidate_delivery(app);
        self.emit(app);
        cleanup
    }
    pub fn cached_snapshot(&self, team: &str) -> Result<wire::Snapshot> {
        let ctx = self.context(team)?;
        let inner = self.inner.lock().unwrap();
        if !Self::valid(&inner, &ctx) {
            return Err("共享话术授权已失效。".into());
        }
        inner.slots[team]
            .snapshot
            .clone()
            .ok_or("共享话术正在加载。".into())
    }
    pub fn transfer_identity(&self) -> Result<(String, String, u64)> {
        let active = self.active()?;
        Ok((
            active.connection.server,
            active.authorization.user.id,
            active.epoch,
        ))
    }
    pub fn transfer_epoch(&self) -> u64 {
        self.inner.lock().unwrap().epoch
    }
    pub fn transfer_namespace(&self) -> Result<String> {
        let active = self.active()?;
        Ok(format!(
            "{}-{}",
            active.connection.partition, active.authorization.user.id
        ))
    }
    pub fn with_transfer_epoch<T>(
        &self,
        epoch: u64,
        action: impl FnOnce() -> Result<T>,
    ) -> Result<T> {
        let inner = self.inner.lock().unwrap();
        if inner.epoch != epoch {
            return Err("会话已变化，请重新预览。".into());
        }
        action()
    }
    /// 预览之后的提交和素材打包仍检查同一账号、会话和生命周期；库内降权立即生效。
    pub fn check_transfer(
        &self,
        team: &str,
        server: &str,
        owner: &str,
        epoch: u64,
        lifecycle: u64,
        edit: bool,
    ) -> Result<()> {
        self.with_transfer_guard(team, (server, owner, epoch), lifecycle, edit, || Ok(()))
    }
    pub fn with_transfer_guard<T>(
        &self,
        team: &str,
        identity: (&str, &str, u64),
        lifecycle: u64,
        edit: bool,
        action: impl FnOnce() -> Result<T>,
    ) -> Result<T> {
        let (server, owner, epoch) = identity;
        let inner = self.inner.lock().unwrap();
        let active = inner.active.as_ref().ok_or("请先登录共享话术服务。")?;
        let info = active
            .authorization
            .teams
            .iter()
            .find(|t| t.team_id == team)
            .ok_or("共享话术权限已撤销。")?;
        if active.connection.server != server
            || active.authorization.user.id != owner
            || active.epoch != epoch
            || info.lifecycle_version != lifecycle
        {
            return Err("账号或共享话术已变化，请重新预览。".into());
        }
        if active.authorization.user.must_change_password {
            return Err("请先修改初始密码。".into());
        }
        if edit && (active.offline || !info.editable) {
            return Err("导入需要联网及共享话术编辑权限。".into());
        }
        action()
    }
    /// 列表隐藏未缓存素材的路径；导出固定素材引用时仍按ID取得受控缓存位置。
    pub fn transfer_asset_path(
        &self,
        team: &str,
        asset: &str,
        name: &str,
        image: bool,
    ) -> Result<String> {
        let ctx = self.context(team)?;
        Ok(local::external_path(
            &self.asset_path(&ctx, asset, name, image),
        ))
    }
    pub async fn transfer_download(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        team: &str,
        asset: &str,
        name: &str,
        image: bool,
    ) -> Result<()> {
        let ctx = self.context(team)?;
        self.download(app, &ctx, asset, name, image).await
    }
    pub async fn transfer_snapshot(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        team: &str,
    ) -> Result<wire::Snapshot> {
        let ctx = self.context(team)?;
        let snapshot: wire::Snapshot = self
            .request(
                &ctx.active,
                Method::GET,
                &format!("teams/{team}/snapshot"),
                None,
            )
            .await?;
        self.accept_snapshot(&ctx, snapshot.clone())?;
        self.emit(app);
        Ok(snapshot)
    }
    /// 共享话术导入只追加在用内容，复用批量发布。内容ID由持久化预览提供，绝不在上传时重新分配。
    pub async fn prepare_transfer(
        self: &Arc<Self>,
        team: &str,
        base: wire::Snapshot,
        pending: &crate::backup::PendingImport,
        merge: bool,
    ) -> Result<wire::Publish> {
        let ctx = self.context(team)?;
        let old_categories = base.categories.len();
        let old_phrases = base.phrases.len();
        let mut categories = base.categories;
        let mut phrases = base.phrases;
        let mut map = HashMap::new();
        let mut new_assets = vec![];
        let result = async {
            for c in pending
                .library
                .categories
                .iter()
                .filter(|c| c.parent_id.is_none())
                .chain(
                    pending
                        .library
                        .categories
                        .iter()
                        .filter(|c| c.parent_id.is_some()),
                )
            {
                let parent = c
                    .parent_id
                    .as_ref()
                    .map(|p| map.get(p).cloned().ok_or("导入分类缺少上级。".to_owned()))
                    .transpose()?;
                let matches: Vec<_> = categories
                    .iter()
                    .filter(|v| v.parent_id == parent && v.name.eq_ignore_ascii_case(&c.name))
                    .collect();
                if merge && matches.len() == 1 {
                    map.insert(c.id.clone(), matches[0].id.clone());
                    continue;
                }
                let position = categories.iter().map(|c| c.position).max().unwrap_or(-1) + 1;
                categories.push(wire::Category {
                    id: c.id.clone(),
                    name: c.name.clone(),
                    parent_id: parent,
                    position,
                    background: c.background.clone(),
                });
                map.insert(c.id.clone(), c.id.clone());
            }
            for original in &pending.library.phrases {
                self.check_transfer(
                    team,
                    &ctx.active.connection.server,
                    &ctx.active.authorization.user.id,
                    ctx.active.epoch,
                    ctx.lifecycle_version,
                    true,
                )?;
                let mut p = original.clone();
                p.root_id = map
                    .get(&p.root_id)
                    .cloned()
                    .ok_or("导入话术缺少一级分类。")?;
                p.group_id = p
                    .group_id
                    .as_ref()
                    .map(|g| {
                        map.get(g)
                            .cloned()
                            .ok_or("导入话术缺少二级分类。".to_owned())
                    })
                    .transpose()?;
                for block in p.segments.iter_mut().flat_map(|s| &mut s.blocks) {
                    if let local::Block::Image { path, .. } | local::Block::File { path, .. } =
                        block
                    {
                        *path = pending
                            .materials
                            .get(path)
                            .ok_or("导入素材缺失。")?
                            .to_string_lossy()
                            .into_owned();
                    }
                }
                let mut p = self.to_wire(&ctx, &p, &mut new_assets).await?;
                p.id = original.id.clone();
                p.revision = 0;
                p.position = phrases.iter().map(|p| p.position).max().unwrap_or(-1) + 1;
                phrases.push(p);
            }
            if phrases.len() == old_phrases && categories.len() == old_categories {
                return Err("没有可导入的共享话术或新分类。".into());
            }
            Ok(wire::Publish {
                expected_version: base.version,
                expected_lifecycle_version: base.lifecycle_version,
                categories,
                phrases,
                new_assets: new_assets.clone(),
            })
        }
        .await;
        if result.is_err() {
            let _ = self.cleanup_transfer(team, &new_assets).await;
        }
        result
    }
    pub async fn cleanup_transfer(&self, team: &str, assets: &[String]) -> Result<()> {
        let ctx = self.context(team)?;
        self.request::<serde_json::Value>(
            &ctx.active,
            Method::POST,
            &format!("teams/{team}/assets/cleanup"),
            Some(serde_json::json!(assets)),
        )
        .await?;
        Ok(())
    }
    async fn upload(&self, ctx: &Context, block: &local::Block) -> Result<wire::Block> {
        let (kind, path, name) = match block {
            local::Block::Image { path, name } => ("image", path, name),
            local::Block::File { path, name } => ("file", path, name),
            local::Block::Text { text } => return Ok(wire::Block::Text { text: text.clone() }),
        };
        let file = tokio::fs::File::open(path)
            .await
            .map_err(|_| "素材文件不存在或无法读取。")?;
        let size = file.metadata().await.map_err(io_error)?.len();
        if size > LIMIT || size == 0 {
            return Err("单个素材需为1字节至128MB。".into());
        }
        let response = self
            .client
            .post(format!(
                "{}/api/teams/{}/assets",
                ctx.active.connection.server, ctx.team
            ))
            .bearer_auth(&ctx.active.token)
            .header("x-team-lifecycle", ctx.lifecycle_version)
            .query(&[("kind", kind), ("name", name.as_str())])
            .timeout(Duration::from_secs(120))
            .body(reqwest::Body::wrap_stream(
                tokio_util::io::ReaderStream::new(file),
            ))
            .send()
            .await
            .map_err(|_| "素材上传中断或超时，请刷新确认结果；不会自动重试。")?;
        let asset = self
            .check(response)
            .await?
            .json::<wire::Asset>()
            .await
            .map_err(|_| "素材上传结果无法读取。")?;
        Ok(if kind == "image" {
            wire::Block::Image {
                asset_id: asset.id,
                name: name.clone(),
            }
        } else {
            wire::Block::File {
                asset_id: asset.id,
                name: name.clone(),
            }
        })
    }
    async fn to_wire(
        &self,
        ctx: &Context,
        p: &local::Phrase,
        new_assets: &mut Vec<String>,
    ) -> Result<wire::Phrase> {
        self.to_wire_selected(ctx, p, new_assets, &[]).await
    }
    async fn to_wire_selected(
        &self,
        ctx: &Context,
        p: &local::Phrase,
        new_assets: &mut Vec<String>,
        selections: &[CompanySelection],
    ) -> Result<wire::Phrase> {
        CompanySelection::validate(p, selections)?;
        let reference = wire::ContentRef::from_key(&p.id);
        if reference
            .as_ref()
            .is_some_and(|r| r.team_id != ctx.team || r.lifecycle_version != ctx.lifecycle_version)
        {
            return Err("话术引用与当前共享话术或版本不符，请重新选择。".into());
        }
        let mut next = wire::Phrase {
            id: reference
                .as_ref()
                .map(|r| r.id.clone())
                .unwrap_or_else(local::id),
            root_id: p.root_id.clone(),
            group_id: p.group_id.clone(),
            title: p.title.clone(),
            color: p.color.clone(),
            background: p.background.clone(),
            position: p.position,
            revision: reference.as_ref().map(|r| r.revision).unwrap_or(0),
            segments: vec![],
        };
        let snapshot = self.cached_snapshot(&ctx.team)?;
        let old = snapshot.phrases.iter().find(|v| v.id == next.id);
        for (si, segment) in p.segments.iter().enumerate() {
            let mut blocks = vec![];
            for (bi, block) in segment.blocks.iter().enumerate() {
                if let Some(selection) =
                    selections.iter().find(|s| s.segment == si && s.block == bi)
                {
                    if !matches!(block, local::Block::File { .. }) {
                        return Err("云文件选择与正文位置不符。".into());
                    }
                    let file = self.company_selection(selection).await?;
                    if selection.session_epoch != ctx.active.epoch {
                        return Err("账号已变化，请重新选择云文件。".into());
                    }
                    blocks.push(wire::Block::File {
                        asset_id: file.asset_id,
                        name: file.name,
                    });
                    continue;
                }
                let existing = old
                    .into_iter()
                    .flat_map(|p| &p.segments)
                    .flat_map(|s| &s.blocks)
                    .find(|b| match (b, block) {
                        (
                            wire::Block::Image { asset_id, name },
                            local::Block::Image { path, .. },
                        ) => {
                            !path.is_empty()
                                && local::external_path(&self.asset_path(ctx, asset_id, name, true))
                                    == *path
                        }
                        (wire::Block::File { asset_id, name }, local::Block::File { path, .. }) => {
                            !path.is_empty()
                                && local::external_path(
                                    &self.asset_path(ctx, asset_id, name, false),
                                ) == *path
                        }
                        _ => false,
                    });
                let block = if let Some(b) = existing {
                    b.clone()
                } else {
                    let b = self.upload(ctx, block).await?;
                    if let Some((id, _)) = b.asset() {
                        new_assets.push(id.into());
                    }
                    b
                };
                blocks.push(block);
            }
            next.segments.push(wire::Segment { blocks });
        }
        wire::validate_phrase(&next)?;
        Ok(next)
    }
    pub async fn publish(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        team: &str,
        r: wire::Publish,
    ) -> Result<()> {
        let ctx = self.context(team)?;
        let result = self
            .request::<wire::Snapshot>(
                &ctx.active,
                Method::PUT,
                &format!("teams/{team}/snapshot"),
                Some(serde_json::to_value(r).map_err(io_error)?),
            )
            .await;
        match result {
            Ok(snapshot) => {
                self.accept_snapshot(&ctx, snapshot)?;
                self.emit(app);
                Ok(())
            }
            Err(e) => {
                let _ = self.refresh_authorization(app).await;
                Err(e)
            }
        }
    }
    pub async fn save_phrase(self: &Arc<Self>, app: &tauri::AppHandle, draft: Draft) -> Result<()> {
        let ctx = self.context(&draft.team_id)?;
        let mut new_assets = vec![];
        let result = async {
            let mut snapshot = self.cached_snapshot(&draft.team_id)?;
            if snapshot.version != draft.expected_version
                || snapshot.lifecycle_version != draft.expected_lifecycle_version
            {
                return Err("共享话术已更新，请比较新内容后保存；草稿已保留。".into());
            }
            let phrase = self
                .to_wire_selected(&ctx, &draft.phrase, &mut new_assets, &draft.company_files)
                .await?;
            if let Some(old) = snapshot.phrases.iter_mut().find(|p| p.id == phrase.id) {
                *old = phrase;
            } else {
                snapshot.phrases.push(phrase);
            }
            self.publish(
                app,
                &draft.team_id,
                wire::Publish {
                    expected_version: draft.expected_version,
                    expected_lifecycle_version: draft.expected_lifecycle_version,
                    categories: snapshot.categories,
                    phrases: snapshot.phrases,
                    new_assets: new_assets.clone(),
                },
            )
            .await
        }
        .await;
        // 只对明确失败清理未发布素材；超时的写入先同步，服务端 committed 保护已提交素材。
        if result.is_err() && !new_assets.is_empty() {
            let _ = self
                .request::<serde_json::Value>(
                    &ctx.active,
                    Method::POST,
                    &format!("teams/{}/assets/cleanup", ctx.team),
                    Some(serde_json::json!(new_assets)),
                )
                .await;
        }
        result
    }

    pub async fn check_delivery(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        key: &str,
    ) -> Result<local::Phrase> {
        let reference = wire::ContentRef::from_key(key).ok_or("共享话术引用无效。")?;
        let active = self.active()?;
        let _ = self.refresh_authorization(app).await;
        let ctx = self.context(&reference.team_id)?;
        if ctx.active.epoch != active.epoch || ctx.lifecycle_version != reference.lifecycle_version
        {
            return Err("账号或共享话术已变化，请重新选择话术。".into());
        }
        if !ctx.active.offline {
            let remote = self
                .request::<wire::Phrase>(
                    &ctx.active,
                    Method::GET,
                    &format!("teams/{}/phrases/{}", reference.team_id, reference.id),
                    None,
                )
                .await?;
            if remote.revision != reference.revision {
                self.schedule(
                    app.clone(),
                    &reference.team_id,
                    self.cached_snapshot(&reference.team_id)?.version + 1,
                );
                return Err("话术已更新，请刷新后重新触发。".into());
            }
        }
        let phrase = self.phrase(key, false)?;
        if self.phrase(key, true).is_err() {
            self.prepare(app, key).await?;
            return Err("素材已下载完成，请重新点击投递；不会自动发送。".into());
        }
        self.begin_delivery(&ctx, &reference)?;
        Ok(phrase)
    }
    /// 最后核验与投递登记共用锁；加载期间切换账号、删除共享话术恢复或内容更新不能穿过此检查。
    fn begin_delivery(&self, ctx: &Context, reference: &wire::ContentRef) -> Result<()> {
        let mut inner = self.inner.lock().unwrap();
        if !Self::valid(&inner, ctx)
            || ctx.team != reference.team_id
            || ctx.lifecycle_version != reference.lifecycle_version
        {
            return Err("共享话术授权已变化，请重新选择话术。".into());
        }
        let unchanged = inner.slots[&ctx.team]
            .snapshot
            .as_ref()
            .is_some_and(|snapshot| {
                snapshot.lifecycle_version == reference.lifecycle_version
                    && snapshot
                        .phrases
                        .iter()
                        .any(|p| p.id == reference.id && p.revision == reference.revision)
            });
        if !unchanged {
            return Err("话术已更新，请重新选择。".into());
        }
        inner.delivery_team = Some((ctx.team.clone(), ctx.lifecycle_version));
        Ok(())
    }
    pub fn end_delivery(&self) {
        self.inner.lock().unwrap().delivery_team = None;
        self.member.lock().unwrap().delivery = None;
    }

    pub async fn save_category(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        team: &str,
        expected: u64,
        lifecycle: u64,
        category: local::Category,
    ) -> Result<()> {
        let mut snapshot = self.cached_snapshot(team)?;
        if snapshot.version != expected || snapshot.lifecycle_version != lifecycle {
            return Err("共享话术已更新，请刷新后重试。".into());
        }
        let category = wire::Category {
            id: if category.id.is_empty() {
                local::id()
            } else {
                category.id
            },
            name: category.name,
            parent_id: category.parent_id,
            position: category.position,
            background: category.background,
        };
        if let Some(old) = snapshot.categories.iter_mut().find(|c| c.id == category.id) {
            *old = category;
        } else {
            snapshot.categories.push(category);
        }
        self.publish(
            app,
            team,
            wire::Publish {
                expected_version: expected,
                expected_lifecycle_version: lifecycle,
                categories: snapshot.categories,
                phrases: snapshot.phrases,
                new_assets: vec![],
            },
        )
        .await
    }
    pub async fn delete(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        team: &str,
        r: wire::DeleteRequest,
    ) -> Result<()> {
        let ctx = self.context(team)?;
        let version = self
            .request::<u64>(
                &ctx.active,
                Method::POST,
                &format!("teams/{team}/recycle"),
                Some(serde_json::to_value(r).map_err(io_error)?),
            )
            .await?;
        self.schedule(app.clone(), team, version);
        Ok(())
    }
    /// 回收操作固定到发起时的账号与生命周期，不把旧窗口请求投向新登录账号。
    fn recycle_context(
        &self,
        team: &str,
        epoch: Option<u64>,
        lifecycle: Option<u64>,
    ) -> Result<Context> {
        let ctx = self.context(team)?;
        if epoch.is_some_and(|epoch| epoch != ctx.active.epoch)
            || lifecycle.is_some_and(|value| value != ctx.lifecycle_version)
        {
            return Err("账号或共享话术已变化，请重新打开回收站。".into());
        }
        if ctx.active.offline
            || ctx.active.authorization.user.must_change_password
            || !ctx
                .active
                .authorization
                .teams
                .iter()
                .any(|t| t.team_id == team && t.editable)
        {
            return Err("请联网并核对共享话术编辑权限后使用回收站。".into());
        }
        Ok(ctx)
    }
    pub async fn recycle(
        &self,
        team: &str,
        epoch: Option<u64>,
        lifecycle: Option<u64>,
    ) -> Result<Vec<wire::RecycleRecord>> {
        let ctx = self.recycle_context(team, epoch, lifecycle)?;
        let rows = self
            .request(
                &ctx.active,
                Method::GET,
                &format!("teams/{team}/recycle"),
                None,
            )
            .await?;
        if !Self::valid(&self.inner.lock().unwrap(), &ctx) {
            return Err("回收站来源已变化，请刷新。".into());
        }
        Ok(rows)
    }
    pub async fn restore(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        team: &str,
        id: &str,
        version: u64,
        lifecycle: u64,
        epoch: Option<u64>,
    ) -> Result<()> {
        let ctx = self.recycle_context(team, epoch, Some(lifecycle))?;
        let version = self.request::<u64>(&ctx.active, Method::POST, &format!("teams/{team}/recycle/{id}"), Some(serde_json::json!({"expectedVersion":version,"expectedLifecycleVersion":lifecycle}))).await?;
        if Self::valid(&self.inner.lock().unwrap(), &ctx) {
            self.schedule(app.clone(), team, version);
        }
        Ok(())
    }
    pub async fn purge(
        &self,
        team: &str,
        id: &str,
        lifecycle: u64,
        epoch: Option<u64>,
    ) -> Result<()> {
        let ctx = self.recycle_context(team, epoch, Some(lifecycle))?;
        self.request::<serde_json::Value>(
            &ctx.active,
            Method::DELETE,
            &format!("teams/{team}/recycle/{id}"),
            None,
        )
        .await?;
        Ok(())
    }
    /// 批量清空一次提交，不逐条重试；旧 Hub 的缺失接口单独提示升级。
    pub async fn empty_recycle(
        &self,
        team: &str,
        request: wire::ClearRecycleRequest,
        epoch: Option<u64>,
    ) -> Result<usize> {
        let ctx = self.recycle_context(team, epoch, Some(request.expected_lifecycle_version))?;
        self.request(
            &ctx.active,
            Method::POST,
            &format!("teams/{team}/recycle/clear"),
            Some(serde_json::to_value(request).map_err(io_error)?),
        )
        .await
        .map_err(|e| {
            // 旧 Hub 会将 clear 匹配为还原记录 ID，并因缺少还原参数返回 422。
            if e.starts_with("404:") || e.starts_with("405:") || e.starts_with("422:") {
                "服务端尚不支持清空共享话术的回收站，请升级 Hub；仍可逐条永久删除。".into()
            } else {
                e
            }
        })
    }
    pub async fn copy_from_private(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        team: &str,
        data: local::Library,
    ) -> Result<()> {
        let ctx = self.context(team)?;
        let mut snapshot = self.cached_snapshot(team)?;
        let expected = snapshot.version;
        let mut map = HashMap::new();
        let mut new_assets = vec![];
        for c in data
            .categories
            .iter()
            .filter(|c| c.parent_id.is_none())
            .chain(data.categories.iter().filter(|c| c.parent_id.is_some()))
        {
            let id = local::id();
            let parent_id = c
                .parent_id
                .as_ref()
                .map(|p| map.get(p).cloned().ok_or("来源分类层级无效。"))
                .transpose()?;
            map.insert(c.id.clone(), id.clone());
            snapshot.categories.push(wire::Category {
                id,
                name: c.name.clone(),
                parent_id,
                position: c.position,
                background: c.background.clone(),
            });
        }
        let result = async {
            for mut p in data.phrases {
                p.id = String::new();
                p.root_id = map.get(&p.root_id).cloned().ok_or("来源一级分类无效。")?;
                p.group_id = p
                    .group_id
                    .as_ref()
                    .map(|g| map.get(g).cloned().ok_or("来源二级分类无效。"))
                    .transpose()?;
                snapshot
                    .phrases
                    .push(self.to_wire(&ctx, &p, &mut new_assets).await?);
            }
            self.publish(
                app,
                team,
                wire::Publish {
                    expected_version: expected,
                    expected_lifecycle_version: snapshot.lifecycle_version,
                    categories: snapshot.categories,
                    phrases: snapshot.phrases,
                    new_assets: new_assets.clone(),
                },
            )
            .await
        }
        .await;
        if result.is_err() {
            let _ = self
                .request::<serde_json::Value>(
                    &ctx.active,
                    Method::POST,
                    &format!("teams/{team}/assets/cleanup"),
                    Some(serde_json::json!(new_assets)),
                )
                .await;
        }
        result
    }
    pub async fn copy_to_private(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        key: &str,
    ) -> Result<()> {
        let state = app.state::<AppState>();
        let library_id = crate::private_local::identity(&state.store.lock().unwrap())?;
        self.prepare(app, key).await?;
        let reference = wire::ContentRef::from_key(key).ok_or("共享话术引用无效。")?;
        let ctx = self.context(&reference.team_id)?;
        let snapshot = self.cached_snapshot(&reference.team_id)?;
        let mut phrase = self.phrase(key, true)?;
        let (dir, settings) = {
            let store = state.store.lock().unwrap();
            crate::private_local::scope(&store, Some(&library_id))?;
            (store.dir.clone(), store.settings()?)
        };
        let mut created = vec![];
        let result = (|| -> Result<()> {
            for block in phrase.segments.iter_mut().flat_map(|s| &mut s.blocks) {
                let image = matches!(block, local::Block::Image { .. });
                if let local::Block::Image { path, name } | local::Block::File { path, name } =
                    block
                {
                    let folder = if image { "images" } else { "private-files" };
                    fs::create_dir_all(dir.join(folder)).map_err(io_error)?;
                    let safe: String = name
                        .chars()
                        .map(|c| {
                            if c.is_control() || "<>:\"/\\|?*".contains(c) {
                                '_'
                            } else {
                                c
                            }
                        })
                        .take(120)
                        .collect();
                    let destination = dir.join(folder).join(format!(
                        "{}-{}",
                        local::id(),
                        if folder == "images" {
                            "image.png"
                        } else {
                            safe.trim_end_matches(['.', ' '])
                        }
                    ));
                    fs::copy(&*path, &destination).map_err(io_error)?;
                    created.push(destination.clone());
                    *path = local::external_path(&destination);
                }
            }
            let categories = snapshot
                .categories
                .into_iter()
                .filter(|c| c.id == phrase.root_id || phrase.group_id.as_ref() == Some(&c.id))
                .map(|c| local::Category {
                    id: c.id,
                    name: c.name,
                    kind: "phrase".into(),
                    parent_id: c.parent_id,
                    position: c.position,
                    background: c.background,
                })
                .collect();
            phrase.shortcut.clear();
            let data = local::Library {
                categories,
                phrases: vec![phrase],
                files: vec![],
                common_phrases: vec![],
                settings,
                data_dir: String::new(),
            };
            // 与私人提交一致：先锁目标库，再锁会话，避免同步落盘与复制互相等待。
            let mut store = state.store.lock().unwrap();
            crate::private_local::scope(&store, Some(&library_id))?;
            let inner = self.inner.lock().unwrap();
            if !Self::valid(&inner, &ctx) {
                return Err("复制期间授权已移除。".into());
            }
            store.import(&data, false, false, &[])?;
            Ok(())
        })();
        if result.is_err() {
            for file in created {
                let _ = fs::remove_file(file);
            }
        } else {
            crate::changed(app, &state)?;
        }
        result
    }
    pub async fn backup(&self, path: &Path) -> Result<()> {
        let active = self.active()?;
        let response = self.response(&active, Method::GET, "backup", None).await?;
        let temp = tempfile::NamedTempFile::new_in(path.parent().ok_or("备份路径无效。")?)
            .map_err(io_error)?;
        let mut file = tokio::fs::File::from_std(temp.reopen().map_err(io_error)?);
        let mut stream = response.bytes_stream();
        while let Some(chunk) = stream.next().await {
            file.write_all(&chunk.map_err(|_| "备份下载中断。")?)
                .await
                .map_err(io_error)?;
        }
        file.flush().await.map_err(io_error)?;
        drop(file);
        temp.persist(path).map_err(io_error)?;
        Ok(())
    }
    pub fn search(
        &self,
        query: &crate::search_match::SearchQuery,
        team: Option<&str>,
    ) -> Vec<serde_json::Value> {
        let teams = self.status().teams;
        let mut hits = vec![];
        for t in teams {
            if team.is_some_and(|id| id != t.team_id) {
                continue;
            }
            let Ok(view) = self.view(&t.team_id) else {
                continue;
            };
            for p in view.phrases {
                let content = format!(
                    "{} {} {}",
                    p.phrase.title,
                    p.phrase.text(),
                    p.phrase
                        .segments
                        .iter()
                        .flat_map(|s| &s.blocks)
                        .filter_map(|b| match b {
                            local::Block::Image { name, .. } | local::Block::File { name, .. } =>
                                Some(name.as_str()),
                            _ => None,
                        })
                        .collect::<Vec<_>>()
                        .join(" ")
                )
                .to_lowercase();
                if query.matches(&content) {
                    hits.push(serde_json::json!({"id":p.phrase.id,"kind":"phrase","phrase":p,"file":null,"sourceLabel":t.name,"teamId":t.team_id}));
                }
            }
        }
        hits
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    pub(super) fn setup() -> (tempfile::TempDir, Arc<TeamClient>, Context) {
        let dir = tempfile::tempdir().unwrap();
        let client = TeamClient::new(dir.path()).unwrap();
        let team = local::id();
        let active = Active {
            connection: Connection {
                server: "http://127.0.0.1:8080".into(),
                username: "001".into(),
                partition: local::id(),
            },
            token: "isolated-test-session".into(),
            epoch: 1,
            authorization: wire::Authorization {
                capabilities: wire::Capabilities::default(),
                user: wire::User {
                    id: local::id(),
                    username: "001".into(),
                    admin: false,
                    disabled: false,
                    must_change_password: false,
                    display_name: String::new(),
                    contact: String::new(),
                    can_create_teams: false,
                    company_file_permissions: wire::CompanyFilePermissions::default(),
                },
                teams: vec![wire::Team {
                    team_id: team.clone(),
                    name: "A".into(),
                    version: 0,
                    editable: false,
                    role: wire::Role::Member,
                    membership_version: 0,
                    lifecycle_version: 1,
                    member_count: None,
                }],
            },
            offline: true,
            cancel: CancellationToken::new(),
        };
        let generation = local::id();
        let ctx = Context {
            company_asset_ids: vec![],
            active: active.clone(),
            team: team.clone(),
            generation: generation.clone(),
            lifecycle_version: 1,
        };
        {
            let mut inner = client.inner.lock().unwrap();
            inner.epoch = 1;
            inner.active = Some(active);
            inner.slots.insert(
                team,
                Slot {
                    last_successful_sync_at: None,
                    snapshot: None,
                    wanted: 0,
                    refreshing: false,
                    generation,
                    cancel: CancellationToken::new(),
                },
            );
        }
        (dir, client, ctx)
    }
    fn snapshot(ctx: &Context, version: u64) -> wire::Snapshot {
        wire::Snapshot {
            company_asset_ids: vec![],
            team_id: ctx.team.clone(),
            version,
            lifecycle_version: ctx.lifecycle_version,
            categories: vec![],
            phrases: vec![],
        }
    }
    #[test]
    fn recycle_rejects_stale_account_lifecycle_and_revoked_permissions() {
        let (_dir, client, ctx) = setup();
        assert!(client.recycle_context(&ctx.team, Some(1), Some(1)).is_err());
        {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.offline = false;
            active.authorization.teams[0].editable = true;
        }
        assert!(client.recycle_context(&ctx.team, Some(1), Some(1)).is_ok());
        assert!(client.recycle_context(&ctx.team, Some(2), Some(1)).is_err());
        assert!(client.recycle_context(&ctx.team, Some(1), Some(2)).is_err());
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .authorization
            .teams[0]
            .editable = false;
        assert!(client.recycle_context(&ctx.team, Some(1), Some(1)).is_err());
    }
    #[test]
    fn empty_recycle_explains_old_hub_responses_without_retrying() {
        use std::io::{Read, Write};
        use std::net::TcpListener;
        let runtime = tokio::runtime::Runtime::new().unwrap();
        for status in [404, 405, 422] {
            let (_dir, client, ctx) = setup();
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            {
                let mut inner = client.inner.lock().unwrap();
                let active = inner.active.as_mut().unwrap();
                active.connection.server = format!("http://{}", listener.local_addr().unwrap());
                active.offline = false;
                active.authorization.teams[0].editable = true;
            }
            let server = std::thread::spawn(move || {
                let (mut stream, _) = listener.accept().unwrap();
                stream
                    .set_read_timeout(Some(Duration::from_secs(5)))
                    .unwrap();
                let mut request = [0; 4096];
                let size = stream.read(&mut request).unwrap();
                assert!(String::from_utf8_lossy(&request[..size]).starts_with("POST /api/teams/"));
                // 只接收一次请求；若客户端错误重试，将得不到这个兼容提示。
                stream.write_all(format!("HTTP/1.1 {status} Unsupported\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").as_bytes()).unwrap();
            });
            let error = runtime
                .block_on(client.empty_recycle(
                    &ctx.team,
                    wire::ClearRecycleRequest {
                        ids: vec![local::id()],
                        expected_lifecycle_version: 1,
                    },
                    Some(1),
                ))
                .unwrap_err();
            server.join().unwrap();
            assert!(error.contains("请升级 Hub"), "{error}");
            assert!(error.contains("逐条永久删除"));
        }
    }
    #[test]
    fn sync_time_requires_current_complete_snapshot_and_is_persisted_per_team() {
        let (_dir, client, ctx) = setup();
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .offline = false;
        assert!(client.status().team_sync_times.is_empty());
        client
            .inner
            .lock()
            .unwrap()
            .slots
            .get_mut(&ctx.team)
            .unwrap()
            .wanted = 2;
        client.accept_snapshot(&ctx, snapshot(&ctx, 1)).unwrap();
        assert!(client.status().team_sync_times.is_empty());
        client.accept_snapshot(&ctx, snapshot(&ctx, 2)).unwrap();
        let time = client.status().team_sync_times[&ctx.team];
        let saved = load_json::<SyncTime>(
            &client
                .team_dir(&ctx.active, &ctx.team)
                .join("sync-time.json"),
        )
        .unwrap();
        assert_eq!(saved.timestamp, time);
        assert_eq!(saved.version, 2);
        client.accept_snapshot(&ctx, snapshot(&ctx, 1)).unwrap();
        assert_eq!(client.status().team_sync_times[&ctx.team], time);
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .offline = true;
        client.accept_snapshot(&ctx, snapshot(&ctx, 3)).unwrap();
        assert_eq!(client.status().team_sync_times[&ctx.team], time);
        client.inner.lock().unwrap().active.as_mut().unwrap().epoch += 1;
        assert!(client.accept_snapshot(&ctx, snapshot(&ctx, 4)).is_err());
        assert_eq!(client.status().team_sync_times[&ctx.team], time);
    }
    #[test]
    fn addresses_allow_lan_http_and_https_without_embedded_credentials() {
        assert_eq!(
            normalize_server("http://192.168.1.10:8080/").unwrap(),
            "http://192.168.1.10:8080"
        );
        assert!(normalize_server("https://example.com/shanyu").is_ok());
        assert!(normalize_server("http://[::1]:8080").is_ok());
        for value in [
            "ftp://server",
            "https://user:password@example.com",
            "http://server?token=secret",
        ] {
            assert!(normalize_server(value).is_err());
        }
    }
    #[test]
    fn private_old_account_context_cannot_write_after_logout_or_relogin() {
        let (_dir, client, _) = setup();
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .authorization
            .capabilities
            .private_sync = true;
        let old = client.account_context().unwrap();
        assert!(client.with_account(&old, || Ok(())).is_ok());
        {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.cancel.cancel();
            active.epoch += 1;
            active.cancel = CancellationToken::new();
        }
        let mut written = false;
        assert!(client
            .with_account(&old, || {
                written = true;
                Ok(())
            })
            .is_err());
        assert!(!written);
        let current = client.account_context().unwrap();
        assert!(client.with_account(&current, || Ok(())).is_ok());
        client.inner.lock().unwrap().active = None;
        assert!(client
            .with_account(&current, || {
                written = true;
                Ok(())
            })
            .is_err());
        assert!(!written);
    }
    #[test]
    fn transfer_guard_allows_offline_export_but_rejects_stale_or_revoked_writes() {
        let (_dir, client, ctx) = setup();
        let server = &ctx.active.connection.server;
        let owner = &ctx.active.authorization.user.id;
        assert!(client
            .check_transfer(&ctx.team, server, owner, 1, 1, false)
            .is_ok());
        assert!(client
            .check_transfer(&ctx.team, server, owner, 1, 1, true)
            .is_err());
        {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.offline = false;
            active.authorization.teams[0].editable = true;
        }
        assert!(client
            .check_transfer(&ctx.team, server, owner, 1, 1, true)
            .is_ok());
        for (epoch, lifecycle, wrong_owner) in [(0, 1, false), (1, 2, false), (1, 1, true)] {
            let mut committed = false;
            assert!(client
                .with_transfer_guard(
                    &ctx.team,
                    (
                        server,
                        if wrong_owner {
                            "another-account"
                        } else {
                            owner
                        },
                        epoch
                    ),
                    lifecycle,
                    true,
                    || {
                        committed = true;
                        Ok(())
                    }
                )
                .is_err());
            assert!(!committed);
        }
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .authorization
            .teams[0]
            .editable = false;
        assert!(client
            .check_transfer(&ctx.team, server, owner, 1, 1, true)
            .is_err());
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .authorization
            .teams
            .clear();
        assert!(client
            .check_transfer(&ctx.team, server, owner, 1, 1, false)
            .is_err());
    }
    #[test]
    fn consecutive_notifications_run_one_task_and_old_responses_never_overwrite_new_cache() {
        let (_dir, client, ctx) = setup();
        assert!(client.claim_refresh(&ctx, 1));
        assert!(!client.claim_refresh(&ctx, 2));
        assert!(!client.claim_refresh(&ctx, 3));
        client.accept_snapshot(&ctx, snapshot(&ctx, 1)).unwrap();
        assert!(client.finish_refresh(&ctx));
        client.accept_snapshot(&ctx, snapshot(&ctx, 3)).unwrap();
        assert!(!client.finish_refresh(&ctx));
        client.accept_snapshot(&ctx, snapshot(&ctx, 2)).unwrap();
        assert_eq!(client.cached_snapshot(&ctx.team).unwrap().version, 3);
        let cached: wire::Snapshot = load_json(
            &client
                .team_dir(&ctx.active, &ctx.team)
                .join("snapshot.json"),
        )
        .unwrap();
        assert_eq!(cached.version, 3);
        assert!(!client.claim_refresh(&ctx, 2));
    }
    #[test]
    fn removed_and_readded_membership_cannot_accept_pre_removal_snapshot_or_download() {
        let (_dir, client, ctx) = setup();
        client.accept_snapshot(&ctx, snapshot(&ctx, 1)).unwrap();
        {
            let mut inner = client.inner.lock().unwrap();
            inner.slots.remove(&ctx.team).unwrap().cancel.cancel();
            inner.active.as_mut().unwrap().authorization.teams.clear();
        }
        assert!(client.accept_snapshot(&ctx, snapshot(&ctx, 2)).is_err());
        {
            let mut inner = client.inner.lock().unwrap();
            inner.active.as_mut().unwrap().authorization.teams =
                ctx.active.authorization.teams.clone();
            inner.slots.insert(
                ctx.team.clone(),
                Slot {
                    last_successful_sync_at: None,
                    snapshot: None,
                    wanted: 2,
                    refreshing: false,
                    generation: local::id(),
                    cancel: CancellationToken::new(),
                },
            );
        }
        assert!(client.accept_snapshot(&ctx, snapshot(&ctx, 2)).is_err());
        let temp = tempfile::NamedTempFile::new().unwrap();
        let path = client.asset_path(&ctx, &local::id(), "文件.txt", false);
        assert!(client.persist_download(&ctx, temp, &path).is_err());
        assert!(!path.exists());
        let current = client.context(&ctx.team).unwrap();
        client
            .accept_snapshot(&current, snapshot(&current, 2))
            .unwrap();
        assert_eq!(client.cached_snapshot(&ctx.team).unwrap().version, 2);
    }
    #[test]
    fn fast_delete_restore_rejects_old_cache_download_and_visible_content_reference() {
        let (_dir, client, ctx) = setup();
        let mut before = snapshot(&ctx, 1);
        before.phrases.push(wire::Phrase {
            id: local::id(),
            root_id: local::id(),
            group_id: None,
            title: "生命周期验证".into(),
            color: String::new(),
            background: String::new(),
            position: 0,
            revision: 1,
            segments: vec![wire::Segment {
                blocks: vec![wire::Block::Text {
                    text: "测试内容".into(),
                }],
            }],
        });
        client.accept_snapshot(&ctx, before.clone()).unwrap();
        let visible = client.view(&ctx.team).unwrap().phrases[0].phrase.id.clone();
        assert!(client.phrase(&visible, true).is_ok());
        let reference = wire::ContentRef::from_key(&visible).unwrap();
        client.begin_delivery(&ctx, &reference).unwrap();
        client.end_delivery();
        // 即使旧 Slot 暂时还在，当前授权生命周期也必须使它失效。
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .authorization
            .teams[0]
            .lifecycle_version = 3;
        assert!(client.view(&ctx.team).is_err());
        assert!(client.begin_delivery(&ctx, &reference).is_err());
        assert!(client.accept_snapshot(&ctx, before.clone()).is_err());
        assert!(!client.claim_refresh(&ctx, 2));
        let path = client.asset_path(&ctx, &local::id(), "旧下载.txt", false);
        assert!(client
            .persist_download(&ctx, tempfile::NamedTempFile::new().unwrap(), &path)
            .is_err());
        assert!(!path.exists());
        let current = client.context(&ctx.team).unwrap();
        assert!(client.begin_delivery(&current, &reference).is_err());
        assert!(client.accept_snapshot(&current, before.clone()).is_err());
        before.lifecycle_version = 3;
        before.version = 2;
        client.accept_snapshot(&current, before).unwrap();
        assert!(client.phrase(&visible, true).is_err());
        let restored = client.view(&ctx.team).unwrap();
        assert!(client.phrase(&restored.phrases[0].phrase.id, true).is_ok());
        let current_ref = wire::ContentRef::from_key(&restored.phrases[0].phrase.id).unwrap();
        client.begin_delivery(&current, &current_ref).unwrap();
        assert_eq!(
            client.inner.lock().unwrap().delivery_team,
            Some((ctx.team.clone(), 3))
        );
        client.end_delivery();
        let mut updated = client.cached_snapshot(&ctx.team).unwrap();
        updated.version += 1;
        updated.phrases[0].revision += 1;
        client.accept_snapshot(&current, updated).unwrap();
        assert!(client.begin_delivery(&current, &current_ref).is_err());
        assert!(client.inner.lock().unwrap().delivery_team.is_none());
    }
    #[test]
    fn logout_and_new_session_invalidate_old_download_before_cache_directory_can_reappear() {
        let (_dir, client, ctx) = setup();
        let path = client.asset_path(&ctx, &local::id(), "文件.txt", false);
        {
            let mut inner = client.inner.lock().unwrap();
            inner.epoch += 1;
            inner.active = None;
            inner.slots.clear();
        }
        let temp = tempfile::NamedTempFile::new().unwrap();
        assert!(client.persist_download(&ctx, temp, &path).is_err());
        assert!(!path.parent().unwrap().exists());
        {
            let mut inner = client.inner.lock().unwrap();
            let mut active = ctx.active.clone();
            active.epoch = 2;
            inner.active = Some(active);
            inner.slots.insert(
                ctx.team.clone(),
                Slot {
                    last_successful_sync_at: None,
                    snapshot: None,
                    wanted: 0,
                    refreshing: false,
                    generation: ctx.generation.clone(),
                    cancel: CancellationToken::new(),
                },
            );
        }
        assert!(client.accept_snapshot(&ctx, snapshot(&ctx, 1)).is_err());
    }
    #[test]
    fn pinyin_search_preserves_team_and_authorization_scope() {
        let (_dir, client, ctx) = setup();
        let mut next = snapshot(&ctx, 1);
        next.phrases.push(wire::Phrase {
            id: local::id(),
            root_id: local::id(),
            group_id: None,
            title: "技术手册UPS2026".into(),
            color: String::new(),
            background: String::new(),
            position: 0,
            revision: 1,
            segments: vec![wire::Segment {
                blocks: vec![wire::Block::Text {
                    text: "售后服务".into(),
                }],
            }],
        });
        client.accept_snapshot(&ctx, next).unwrap();
        for exact in [false, true] {
            for input in [
                "技术手册",
                "JISHUSHOUCEups2026",
                "JSSCups2026",
                "shouhoufuwu",
            ] {
                let query = crate::search_match::SearchQuery::new(input, exact);
                assert_eq!(client.search(&query, Some(&ctx.team)).len(), 1);
                assert!(client.search(&query, Some("other-team")).is_empty());
            }
        }
        assert_eq!(
            client
                .search(
                    &crate::search_match::SearchQuery::new("jssc shfw", false),
                    None
                )
                .len(),
            1
        );
        assert!(client
            .search(
                &crate::search_match::SearchQuery::new("jssc shfw", true),
                None
            )
            .is_empty());
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .authorization
            .teams
            .clear();
        assert!(client
            .search(&crate::search_match::SearchQuery::new("jssc", false), None)
            .is_empty());
    }
    #[test]
    fn incomplete_assets_block_whole_phrase_and_revision_cannot_silently_change() {
        let (_dir, client, ctx) = setup();
        let mut next = snapshot(&ctx, 1);
        let id = local::id();
        let asset = local::id();
        let reference = wire::ContentRef {
            source: "team".into(),
            team_id: ctx.team.clone(),
            id: id.clone(),
            revision: 1,
            lifecycle_version: 1,
        };
        next.phrases.push(wire::Phrase {
            id,
            root_id: local::id(),
            group_id: None,
            title: "带附件".into(),
            color: String::new(),
            background: String::new(),
            position: 0,
            revision: 1,
            segments: vec![wire::Segment {
                blocks: vec![
                    wire::Block::Text {
                        text: "前文".into(),
                    },
                    wire::Block::File {
                        asset_id: asset.clone(),
                        name: "附件.txt".into(),
                    },
                ],
            }],
        });
        client.accept_snapshot(&ctx, next.clone()).unwrap();
        assert!(client.phrase(&reference.key(), false).is_ok());
        assert!(client.phrase(&reference.key(), true).is_err());
        let path = client.asset_path(&ctx, &asset, "附件.txt", false);
        let mut temp = tempfile::NamedTempFile::new().unwrap();
        std::io::Write::write_all(&mut temp, b"complete").unwrap();
        client.persist_download(&ctx, temp, &path).unwrap();
        assert!(client.phrase(&reference.key(), true).is_ok());
        fs::OpenOptions::new()
            .write(true)
            .open(&path)
            .unwrap()
            .set_len(2)
            .unwrap();
        assert!(client.phrase(&reference.key(), true).is_err());
        next.version = 2;
        next.phrases[0].revision = 2;
        client.accept_snapshot(&ctx, next).unwrap();
        assert!(client.phrase(&reference.key(), false).is_err());
    }
}
