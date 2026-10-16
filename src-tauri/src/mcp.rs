//! 本机 AI 连接：SDK 处理 MCP 协议，本模块只暴露明确授权的库与有限业务操作。
//! 每次调用重新核验账号、授权与版本；不提供发送、剪贴板、永久删除或任意文件路径接口。
use crate::{
    model::{self, *},
    private_local,
    store::{db_error, Store},
    AppState,
};
use axum::{
    extract::{Request, State as HttpState},
    http::StatusCode,
    middleware::{self, Next},
    response::{IntoResponse, Response},
    Router,
};
use rmcp::{
    model::{
        CallToolRequestParams, CallToolResponse, CallToolResult, ContentBlock, ListToolsResult,
        PaginatedRequestParams, ServerCapabilities, ServerConfig, Tool,
    },
    service::RequestContext,
    transport::streamable_http_server::{
        session::local::LocalSessionManager, StreamableHttpServerConfig, StreamableHttpService,
    },
    ErrorData, RoleServer, ServerHandler,
};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    path::{Component, Path, PathBuf},
    sync::{Arc, Mutex},
};
use tauri::{Emitter, Listener, Manager, State};
use tokio_util::sync::CancellationToken;

#[derive(Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Grant {
    pub source: String,
    pub library_id: String,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Root {
    pub id: String,
    pub path: String,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase", deny_unknown_fields)]
pub struct Config {
    pub enabled: bool,
    pub port: u16,
    pub grants: Vec<Grant>,
    pub roots: Vec<Root>,
}
impl Default for Config {
    fn default() -> Self {
        Self {
            enabled: false,
            port: 17823,
            grants: vec![],
            roots: vec![],
        }
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Patch {
    enabled: Option<bool>,
    port: Option<u16>,
    grants: Option<Vec<Grant>>,
    roots: Option<Vec<Root>>,
}
#[derive(Clone, PartialEq, Eq)]
struct Account {
    key: String,
    epoch: u64,
}
impl Account {
    fn stamp(&self) -> String {
        json!([self.key, self.epoch]).to_string()
    }
}
fn account(state: &AppState) -> Result<Account> {
    let library = private_local::identity(&state.store.lock().unwrap())?;
    let status = state.teams.status();
    Ok(Account {
        key: json!([library, status.server, status.user.map(|u| u.id)]).to_string(),
        epoch: status.session_epoch,
    })
}
fn config(state: &AppState, account: &Account) -> Result<Config> {
    let value = private_local::meta(
        &state.device_store.lock().unwrap(),
        &format!("mcp:{}", account.key),
    )?;
    if value.is_empty() {
        Ok(Config::default())
    } else {
        serde_json::from_str(&value).map_err(db_error)
    }
}
fn save_config(state: &AppState, current: &Account, cfg: &Config) -> Result<()> {
    let store = state.device_store.lock().unwrap();
    let tx = store.conn.unchecked_transaction().map_err(db_error)?;
    private_local::set_meta(
        &store,
        &format!("mcp:{}", current.key),
        &serde_json::to_string(cfg).map_err(db_error)?,
    )?;
    tx.commit().map_err(db_error)
}
fn token_partition() -> String {
    #[cfg(debug_assertions)]
    if let Ok(dir) = std::env::var("SHANYU_TEST_DATA_DIR") {
        return format!("test:{dir}");
    }
    "local".into()
}
fn token() -> Result<String> {
    if let Some(value) = crate::mcp_credentials::read(&token_partition())? {
        return Ok(value);
    }
    let value = format!(
        "{}{}",
        uuid::Uuid::new_v4().simple(),
        uuid::Uuid::new_v4().simple()
    );
    crate::mcp_credentials::write(&token_partition(), &value)?;
    Ok(value)
}
struct Running {
    account: Account,
    cancel: CancellationToken,
    task: tauri::async_runtime::JoinHandle<()>,
}
/// 配置修改和业务调用共用异步锁；撤权返回后，不会再有使用旧授权的写入。
#[derive(Default)]
pub struct Mcp {
    gate: tokio::sync::Mutex<()>,
    running: Mutex<Option<Running>>,
    error: Mutex<String>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    account: String,
    config: Config,
    running: bool,
    error: String,
}
#[tauri::command]
pub fn get_mcp_status(state: State<AppState>) -> Result<Status> {
    let current = account(&state)?;
    let running = state
        .mcp
        .running
        .lock()
        .unwrap()
        .as_ref()
        .is_some_and(|r| r.account == current && !r.cancel.is_cancelled());
    Ok(Status {
        config: config(&state, &current)?,
        account: current.stamp(),
        running,
        error: state.mcp.error.lock().unwrap().clone(),
    })
}
async fn stop(state: &AppState) {
    let running = state.mcp.running.lock().unwrap().take();
    if let Some(r) = running {
        r.cancel.cancel();
        // 配置调用持有 gate；旧 HTTP 调用可能正在等同一把锁，不能等待它们排空。
        // 取消协议会话后终止接收任务，排队请求拿到锁时会再次检查 cancel。
        r.task.abort();
        let _ = r.task.await;
    }
}
#[derive(Clone)]
struct Endpoint {
    app: tauri::AppHandle,
    account: Account,
    token: String,
    cancel: CancellationToken,
}
fn authorized(headers: &axum::http::HeaderMap, secret: &str) -> bool {
    // Origin 存在即拒绝，避免本地网页借用已知令牌；Host 由 SDK 再核验。
    !headers.contains_key("origin")
        && headers
            .get("authorization")
            .and_then(|v| v.to_str().ok())
            .is_some_and(|v| v.strip_prefix("Bearer ") == Some(secret))
}
async fn authenticate(
    HttpState(endpoint): HttpState<Endpoint>,
    request: Request,
    next: Next,
) -> Response {
    if !authorized(request.headers(), &endpoint.token) {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    if endpoint.cancel.is_cancelled()
        || account(&endpoint.app.state::<AppState>()).ok().as_ref() != Some(&endpoint.account)
    {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    next.run(request).await
}
async fn run(app: &tauri::AppHandle, current: Account, cfg: &Config) -> Result<()> {
    if !cfg.enabled {
        return Ok(());
    }
    let listener = tokio::net::TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, cfg.port))
        .await
        .map_err(|_| format!("无法启动 AI 连接：本机端口 {} 已被占用或不可用。", cfg.port))?;
    if account(&app.state::<AppState>())? != current {
        return Err("账号已变化，请重新启用 AI 连接。".into());
    }
    let cancel = CancellationToken::new();
    let endpoint = Endpoint {
        app: app.clone(),
        account: current.clone(),
        token: token()?,
        cancel: cancel.clone(),
    };
    let mut options = StreamableHttpServerConfig::default().enforce_origin_validation();
    options.cancellation_token = cancel.clone();
    options.allowed_hosts = vec![format!("127.0.0.1:{}", cfg.port)];
    options.json_response = true;
    let factory = endpoint.clone();
    let service = StreamableHttpService::new(
        move || Ok(Handler(factory.clone())),
        Arc::new(LocalSessionManager::default()),
        options,
    );
    let router = Router::new()
        .nest_service("/mcp", service)
        .layer(middleware::from_fn_with_state(endpoint, authenticate));
    let shutdown = cancel.clone();
    let task = tauri::async_runtime::spawn(async move {
        let _ = axum::serve(listener, router)
            .with_graceful_shutdown(shutdown.cancelled_owned())
            .await;
    });
    *app.state::<AppState>().mcp.running.lock().unwrap() = Some(Running {
        account: current,
        cancel,
        task,
    });
    Ok(())
}
#[tauri::command]
pub async fn patch_mcp_settings(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    account_key: String,
    patch: Patch,
) -> Result<Status> {
    let _activity = crate::update_activity::Activity::enter()?;
    let _guard = state.mcp.gate.lock().await;
    let current = account(&state)?;
    if current.stamp() != account_key {
        return Err("账号已变化，请重新打开 AI 连接设置。".into());
    }
    let mut cfg = config(&state, &current)?;
    if let Some(v) = patch.enabled {
        cfg.enabled = v;
    }
    if let Some(v) = patch.port {
        if v < 1024 {
            return Err("端口应为1024—65535。".into());
        }
        cfg.port = v;
    }
    if let Some(v) = patch.grants {
        for grant in &v {
            available(&state, grant)?;
        }
        cfg.grants = v;
    }
    if let Some(mut v) = patch.roots {
        for root in &mut v {
            let path = Path::new(&root.path)
                .canonicalize()
                .map_err(|_| "素材目录不可访问。")?;
            if !path.is_dir() || root.id.is_empty() {
                return Err("请选择有效的素材目录。".into());
            }
            root.path = model::external_path(&path);
        }
        if v.iter()
            .enumerate()
            .any(|(i, r)| v[..i].iter().any(|o| o.id == r.id))
        {
            return Err("素材目录标识重复。".into());
        }
        cfg.roots = v;
    }
    // 先持久化授权再启动监听；保存失败时不改变现有服务，避免显示与实际权限不一致。
    if account(&state)? != current {
        return Err("账号已变化，请重新打开 AI 连接设置。".into());
    }
    save_config(&state, &current, &cfg)?;
    stop(&state).await;
    let result = run(&app, current.clone(), &cfg).await;
    *state.mcp.error.lock().unwrap() = result.as_ref().err().cloned().unwrap_or_default();
    if result.is_err() {
        cfg.enabled = false;
        save_config(&state, &current, &cfg)?;
    }
    let _ = app.emit("mcp-changed", ());
    get_mcp_status(state.clone())
}
#[tauri::command]
pub async fn reset_mcp_token(app: tauri::AppHandle, state: State<'_, AppState>) -> Result<()> {
    let _guard = state.mcp.gate.lock().await;
    stop(&state).await;
    crate::mcp_credentials::delete(&token_partition())?;
    token()?;
    let current = account(&state)?;
    let cfg = config(&state, &current)?;
    let result = run(&app, current, &cfg).await;
    *state.mcp.error.lock().unwrap() = result.as_ref().err().cloned().unwrap_or_default();
    let _ = app.emit("mcp-changed", ());
    result
}
#[tauri::command]
pub fn get_mcp_connection(state: State<AppState>) -> Result<Value> {
    let current = account(&state)?;
    let cfg = config(&state, &current)?;
    Ok(
        json!({"mcpServers":{"quickphrase":{"type":"http","url":format!("http://127.0.0.1:{}/mcp",cfg.port),"headers":{"Authorization":format!("Bearer {}",token()?)}}}}),
    )
}
pub fn start(app: tauri::AppHandle) {
    let owner = app.clone();
    app.listen("teams-changed", move |_| {
        let app = owner.clone();
        tauri::async_runtime::spawn(async move {
            let state = app.state::<AppState>();
            let _guard = state.mcp.gate.lock().await;
            let current = account(&state).ok();
            let stale = state
                .mcp
                .running
                .lock()
                .unwrap()
                .as_ref()
                .is_some_and(|r| Some(&r.account) != current.as_ref());
            if stale {
                stop(&state).await;
                let _ = app.emit("mcp-changed", ());
            }
        });
    });
    tauri::async_runtime::spawn(async move {
        let state = app.state::<AppState>();
        let _guard = state.mcp.gate.lock().await;
        let result = async {
            let current = account(&state)?;
            let cfg = config(&state, &current)?;
            run(&app, current, &cfg).await
        }
        .await;
        if let Err(e) = result {
            *state.mcp.error.lock().unwrap() = e;
        }
    });
}
fn available(state: &AppState, g: &Grant) -> Result<()> {
    match g.source.as_str() {
        "private" if private_local::identity(&state.store.lock().unwrap())? == g.library_id => {
            Ok(())
        }
        "team"
            if state
                .teams
                .status()
                .teams
                .iter()
                .any(|t| t.team_id == g.library_id) =>
        {
            Ok(())
        }
        _ => Err("该话术库已不可访问，请核对账号和授权。".into()),
    }
}

#[derive(Clone)]
struct Handler(Endpoint);
impl ServerHandler for Handler {
    fn get_info(&self) -> ServerConfig {
        ServerConfig::new(ServerCapabilities::builder().enable_tools().build()).with_instructions("仅操作用户授权的库。先读取版本再写入；写入结果不明确时读取核对，禁止自动重试。素材使用目录标识与相对路径，已有素材用当前话术内的索引引用。")
    }
    async fn list_tools(
        &self,
        _: Option<PaginatedRequestParams>,
        _: RequestContext<RoleServer>,
    ) -> std::result::Result<ListToolsResult, ErrorData> {
        Ok(ListToolsResult {
            tools: tools(),
            ..Default::default()
        })
    }
    async fn call_tool(
        &self,
        request: CallToolRequestParams,
        _: RequestContext<RoleServer>,
    ) -> std::result::Result<CallToolResponse, ErrorData> {
        let result = self
            .execute(
                &request.name,
                Value::Object(request.arguments.unwrap_or_default()),
            )
            .await;
        Ok(match result {
            Ok(v) => CallToolResult::success(vec![ContentBlock::text(v.to_string())]),
            Err(e) => CallToolResult::error(vec![ContentBlock::text(e)]),
        }
        .into())
    }
}
fn tools() -> Vec<Tool> {
    let base =
        json!({"source":{"type":"string","enum":["private","team"]},"libraryId":{"type":"string"}});
    let version = json!({"expectedRevision":{"type":"integer","minimum":0},"expectedVersion":{"type":"integer","minimum":0},"expectedLifecycleVersion":{"type":"integer","minimum":0}});
    let phrase = json!({"type":"object","description":"完整话术。id为空创建；更新必须提交读取时的版本。素材块禁止path，只能引用existing:{segment,block}或使用rootId+relativePath。","properties":{"id":{"type":"string"},"rootId":{"type":"string"},"groupId":{"type":["string","null"]},"title":{"type":"string"},"color":{"type":"string"},"background":{"type":"string"},"shortcut":{"type":"string"},"position":{"type":"integer"},"segments":{"type":"array","items":{"type":"object","properties":{"blocks":{"type":"array","items":{"type":"object","properties":{"kind":{"enum":["text","image","file"]},"text":{"type":"string"},"name":{"type":"string"},"existing":{"type":"object","properties":{"segment":{"type":"integer","minimum":0},"block":{"type":"integer","minimum":0}},"required":["segment","block"],"additionalProperties":false},"rootId":{"type":"string"},"relativePath":{"type":"string"}},"required":["kind"],"additionalProperties":false}}},"required":["blocks"],"additionalProperties":false}}},"required":["rootId","segments"],"additionalProperties":false});
    [
        ("list_libraries","列出明确授权的库和素材目录标识",json!({}),vec![]),
        ("list_categories","列出指定库的分类及版本",json!({}),vec![]),
        ("search_phrases","在指定授权库分页搜索，最多100条，沿用应用匹配规则",json!({"query":{"type":"string"},"offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":100}}),vec!["query"]),
        ("read_phrase","读取话术及素材索引、修订号",json!({"id":{"type":"string"}}),vec!["id"]),
        ("save_category","创建或修改话术分类；已有分类需expectedRevision（个人）或库版本（共享）",json!({"category":{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"kind":{"const":"phrase"},"parentId":{"type":["string","null"]},"position":{"type":"integer"},"background":{"type":"string"}},"required":["id","name","kind","parentId","position","background"],"additionalProperties":false}}),vec!["category"]),
        ("save_phrase","创建或修改话术，导入素材与本次保存一起提交，失败清理本次副本",json!({"phrase":phrase}),vec!["phrase"]),
        ("move_phrase","把话术移动到同一库的分类，不跨库",json!({"id":{"type":"string"},"rootId":{"type":"string"},"groupId":{"type":["string","null"]}}),vec!["id","rootId"]),
        ("sort_item","在当前分类内调整话术或分类顺序，position从0开始",json!({"id":{"type":"string"},"kind":{"enum":["phrase","category"]},"position":{"type":"integer","minimum":0}}),vec!["id","kind","position"]),
        ("recycle_phrase","回收话术，可在应用回收站还原；不永久删除",json!({"id":{"type":"string"}}),vec!["id"]),
    ].into_iter().map(|(name,description,extra,required)|{
        let mut props=if name=="list_libraries" {serde_json::Map::new()} else {base.as_object().unwrap().clone()};
        let mut req=if name=="list_libraries" {vec![]}else{vec!["source","libraryId"]};
        props.extend(extra.as_object().unwrap().clone()); req.extend(required);
        if ["save_category","save_phrase","move_phrase","sort_item","recycle_phrase"].contains(&name) {props.extend(version.as_object().unwrap().clone());}
        Tool::new(name,description,json!({"type":"object","properties":props,"required":req,"additionalProperties":false}).as_object().unwrap().clone())
    }).collect()
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Args {
    #[serde(default)]
    source: String,
    #[serde(default)]
    library_id: String,
    id: Option<String>,
    query: Option<String>,
    offset: Option<usize>,
    limit: Option<usize>,
    expected_revision: Option<u64>,
    expected_version: Option<u64>,
    expected_lifecycle_version: Option<u64>,
    phrase: Option<InputPhrase>,
    category: Option<Category>,
    root_id: Option<String>,
    group_id: Option<String>,
    kind: Option<String>,
    position: Option<usize>,
}
#[derive(Deserialize, Default)]
#[serde(default, rename_all = "camelCase", deny_unknown_fields)]
struct InputPhrase {
    id: String,
    root_id: String,
    group_id: Option<String>,
    title: String,
    color: String,
    background: String,
    shortcut: String,
    position: i64,
    segments: Vec<InputSegment>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct InputSegment {
    blocks: Vec<InputBlock>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct InputBlock {
    kind: String,
    text: Option<String>,
    name: Option<String>,
    existing: Option<Existing>,
    root_id: Option<String>,
    relative_path: Option<String>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct Existing {
    segment: usize,
    block: usize,
}
/// 输出不泄露本机文件路径；引用只在当前话术内解释，不能借用其他库的素材。
fn exposed(p: &Phrase, revision: u64) -> Value {
    let mut value = serde_json::to_value(p).unwrap();
    value["revision"] = json!(revision);
    for (si, s) in value["segments"]
        .as_array_mut()
        .unwrap()
        .iter_mut()
        .enumerate()
    {
        for (bi, b) in s["blocks"].as_array_mut().unwrap().iter_mut().enumerate() {
            if b["kind"] != "text" {
                b.as_object_mut().unwrap().remove("path");
                b["existing"] = json!({"segment":si,"block":bi});
            }
        }
    }
    value
}
fn revision(store: &Store, kind: &str, id: &str, expected: Option<u64>) -> Result<()> {
    if !id.is_empty() && expected != Some(private_local::revision(store, kind, id)?) {
        return Err("内容已变化或未提供修订号，请重新读取后核对。".into());
    }
    Ok(())
}
fn import_path(roots: &[Root], id: &str, relative: &str) -> Result<PathBuf> {
    let root = roots
        .iter()
        .find(|r| r.id == id)
        .ok_or("素材目录未授权。")?;
    let relative = Path::new(relative);
    if relative.as_os_str().is_empty()
        || relative
            .components()
            .any(|c| !matches!(c, Component::Normal(_)))
        || relative.to_string_lossy().contains(':')
    {
        return Err("素材必须使用授权目录内的相对路径。".into());
    }
    let resolved_root = Path::new(&root.path)
        .canonicalize()
        .map_err(|_| "素材目录不可访问。")?;
    // 授权时已保存规范路径。目录本身后来被替换为联接也不能扩大授权范围。
    if Path::new(&model::external_path(&resolved_root)) != Path::new(&root.path) {
        return Err("素材目录位置已变化，请重新授权。".into());
    }
    let root = resolved_root;
    let path = root
        .join(relative)
        .canonicalize()
        .map_err(|_| "素材不存在或不可访问。")?;
    if !path.starts_with(&root) || !path.is_file() {
        return Err("素材超出授权目录或不是文件。".into());
    }
    Ok(path)
}
/// 打开后按句柄核验最终路径，并禁止写入/删除共享；避免校验后路径被链接替换。
fn open_import(roots: &[Root], id: &str, relative: &str) -> Result<std::fs::File> {
    let path = import_path(roots, id, relative)?;
    let mut options = std::fs::OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options.share_mode(1);
    }
    let file = options
        .open(&path)
        .map_err(|_| "素材无法读取或正在被修改。")?;
    #[cfg(windows)]
    {
        use std::os::windows::io::AsRawHandle;
        use windows::Win32::{
            Foundation::HANDLE,
            Storage::FileSystem::{GetFinalPathNameByHandleW, FILE_NAME_NORMALIZED},
        };
        let mut buffer = vec![0u16; 32768];
        let len = unsafe {
            GetFinalPathNameByHandleW(
                HANDLE(file.as_raw_handle()),
                &mut buffer,
                FILE_NAME_NORMALIZED,
            )
        } as usize;
        if len == 0 || len >= buffer.len() {
            return Err("无法核对素材最终位置。".into());
        }
        let final_path = PathBuf::from(String::from_utf16_lossy(&buffer[..len]));
        let root = Path::new(
            &roots
                .iter()
                .find(|r| r.id == id)
                .ok_or("素材目录未授权。")?
                .path,
        )
        .canonicalize()
        .map_err(db_error)?;
        if !final_path.starts_with(root) {
            return Err("素材链接超出授权目录。".into());
        }
    }
    Ok(file)
}
fn materialize(
    input: InputPhrase,
    old: Option<&Phrase>,
    cfg: &Config,
    dir: &Path,
    batch: &mut private_local::MaterialBatch,
) -> Result<Phrase> {
    let mut segments = vec![];
    for segment in input.segments {
        let mut blocks = vec![];
        for b in segment.blocks {
            if b.kind == "text" {
                if b.existing.is_some() || b.root_id.is_some() || b.relative_path.is_some() {
                    return Err("文字块不能包含素材引用。".into());
                }
                blocks.push(Block::Text {
                    text: b.text.unwrap_or_default(),
                });
                continue;
            }
            if !["image", "file"].contains(&b.kind.as_str()) {
                return Err("素材类型无效。".into());
            }
            if let Some(r) = b.existing {
                if b.root_id.is_some() || b.relative_path.is_some() {
                    return Err("素材不能同时引用旧内容和新路径。".into());
                }
                let block = old
                    .and_then(|p| p.segments.get(r.segment))
                    .and_then(|s| s.blocks.get(r.block))
                    .ok_or("原素材引用已失效。")?;
                if !matches!(
                    (b.kind.as_str(), block),
                    ("image", Block::Image { .. }) | ("file", Block::File { .. })
                ) {
                    return Err("原素材类型不匹配。".into());
                }
                blocks.push(block.clone());
                continue;
            }
            let source = import_path(
                &cfg.roots,
                b.root_id.as_deref().ok_or("请选择素材目录标识。")?,
                b.relative_path.as_deref().ok_or("请填写素材相对路径。")?,
            )?;
            let name = b
                .name
                .unwrap_or_else(|| source.file_name().unwrap().to_string_lossy().into_owned());
            let mut file = open_import(
                &cfg.roots,
                b.root_id.as_deref().unwrap(),
                b.relative_path.as_deref().unwrap(),
            )?;
            let size = file.metadata().map_err(db_error)?.len();
            if size == 0 || size > 128 * 1024 * 1024 {
                return Err("素材需大于0且不超过128MB。".into());
            }
            let folder = dir
                .join(if b.kind == "image" {
                    "images"
                } else {
                    "assets"
                })
                .join(model::id());
            std::fs::create_dir_all(&folder).map_err(db_error)?;
            let target = folder.join(if b.kind == "image" {
                "image.png".into()
            } else {
                source.file_name().unwrap().to_os_string()
            });
            batch.track(target.clone());
            if b.kind == "image" {
                let mut bytes = vec![];
                std::io::Read::read_to_end(&mut file, &mut bytes).map_err(db_error)?;
                crate::backup::decode_image(&bytes)?
                    .save_with_format(&target, image::ImageFormat::Png)
                    .map_err(db_error)?;
            } else {
                let mut output = std::fs::File::create(&target).map_err(db_error)?;
                std::io::copy(&mut file, &mut output).map_err(db_error)?;
            }
            let path = model::external_path(&target);
            blocks.push(if b.kind == "image" {
                Block::Image { path, name }
            } else {
                Block::File { path, name }
            });
        }
        segments.push(Segment { blocks });
    }
    let phrase = Phrase {
        id: input.id,
        root_id: input.root_id,
        group_id: input.group_id,
        title: input.title,
        color: input.color,
        background: input.background,
        shortcut: input.shortcut,
        position: input.position,
        segments,
    };
    crate::model::validate_phrase(&phrase)?;
    Ok(phrase)
}
impl Handler {
    async fn execute(&self, name: &str, value: Value) -> Result<Value> {
        if !tools().iter().any(|t| t.name == name) {
            return Err("不支持的工具。".into());
        }
        let args: Args =
            serde_json::from_value(value).map_err(|_| "工具参数无效，请按工具定义填写。")?;
        let state = self.0.app.state::<AppState>();
        let _guard = state.mcp.gate.lock().await;
        if self.0.cancel.is_cancelled() || account(&state)? != self.0.account {
            return Err("AI 连接已失效，请重新连接。".into());
        }
        let cfg = config(&state, &self.0.account)?;
        if !cfg.enabled {
            return Err("AI 连接已关闭。".into());
        }
        if name == "list_libraries" {
            return Ok(
                json!({"libraries":cfg.grants.iter().filter(|g|available(&state,g).is_ok()).collect::<Vec<_>>(),"materialRoots":cfg.roots.iter().map(|r|json!({"id":r.id})).collect::<Vec<_>>()}),
            );
        }
        let grant = Grant {
            source: args.source.clone(),
            library_id: args.library_id.clone(),
        };
        if !cfg.grants.contains(&grant) {
            return Err("该话术库未授权给 AI 连接。".into());
        }
        available(&state, &grant)?;
        let write = !["list_categories", "search_phrases", "read_phrase"].contains(&name);
        let _activity = if write {
            Some(crate::update_activity::Activity::enter()?)
        } else {
            None
        };
        let result = if args.source == "private" {
            self.private(name, args, &cfg, &state)
        } else {
            self.shared(name, args, &cfg, &state).await
        };
        // 读取也要在生成结果后核验账号，不能把切号前读取的正文返回给旧会话。
        // 已提交写入保留提交回执，避免客户端误认为失败后重复创建。
        if !write && (self.0.cancel.is_cancelled() || account(&state)? != self.0.account) {
            return Err("AI 连接已失效，请重新连接。".into());
        }
        result
    }

    fn private(&self, name: &str, args: Args, cfg: &Config, state: &AppState) -> Result<Value> {
        if let Some(p) = &args.phrase {
            crate::checked_phrase_shortcut(state, &p.id, &p.shortcut)?;
        }
        let mut store = crate::private_commands::active_store(state, Some(&args.library_id))?;
        // 持有库锁后再核验账号；切号不能把旧请求写入新库。
        if state.teams.status().session_epoch != self.0.account.epoch {
            return Err("账号已变化。".into());
        }
        let library = store.library()?;
        let id = args.id.as_deref().unwrap_or("");
        let old = library.phrases.iter().find(|p| p.id == id);
        match name {
            "list_categories"=>return Ok(json!(library.categories.iter().filter(|c|c.kind=="phrase").map(|c|json!({"category":c,"revision":private_local::revision(&store,"category",&c.id).unwrap_or(0)})).collect::<Vec<_>>())),
            "search_phrases"=>{let query=crate::search_match::SearchQuery::new(args.query.as_deref().unwrap_or(""),library.settings.search_mode=="exact");let hits=store.search_matching(&query)?;let hits=hits.iter().filter_map(|h|h.phrase.as_ref()).map(|p|exposed(p,private_local::revision(&store,"phrase",&p.id).unwrap_or(0))).collect();return page(hits,args.offset,args.limit);}
            "read_phrase"=>{let p=old.ok_or("话术不存在。")?;return Ok(exposed(p,private_local::revision(&store,"phrase",id)?));}
            _=>{}
        }
        let mut batch = private_local::MaterialBatch::default();
        let result = state.teams.with_mcp_session(self.0.account.epoch, || {
            let result = match name {
                "save_category" => {
                    let category = args.category.ok_or("缺少分类。")?;
                    if category.kind != "phrase" {
                        return Err("只允许修改话术分类。".into());
                    }
                    revision(&store, "category", &category.id, args.expected_revision)?;
                    store.save_category(category)?
                }
                "save_phrase" => {
                    let input = args.phrase.ok_or("缺少话术。")?;
                    revision(&store, "phrase", &input.id, args.expected_revision)?;
                    let old = library.phrases.iter().find(|p| p.id == input.id);
                    let p = materialize(input, old, cfg, &store.dir, &mut batch)?;
                    crate::shortcut_conflict(
                        &state.native.settings.read().unwrap(),
                        std::slice::from_ref(&p),
                    )?;
                    store.save_phrase(p, None)?
                }
                "move_phrase" => {
                    revision(&store, "phrase", id, args.expected_revision)?;
                    let mut p = old.ok_or("话术不存在。")?.clone();
                    p.root_id = args.root_id.ok_or("缺少目标分类。")?;
                    p.group_id = args.group_id;
                    store.save_phrase(p, None)?
                }
                "sort_item" => {
                    let kind = args.kind.as_deref().ok_or("缺少类型。")?;
                    if !["phrase", "category"].contains(&kind) {
                        return Err("排序类型无效。".into());
                    }
                    if kind == "category"
                        && !library
                            .categories
                            .iter()
                            .any(|c| c.id == id && c.kind == "phrase")
                    {
                        return Err("话术分类不存在。".into());
                    }
                    revision(&store, kind, id, args.expected_revision)?;
                    store.sort(id, kind, args.position.ok_or("缺少位置。")?)?;
                    id.into()
                }
                "recycle_phrase" => {
                    revision(&store, "phrase", id, args.expected_revision)?;
                    old.ok_or("话术不存在。")?;
                    store.delete_item(id, "phrase", true)?;
                    id.into()
                }
                _ => return Err("不支持的操作。".into()),
            };
            Ok(result)
        })?;
        batch.keep();
        drop(store);
        // 已提交不能因通知失败报告为写入失败，否则客户端可能重复创建。
        let refresh = crate::changed(&self.0.app, state);
        state.private_sync.wake();
        Ok(json!({"id":result,"committed":true,"refreshRequired":refresh.is_err()}))
    }
    async fn shared(
        &self,
        name: &str,
        args: Args,
        cfg: &Config,
        state: &AppState,
    ) -> Result<Value> {
        let view = state.teams.view(&args.library_id)?;
        if state.teams.status().session_epoch != self.0.account.epoch {
            return Err("账号已变化。".into());
        }
        let id = args.id.as_deref().unwrap_or("");
        let old = view
            .phrases
            .iter()
            .find(|p| p.content_ref.id == id || p.phrase.id == id);
        let version =
            json!({"version":view.team.version,"lifecycleVersion":view.team.lifecycle_version});
        match name {
            "list_categories" => {
                return Ok(json!({"categories":view.categories,"version":version}))
            }
            "read_phrase" => {
                let p = old.ok_or("话术不存在。")?;
                return Ok(
                    json!({"phrase":exposed(&p.phrase,p.content_ref.revision),"version":version}),
                );
            }
            "search_phrases" => {
                let mode = state.native.settings.read().unwrap().search_mode.clone();
                let query = crate::search_match::SearchQuery::new(
                    args.query.as_deref().unwrap_or(""),
                    mode == "exact",
                );
                let hits = view
                    .phrases
                    .iter()
                    .filter(|p| query.matches(&format!("{} {}", p.phrase.title, p.phrase.text())))
                    .map(|p| exposed(&p.phrase, p.content_ref.revision))
                    .collect();
                return Ok(json!({"page":page(hits,args.offset,args.limit)?,"version":version}));
            }
            _ => {}
        }
        if !view.team.editable || view.offline {
            return Err("请联网并确认共享话术编辑权限。".into());
        }
        if args.expected_version != Some(view.team.version)
            || args.expected_lifecycle_version != Some(view.team.lifecycle_version)
        {
            return Err("共享话术版本已变化或未提供，请重新读取后核对。".into());
        }
        let mut batch = private_local::MaterialBatch::default();
        let phrase = if name == "save_phrase" {
            let input = args.phrase.ok_or("缺少话术。")?;
            let old = view.phrases.iter().find(|p| p.phrase.id == input.id);
            if !input.id.is_empty() && old.is_none() {
                return Err("话术引用无效，请重新读取。".into());
            }
            let old = old
                .map(|p| {
                    state
                        .teams
                        .mcp_existing_phrase(&p.phrase.id, self.0.account.epoch)
                })
                .transpose()?;
            let dir = state.device_store.lock().unwrap().dir.clone();
            Some(materialize(input, old.as_ref(), cfg, &dir, &mut batch)?)
        } else {
            None
        };
        state
            .teams
            .mcp_write(
                &self.0.app,
                crate::team::McpWrite {
                    epoch: self.0.account.epoch,
                    team: args.library_id,
                    version: view.team.version,
                    lifecycle: view.team.lifecycle_version,
                    operation: name.into(),
                    phrase,
                    category: args.category,
                    id: id.into(),
                    root_id: args.root_id,
                    group_id: args.group_id,
                    kind: args.kind,
                    position: args.position,
                },
            )
            .await
        // 共享上传已产生服务端副本；临时本地素材由 batch 自动清理。
    }
}
fn page(items: Vec<Value>, offset: Option<usize>, limit: Option<usize>) -> Result<Value> {
    let offset = offset.unwrap_or(0);
    let limit = limit.unwrap_or(50);
    if !(1..=100).contains(&limit) {
        return Err("每页数量应为1—100。".into());
    }
    let total = items.len();
    Ok(
        json!({"items":items.into_iter().skip(offset).take(limit).collect::<Vec<_>>(),"total":total,"nextOffset":if offset.saturating_add(limit)<total{Some(offset+limit)}else{None}}),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn default_closed_and_no_grants() {
        let c = Config::default();
        assert!(!c.enabled);
        assert!(c.grants.is_empty());
        assert_eq!(c.port, 17823);
    }
    #[test]
    fn bearer_and_origin_required_boundary() {
        let mut h = axum::http::HeaderMap::new();
        assert!(!authorized(&h, "secret"));
        h.insert("authorization", "Bearer secret".parse().unwrap());
        assert!(authorized(&h, "secret"));
        h.insert("origin", "http://localhost".parse().unwrap());
        assert!(!authorized(&h, "secret"));
    }
    #[test]
    fn import_paths_are_confined() {
        let root = tempfile::tempdir().unwrap();
        std::fs::write(root.path().join("a.txt"), "a").unwrap();
        let roots = vec![Root {
            id: "r".into(),
            path: model::external_path(&root.path().canonicalize().unwrap()),
        }];
        assert!(import_path(&roots, "r", "a.txt").is_ok());
        for p in ["../a.txt", "C:\\a.txt", "a.txt:stream", "."] {
            assert!(import_path(&roots, "r", p).is_err());
        }
        assert!(import_path(&roots, "other", "a.txt").is_err());
    }
    #[test]
    fn failed_import_cleans_only_new_copies_and_preserves_sources() {
        let source = tempfile::tempdir().unwrap();
        let destination = tempfile::tempdir().unwrap();
        std::fs::write(source.path().join("a.txt"), "原始文件").unwrap();
        let cfg = Config {
            roots: vec![Root {
                id: "r".into(),
                path: model::external_path(&source.path().canonicalize().unwrap()),
            }],
            ..Config::default()
        };
        let input = serde_json::from_value(json!({"rootId":"category","segments":[{"blocks":[{"kind":"file","rootId":"r","relativePath":"a.txt"},{"kind":"file","rootId":"r","relativePath":"missing.txt"}]}]})).unwrap();
        let mut batch = private_local::MaterialBatch::default();
        assert!(materialize(input, None, &cfg, destination.path(), &mut batch).is_err());
        drop(batch);
        assert_eq!(
            std::fs::read_to_string(source.path().join("a.txt")).unwrap(),
            "原始文件"
        );
        for entry in std::fs::read_dir(destination.path().join("assets")).unwrap() {
            assert_eq!(std::fs::read_dir(entry.unwrap().path()).unwrap().count(), 0);
        }
    }
    #[test]
    fn no_absolute_material_paths_accepted() {
        assert!(
            serde_json::from_value::<InputBlock>(json!({"kind":"file","path":"C:\\secret"}))
                .is_err()
        );
    }
    #[test]
    fn tools_do_not_expose_dangerous_actions() {
        let t = tools();
        assert_eq!(t.len(), 9);
        assert!(!t
            .iter()
            .any(|t| ["send", "delete", "clipboard", "account"].contains(&t.name.as_ref())));
    }
}
