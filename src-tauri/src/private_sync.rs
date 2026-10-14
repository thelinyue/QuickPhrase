//! 一个账号库一个串行同步循环；会话世代、库身份和恢复世代共同约束结果落盘。
//! 队列只保存尚未发送的键；发送前固化请求和本地修订，超时后先查结果，再同编号重试。
use crate::{
    model::*,
    private_local as local,
    store::{db_error, Store},
    team::AccountContext,
    AppState,
};
use reqwest::Method;
use rusqlite::{params, OptionalExtension};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use shanyu_team_model::private as wire;
use std::{
    collections::{HashMap, HashSet},
    path::{Path, PathBuf},
    sync::{atomic::Ordering, Arc, Mutex},
    time::Duration,
};
use tauri::{Emitter, Manager};
use tokio::sync::Notify;
type Values = HashMap<(wire::Kind, String), Option<Value>>;
type Keys = HashSet<(wire::Kind, String)>;

pub fn conflict_keys(store: &Store) -> Result<Keys> {
    let rows = store
        .conn
        .prepare("SELECT kind,id FROM sync_records WHERE conflict=1")
        .map_err(db_error)?
        .query_map([], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)))
        .map_err(db_error)?
        .collect::<std::result::Result<Vec<_>, _>>()
        .map_err(db_error)?;
    rows.into_iter()
        .map(|(kind, id)| {
            Ok((
                serde_json::from_value(Value::String(kind)).map_err(db_error)?,
                id,
            ))
        })
        .collect()
}
fn category_refs(kind: wire::Kind, id: &str, value: Option<&Value>) -> HashSet<String> {
    let mut refs = HashSet::new();
    if kind == wire::Kind::Category {
        refs.insert(id.into());
    }
    if let Some(v) = value {
        for key in ["parentId", "rootId", "groupId"] {
            if let Some(id) = v[key].as_str() {
                refs.insert(id.into());
            }
        }
        if kind == wire::Kind::Recycle {
            for (array, kind) in [
                ("categories", wire::Kind::Category),
                ("phrases", wire::Kind::Phrase),
            ] {
                if let Some(values) = v[array].as_array() {
                    for v in values {
                        refs.extend(category_refs(
                            kind,
                            v["id"].as_str().unwrap_or_default(),
                            Some(v),
                        ));
                    }
                }
            }
        }
    }
    refs
}
fn depends_on(
    kind: wire::Kind,
    id: &str,
    value: Option<&Value>,
    values: &Values,
    blocked: &HashSet<String>,
) -> bool {
    let mut refs = category_refs(kind, id, value);
    let initial: Vec<_> = refs.iter().cloned().collect();
    for id in initial {
        if let Some(Some(v)) = values.get(&(wire::Kind::Category, id)) {
            if let Some(parent) = v["parentId"].as_str() {
                refs.insert(parent.into());
            }
        }
    }
    refs.iter().any(|id| blocked.contains(id))
}
/// 删除记录已没有本机归属，仍按基线关联阻止跨越未解决的分类冲突；其他分类继续同步。
fn pending_changes(store: &Store) -> Result<(Vec<wire::Change>, HashMap<String, u64>)> {
    let values = local::local_records(store)?;
    let base = local::base_records(store)?;
    let previous: Values = base
        .iter()
        .map(|(k, r)| (k.clone(), r.value.clone()))
        .collect();
    let conflicts = conflict_keys(store)?;
    let blocked: HashSet<_> = conflicts
        .iter()
        .filter(|(kind, _)| *kind == wire::Kind::Category)
        .map(|(_, id)| id.clone())
        .collect();
    let mut changes = vec![];
    let mut revisions = HashMap::new();
    for ((kind, id), revision) in local::dirty(store)? {
        let k = (kind, id.clone());
        if conflicts.contains(&k)
            || depends_on(
                kind,
                &id,
                values.get(&k).and_then(Option::as_ref),
                &values,
                &blocked,
            )
            || depends_on(
                kind,
                &id,
                previous.get(&k).and_then(Option::as_ref),
                &previous,
                &blocked,
            )
        {
            continue;
        }
        changes.push(wire::Change {
            kind,
            id: id.clone(),
            expected_version: base.get(&k).map_or(0, |r| r.version),
            value: values.get(&k).cloned().flatten(),
        });
        revisions.insert(key(kind, &id), revision);
    }
    Ok((changes, revisions))
}
/// 两套合法分类结构合并后可能不合法。恢复原草稿及其直接依赖，远端版本仍保存在冲突表。
fn protect_dependencies(original: &Values, values: &mut Values, conflicts: &mut Keys) {
    loop {
        let mut invalid = vec![];
        for ((kind, id), v) in values.iter() {
            let Some(v) = v else { continue };
            let cat = |id: &str| {
                values
                    .get(&(wire::Kind::Category, id.into()))
                    .and_then(Option::as_ref)
            };
            let mut refs = vec![];
            if *kind == wire::Kind::Category {
                if let Some(parent) = v["parentId"].as_str() {
                    if cat(parent).is_none_or(|v| !v["parentId"].is_null()) {
                        refs.push(parent.to_owned());
                    }
                }
            } else if *kind == wire::Kind::Phrase {
                let root = v["rootId"].as_str().unwrap_or_default();
                if cat(root).is_none_or(|v| !v["parentId"].is_null()) {
                    refs.push(root.to_owned());
                }
                if let Some(group) = v["groupId"].as_str() {
                    if cat(group).is_none_or(|v| v["parentId"].as_str() != Some(root)) {
                        refs.push(group.to_owned());
                    }
                }
            }
            if !refs.is_empty() {
                invalid.push(((*kind, id.clone()), refs));
            }
        }
        if invalid.is_empty() {
            break;
        }
        let mut progress = false;
        for (k, refs) in invalid {
            for id in refs {
                let ck = (wire::Kind::Category, id);
                conflicts.insert(ck.clone());
                if let Some(Some(v)) = original.get(&ck) {
                    if values.get(&ck) != Some(&Some(v.clone())) {
                        values.insert(ck, Some(v.clone()));
                        progress = true;
                    }
                }
            }
            let old = original.get(&k).cloned().flatten();
            if values.get(&k) != Some(&old) {
                values.insert(k.clone(), old);
                conflicts.insert(k);
                progress = true;
            }
        }
        if !progress {
            break;
        }
    }
}

pub struct PrivateSync {
    pub dir: PathBuf,
    notify: Notify,
    error: Mutex<String>,
    downloads: Mutex<HashSet<String>>,
    preview: Mutex<Option<Prepared>>,
    running: Mutex<Option<String>>,
}
struct Prepared {
    view: Preview,
    ctx: AccountContext,
    profile: local::Profile,
    remote: wire::Snapshot,
    source: Values,
    source_clock: i64,
    source_dir: PathBuf,
    _materials: tempfile::TempDir,
}
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub library_id: String,
    pub username: String,
    pub enabled: bool,
    pub state: String,
    pub pending: usize,
    pub conflicts: usize,
    pub settings_pending: usize,
    pub message: String,
    pub same_account: bool,
}
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Choice {
    pub key: String,
    pub label: String,
    pub local: String,
    pub server: String,
    pub setting: bool,
}
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Preview {
    pub id: String,
    pub categories: usize,
    pub phrases: usize,
    pub common_phrases: usize,
    pub conflicts: Vec<Choice>,
    pub resume: bool,
    pub restore: bool,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Merge {
    pub preview_id: String,
    pub choices: HashMap<String, String>,
}
fn clock(store: &Store) -> Result<i64> {
    store
        .conn
        .query_row("SELECT value FROM sync_clock WHERE id=1", [], |r| r.get(0))
        .map_err(db_error)
}
fn key(kind: wire::Kind, id: &str) -> String {
    format!("{}:{id}", kind.as_str())
}
pub fn record_label(value: &Option<Value>) -> String {
    value
        .as_ref()
        .and_then(|v| {
            ["name", "title", "label", "text"]
                .iter()
                .find_map(|key| v[*key].as_str().filter(|s| !s.is_empty()))
        })
        .unwrap_or("个人内容")
        .into()
}
/// 只供确认界面显示，不能写入日志。同标题的话术也必须展示正文和素材，供用户判断取舍。
pub fn describe(kind: wire::Kind, value: &Option<Value>) -> String {
    let Some(v) = value else {
        return "已删除".into();
    };
    match kind {
        wire::Kind::Phrase => {
            let mut lines = vec![];
            if let Some(title) = v["title"].as_str().filter(|s| !s.is_empty()) {
                lines.push(format!("标题：{title}"));
            }
            if let Some(segments) = v["segments"].as_array() {
                for (index, segment) in segments.iter().enumerate() {
                    if segments.len() > 1 {
                        lines.push(format!("第{}段", index + 1));
                    }
                    if let Some(blocks) = segment["blocks"].as_array() {
                        for block in blocks {
                            let text = match block["kind"].as_str() {
                                Some("text") => block["text"].as_str().unwrap_or_default().into(),
                                Some("image") => {
                                    format!("[图片：{}]", block["name"].as_str().unwrap_or("图片"))
                                }
                                Some("file") => {
                                    format!("[附件：{}]", block["name"].as_str().unwrap_or("附件"))
                                }
                                _ => continue,
                            };
                            lines.push(text);
                        }
                    }
                }
            }
            if let Some(shortcut) = v["shortcut"].as_str().filter(|s| !s.is_empty()) {
                lines.push(format!("快捷键：{shortcut}"));
            }
            lines.join("\n")
        }
        wire::Kind::Recycle => {
            let mut lines = vec![record_label(value)];
            if let Some(categories) = v["categories"].as_array() {
                lines.push(format!("{} 个分类", categories.len()));
            }
            if let Some(phrases) = v["phrases"].as_array() {
                for phrase in phrases {
                    lines.push(describe(wire::Kind::Phrase, &Some(phrase.clone())));
                }
            }
            lines.join("\n")
        }
        wire::Kind::Category => format!(
            "{} · {} · 排序{}",
            record_label(value),
            if v["parentId"].is_null() {
                "一级分类"
            } else {
                "二级分类"
            },
            v["position"].as_u64().unwrap_or(0) + 1
        ),
        wire::Kind::Common => v["text"].as_str().unwrap_or_default().into(),
        wire::Kind::Setting => match v {
            Value::Bool(true) => "开".into(),
            Value::Bool(false) => "关".into(),
            Value::String(s) => s.clone(),
            Value::Array(items) => items
                .iter()
                .map(|item| item.as_str().unwrap_or_default())
                .collect::<Vec<_>>()
                .join("，"),
            _ => v.to_string(),
        },
    }
}
fn walk(
    value: &mut Value,
    work: &mut impl FnMut(&mut serde_json::Map<String, Value>) -> Result<()>,
) -> Result<()> {
    match value {
        Value::Object(map) => {
            if matches!(
                map.get("kind").and_then(Value::as_str),
                Some("image" | "file")
            ) {
                work(map)?;
            } else {
                for v in map.values_mut() {
                    walk(v, work)?;
                }
            }
        }
        Value::Array(values) => {
            for v in values {
                walk(v, work)?;
            }
        }
        _ => {}
    }
    Ok(())
}
pub fn asset_path(store: &Store, id: &str, kind: &str, name: &str) -> Result<PathBuf> {
    if uuid::Uuid::parse_str(id).is_err() || !["image", "file"].contains(&kind) {
        return Err("素材身份或类型无效。".into());
    }
    let path = if kind == "image" {
        store.dir.join("images").join(format!("{id}.png"))
    } else {
        let name: String = name
            .chars()
            .map(|c| {
                if c.is_control() || "\\/:*?\"<>|".contains(c) {
                    '_'
                } else {
                    c
                }
            })
            .collect();
        let name = name.trim_end_matches(['.', ' ']);
        let name = if name.is_empty() { "附件" } else { name };
        store.dir.join("assets").join(id).join(name)
    };
    std::fs::create_dir_all(path.parent().unwrap()).map_err(db_error)?;
    Ok(path)
}
pub fn localize(
    store: &Store,
    value: &Option<Value>,
    assets: &[wire::Asset],
) -> Result<Option<Value>> {
    let mut value = value.clone();
    if let Some(v) = value.as_mut() {
        walk(v, &mut |b| {
            let Some(id) = b
                .remove("assetId")
                .and_then(|v| v.as_str().map(str::to_owned))
            else {
                return Ok(());
            };
            let asset = assets
                .iter()
                .find(|a| a.id == id)
                .ok_or("服务器素材记录缺失。")?;
            let existing: Option<(String, u64)> = store
                .conn
                .query_row(
                    "SELECT path,size FROM sync_assets WHERE id=?1",
                    [&id],
                    |r| Ok((r.get(0)?, r.get::<_, i64>(1)? as u64)),
                )
                .optional()
                .map_err(db_error)?;
            let path = existing
                .as_ref()
                .map(|(p, _)| PathBuf::from(p))
                .unwrap_or(asset_path(store, &id, &asset.kind, &asset.name)?);
            let path = external_path(&path);
            let size = existing
                .as_ref()
                .filter(|(_, size)| std::fs::metadata(&path).is_ok_and(|m| m.len() == *size))
                .map_or(asset.size, |(_, size)| *size);
            store.conn.execute("INSERT INTO sync_assets VALUES(?1,?2,?3,?4,?5) ON CONFLICT(id) DO UPDATE SET size=excluded.size",params![path,id,asset.kind,asset.name,size as i64]).map_err(db_error)?;
            b.insert("path".into(), Value::String(path));
            Ok(())
        })?;
    }
    Ok(value)
}
pub fn binding_library(store: &Store, settings: Settings) -> Result<Library> {
    let mut library = store.library()?;
    library.settings = settings;
    for p in &mut library.phrases {
        if !p.shortcut.is_empty() {
            let conflict = crate::native::parse_shortcut(&p.shortcut)
                .map_or(true, |s| library.settings.shortcut_reserved(&s));
            if conflict {
                p.shortcut.clear();
            }
        }
        p.id = local::content_key(store, "phrase", &p.id)?;
    }
    Ok(library)
}
impl PrivateSync {
    pub fn new(dir: &Path) -> Arc<Self> {
        Arc::new(Self {
            dir: dir.into(),
            notify: Notify::new(),
            error: Mutex::new(String::new()),
            downloads: Mutex::new(HashSet::new()),
            preview: Mutex::new(None),
            running: Mutex::new(None),
        })
    }
    pub fn cancel_preview(&self) {
        self.preview.lock().unwrap().take();
    }
    pub fn wake(&self) {
        self.notify.notify_one();
    }
    fn emit(&self, app: &tauri::AppHandle) {
        let _ = app.emit("private-sync-changed", ());
    }
    pub fn status(&self, state: &AppState) -> Result<Status> {
        let store = state.store.lock().unwrap();
        let owner = local::meta(&store, "owner")?;
        let server = local::meta(&store, "server")?;
        let current = state.teams.status();
        let same_account =
            current.user.as_ref().is_some_and(|u| u.id == owner) && current.server == server;
        let pending = store
            .conn
            .query_row("SELECT count(*) FROM sync_dirty", [], |r| {
                r.get::<_, i64>(0)
            })
            .map_err(db_error)? as usize;
        let conflicts = store
            .conn
            .query_row(
                "SELECT count(*) FROM sync_records WHERE conflict=1",
                [],
                |r| r.get::<_, i64>(0),
            )
            .map_err(db_error)? as usize;
        let settings = serde_json::to_value(store.settings()?).map_err(db_error)?;
        let settings_pending = local::local_records(&store)?
            .iter()
            .filter(|((kind, id), v)| {
                *kind == wire::Kind::Setting && v.as_ref().is_some_and(|v| settings[id] != *v)
            })
            .count();
        let enabled = local::meta(&store, "enabled")? == "1";
        let state_label = if conflicts > 0 || local::meta(&store, "reconcile")? == "1" {
            "有冲突"
        } else if !same_account || current.offline {
            "离线"
        } else if self.running.lock().unwrap().as_ref() == Some(&local::identity(&store)?)
            && enabled
        {
            "同步中"
        } else if pending > 0 {
            "待同步"
        } else {
            "已同步"
        };
        Ok(Status {
            library_id: local::identity(&store)?,
            username: local::meta(&store, "username")?,
            enabled,
            state: state_label.into(),
            pending,
            conflicts,
            settings_pending,
            message: self.error.lock().unwrap().clone(),
            same_account,
        })
    }
    pub fn start(self: &Arc<Self>, app: tauri::AppHandle) {
        let this = self.clone();
        tauri::async_runtime::spawn(async move {
            loop {
                tokio::select! {_=this.notify.notified()=>{},_=tokio::time::sleep(Duration::from_secs(60))=>{}}
                let library_id = local::identity(&app.state::<AppState>().store.lock().unwrap())
                    .unwrap_or_default();
                *this.running.lock().unwrap() = Some(library_id.clone());
                let account = app.state::<AppState>().teams.account_context().ok();
                this.emit(&app);
                let result = this.run(&app).await;
                *this.running.lock().unwrap() = None;
                if local::identity(&app.state::<AppState>().store.lock().unwrap())
                    .ok()
                    .as_ref()
                    == Some(&library_id)
                    && account.as_ref().is_none_or(|ctx| {
                        app.state::<AppState>()
                            .teams
                            .with_account(ctx, || Ok(()))
                            .is_ok()
                    })
                {
                    if let Err(error) = result {
                        *this.error.lock().unwrap() = error;
                    } else {
                        this.error.lock().unwrap().clear();
                    }
                }
                this.emit(&app);
            }
        });
        self.wake();
    }
    pub fn startup(&self, device: &Store) -> Result<Store> {
        local::registry(device)?;
        let profile=device.conn.query_row("SELECT p.library_id,p.server,p.user_id,p.username FROM private_active a JOIN private_profiles p ON p.library_id=a.library_id WHERE a.id=1",[],|r|Ok(local::Profile{library_id:r.get(0)?,server:r.get(1)?,user_id:r.get(2)?,username:r.get(3)?})).optional().map_err(db_error)?;
        match profile {
            Some(p) => local::open_profile(&self.dir, &p),
            None => Store::open(&self.dir),
        }
    }
    pub fn activate(&self, app: &tauri::AppHandle, profile: Option<&local::Profile>) -> Result<()> {
        let state = app.state::<AppState>();
        let mut store = state.store.lock().unwrap();
        let next = match profile {
            Some(p) => local::open_profile(&self.dir, p)?,
            None => Store::open(&self.dir)?,
        };
        if local::identity(&store)? == local::identity(&next)? {
            return Ok(());
        }
        state.delivery.cancelled.store(true, Ordering::Release);
        state.native.cancel_candidates();
        *state.native.sequence_map.write().unwrap() = (vec![], true);
        let device = state.device_store.lock().unwrap();
        let next_id = profile.map(|p| p.library_id.as_str()).unwrap_or("");
        device.conn.execute("INSERT INTO private_active VALUES(1,?1) ON CONFLICT(id) DO UPDATE SET library_id=excluded.library_id",[next_id]).map_err(db_error)?;
        drop(device);
        *store = next;
        self.error.lock().unwrap().clear();
        app.asset_protocol_scope()
            .allow_directory(store.dir.join("images"), true)
            .map_err(db_error)?;
        app.asset_protocol_scope()
            .allow_directory(store.dir.join("assets"), true)
            .map_err(db_error)?;
        drop(store);
        // 与远端刷新共用设置应用边界；快捷键被占用时保留本机配置，账号库仍然切换并同步正文。
        crate::refreshed(app, &state)?;
        self.wake();
        Ok(())
    }
    pub fn account_changed(&self, app: &tauri::AppHandle) -> Result<()> {
        let state = app.state::<AppState>();
        let status = state.teams.status();
        let Some(user) = status.user else {
            self.wake();
            return Ok(());
        };
        let profile = {
            let device = state.device_store.lock().unwrap();
            device
                .conn
                .execute(
                    "UPDATE private_profiles SET username=?3 WHERE server=?1 AND user_id=?2",
                    params![status.server, user.id, user.username],
                )
                .map_err(db_error)?;
            local::find_profile(&device, &status.server, &user.id)?
        };
        if let Some(profile) = profile {
            let target = local::open_profile(&self.dir, &profile)?;
            if local::meta(&target, "initialized")? == "1" {
                self.activate(app, Some(&profile))?;
                return Ok(());
            }
        }
        let is_other = {
            let store = state.store.lock().unwrap();
            local::bound(&store)
                && (local::meta(&store, "owner")? != user.id
                    || local::meta(&store, "server")? != status.server)
        };
        if is_other {
            self.activate(app, None)?;
        }
        self.wake();
        Ok(())
    }
    pub async fn preview(&self, app: &tauri::AppHandle) -> Result<Preview> {
        let state = app.state::<AppState>();
        let ctx = state.teams.account_context()?;
        let remote: wire::Snapshot = state
            .teams
            .account_request(&ctx, Method::GET, "snapshot", None)
            .await?;
        let profile = {
            let device = state.device_store.lock().unwrap();
            match local::find_profile(&device, &ctx.server, &ctx.user_id)? {
                Some(p) => p,
                None => {
                    let p = local::Profile {
                        library_id: id(),
                        server: ctx.server.clone(),
                        user_id: ctx.user_id.clone(),
                        username: ctx.username.clone(),
                    };
                    device
                        .conn
                        .execute(
                            "INSERT INTO private_profiles VALUES(?1,?2,?3,?4)",
                            params![p.server, p.user_id, p.library_id, p.username],
                        )
                        .map_err(db_error)?;
                    p
                }
            }
        };
        let target = local::open_profile(&self.dir, &profile)?;
        let initialized = local::meta(&target, "initialized")? == "1";
        let restore = initialized && local::meta(&target, "generation")? != remote.generation;
        let source_store = if initialized {
            target
        } else {
            Store::open(&self.dir)?
        };
        let mut source = local::local_records(&source_store)?;
        let frozen_materials = if initialized && !restore {
            tempfile::tempdir().map_err(db_error)?
        } else {
            freeze_materials(&mut source, initialized)?
        };
        let source_clock = clock(&source_store)?;
        let conflicts = if initialized && !restore {
            vec![]
        } else {
            remote
                .records
                .iter()
                .filter_map(|r| {
                    source.get(&(r.kind, r.id.clone())).map(|v| Choice {
                        key: key(r.kind, &r.id),
                        label: if r.kind == wire::Kind::Setting {
                            format!("设置：{}", r.id)
                        } else {
                            record_label(v)
                        },
                        local: describe(r.kind, v),
                        server: describe(r.kind, &r.value),
                        setting: r.kind == wire::Kind::Setting,
                    })
                })
                .collect()
        };
        let view = Preview {
            id: id(),
            categories: source
                .keys()
                .filter(|(k, _)| *k == wire::Kind::Category)
                .count(),
            phrases: source
                .keys()
                .filter(|(k, _)| *k == wire::Kind::Phrase)
                .count(),
            common_phrases: source
                .keys()
                .filter(|(k, _)| *k == wire::Kind::Common)
                .count(),
            conflicts,
            resume: initialized && !restore,
            restore,
        };
        *self.preview.lock().unwrap() = Some(Prepared {
            view: view.clone(),
            ctx,
            profile,
            remote,
            source,
            source_clock,
            source_dir: source_store.dir,
            _materials: frozen_materials,
        });
        Ok(view)
    }
    pub fn confirm(&self, app: &tauri::AppHandle, request: Merge) -> Result<()> {
        let prepared = self
            .preview
            .lock()
            .unwrap()
            .take()
            .ok_or("合并预览已失效，请重新打开。")?;
        if prepared.view.id != request.preview_id {
            return Err("合并预览已失效。".into());
        }
        let state = app.state::<AppState>();
        state.teams.with_account(&prepared.ctx, || {
            let source = Store::open(&prepared.source_dir)?;
            if clock(&source)? != prepared.source_clock {
                return Err("本机内容已变化，请重新预览。".into());
            }
            let target = local::open_profile(&self.dir, &prepared.profile)?;
            if prepared.view.resume {
                local::set_meta(&target, "enabled", "1")?;
                return Ok(());
            }
            merge_confirm(
                &target,
                &prepared.remote,
                &prepared.source,
                &request.choices,
            )
        })?;
        self.activate(app, Some(&prepared.profile))?;
        self.wake();
        self.emit(app);
        Ok(())
    }
    async fn run(&self, app: &tauri::AppHandle) -> Result<()> {
        let state = app.state::<AppState>();
        let ctx = match state.teams.account_context() {
            Ok(c) => c,
            Err(_) => return Ok(()),
        };
        let (library_id, generation, inflight) = {
            let store = state.store.lock().unwrap();
            if local::meta(&store, "enabled")? != "1"
                || local::meta(&store, "owner")? != ctx.user_id
                || local::meta(&store, "server")? != ctx.server
                || local::meta(&store, "reconcile")? == "1"
            {
                return Ok(());
            }
            let pending: Option<String> = store
                .conn
                .query_row("SELECT body FROM sync_inflight WHERE id=1", [], |r| {
                    r.get(0)
                })
                .optional()
                .map_err(db_error)?;
            (
                local::identity(&store)?,
                local::meta(&store, "generation")?,
                pending,
            )
        };
        // 恢复后的服务器必须先核对世代，不能先回放恢复前的固化队列。
        let remote: wire::Snapshot = state
            .teams
            .account_request(&ctx, Method::GET, "snapshot", None)
            .await?;
        {
            let store = state.store.lock().unwrap();
            state.teams.with_account(&ctx, || {
                local::scope(&store, Some(&library_id))?;
                if local::meta(&store, "enabled")? != "1" {
                    return Err("同步已暂停。".into());
                }
                if remote.generation != generation {
                    local::set_meta(&store, "reconcile", "1")?;
                    return Err("服务器数据已恢复，请重新核对个人库。".into());
                }
                Ok(())
            })?;
        }
        if let Some(body) = inflight {
            let request: wire::Mutation = serde_json::from_str(&body).map_err(db_error)?;
            let result: Option<wire::Commit> = state
                .teams
                .account_request(
                    &ctx,
                    Method::GET,
                    &format!("requests/{}", request.request_id),
                    None,
                )
                .await?;
            if let Some(result) = result {
                self.ack(app, &ctx, &library_id, &result)?;
            } else {
                self.commit(app, &ctx, &library_id, &request).await?;
            }
        }
        {
            let store = state.store.lock().unwrap();
            state.teams.with_account(&ctx, || {
                local::scope(&store, Some(&library_id))?;
                if local::meta(&store, "enabled")? != "1" {
                    return Err("同步已暂停，旧结果已保留待核对。".into());
                }
                if remote.generation != generation {
                    local::set_meta(&store, "reconcile", "1")?;
                    return Err("服务器数据已恢复，请重新核对个人库。".into());
                }
                self.apply(&store, &remote)
            })?;
        }
        crate::refreshed(app, &state)?;
        let (mut changes, revs) = {
            let store = state.store.lock().unwrap();
            local::scope(&store, Some(&library_id))?;
            if local::meta(&store, "enabled")? != "1" {
                return Ok(());
            }
            pending_changes(&store)?
        };
        if changes.is_empty() {
            return Ok(());
        }
        let mut new_assets = vec![];
        let mut uploads = vec![];
        let mut unavailable = HashSet::new();
        let mut blocked_categories = HashSet::new();
        for change in &mut changes {
            let related = category_refs(change.kind, &change.id, change.value.as_ref());
            if let Some(value) = change.value.as_mut() {
                let store = state.store.lock().unwrap();
                local::scope(&store, Some(&library_id))?;
                let result = walk(value, &mut |b| {
                    let Some(path) = b.remove("path").and_then(|v| v.as_str().map(str::to_owned))
                    else {
                        return Ok(());
                    };
                    let kind = b["kind"].as_str().ok_or("素材类型无效。")?.to_owned();
                    let name = b["name"].as_str().ok_or("素材名称无效。")?.to_owned();
                    let existing: Option<String> = store
                        .conn
                        .query_row("SELECT id FROM sync_assets WHERE path=?1", [&path], |r| {
                            r.get(0)
                        })
                        .optional()
                        .map_err(db_error)?;
                    let asset_id = existing.unwrap_or_else(id);
                    b.insert("assetId".into(), Value::String(asset_id.clone()));
                    if remote.assets.iter().any(|a| a.id == asset_id) {
                        return Ok(());
                    };
                    let size = std::fs::metadata(&path)
                        .map_err(|_| "素材缺失，相关内容保留在本机。".to_owned())?
                        .len();
                    if size == 0 || size > 128 * 1024 * 1024 {
                        return Err("素材不能为空或超过128MB。".into());
                    }
                    store
                        .conn
                        .execute(
                            "INSERT OR IGNORE INTO sync_assets VALUES(?1,?2,?3,?4,?5)",
                            params![path, asset_id, kind, name, size as i64],
                        )
                        .map_err(db_error)?;
                    uploads.push((key(change.kind, &change.id), asset_id, kind, name, path));
                    Ok(())
                });
                if result.is_err() {
                    unavailable.insert(key(change.kind, &change.id));
                    blocked_categories.extend(related);
                }
            }
        }
        changes.retain(|c| {
            !unavailable.contains(&key(c.kind, &c.id))
                && !category_refs(c.kind, &c.id, c.value.as_ref())
                    .iter()
                    .any(|id| blocked_categories.contains(id))
        });
        let eligible: HashSet<_> = changes.iter().map(|c| key(c.kind, &c.id)).collect();
        if changes.is_empty() {
            return Err("素材缺失，请补齐后继续同步。".into());
        }
        for (record_key, asset_id, kind, name, path) in uploads {
            if !eligible.contains(&record_key) || new_assets.contains(&asset_id) {
                continue;
            }
            {
                let store = state.store.lock().unwrap();
                local::scope(&store, Some(&library_id))?;
                if local::meta(&store, "enabled")? != "1" {
                    return Err("同步已暂停。".into());
                }
            }
            match state
                .teams
                .account_upload(&ctx, &asset_id, &kind, &name, Path::new(&path))
                .await
            {
                Ok(_) => new_assets.push(asset_id),
                Err(error) => {
                    new_assets.push(asset_id);
                    let _ = state
                        .teams
                        .account_request::<()>(
                            &ctx,
                            Method::POST,
                            "assets/cleanup",
                            Some(serde_json::to_value(&new_assets).map_err(db_error)?),
                        )
                        .await;
                    return Err(error);
                }
            }
        }
        let revs: HashMap<_, _> = revs
            .into_iter()
            .filter(|(k, _)| eligible.contains(k))
            .collect();
        let request = wire::Mutation {
            generation,
            request_id: id(),
            changes,
            new_assets,
        };
        {
            let store = state.store.lock().unwrap();
            state.teams.with_account(&ctx, || {
                local::scope(&store, Some(&library_id))?;
                if local::meta(&store, "enabled")? != "1" {
                    return Err("同步已暂停。".into());
                }
                store
                    .conn
                    .execute(
                        "INSERT INTO sync_inflight VALUES(1,?1,?2)",
                        params![
                            serde_json::to_string(&request).map_err(db_error)?,
                            serde_json::to_string(&revs).map_err(db_error)?
                        ],
                    )
                    .map_err(db_error)?;
                Ok(())
            })?;
        }
        self.emit(app);
        self.commit(app, &ctx, &library_id, &request).await?;
        if !unavailable.is_empty() {
            return Err("部分素材缺失；其他内容已同步。".into());
        }
        self.wake();
        Ok(())
    }
    async fn commit(
        &self,
        app: &tauri::AppHandle,
        ctx: &AccountContext,
        library_id: &str,
        request: &wire::Mutation,
    ) -> Result<()> {
        let state = app.state::<AppState>();
        {
            let store = state.store.lock().unwrap();
            local::scope(&store, Some(library_id))?;
            if local::meta(&store, "enabled")? != "1" {
                return Err("同步已暂停。".into());
            }
        }
        match state.teams.account_commit(ctx, request).await? {
            crate::team::PrivateCommit::Applied(result) => self.ack(app, ctx, library_id, &result),
            crate::team::PrivateCommit::Rejected(error) => {
                let store = state.store.lock().unwrap();
                state.teams.with_account(ctx, || {
                    local::scope(&store,Some(library_id))?;
                    store.conn.execute("DELETE FROM sync_inflight WHERE id=1 AND json_extract(body,'$.requestId')=?1",[&request.request_id]).map_err(db_error)?;
                    Ok(())
                })?;
                Err(error)
            }
        }
    }
    fn apply(&self, store: &Store, snapshot: &wire::Snapshot) -> Result<()> {
        let seen = local::meta(store, "remoteVersion")?
            .parse::<u64>()
            .unwrap_or(0);
        if snapshot.version <= seen {
            return Ok(());
        }
        let base = local::base_records(store)?;
        let dirty = local::dirty(store)?;
        let original = local::local_records(store)?;
        let mut values = original.clone();
        let mut changed = HashMap::new();
        let mut conflicts = conflict_keys(store)?;
        for record in &snapshot.records {
            let k = (record.kind, record.id.clone());
            if base.get(&k).is_some_and(|r| r.version >= record.version) {
                continue;
            }
            if dirty.contains_key(&k) || conflicts.contains(&k) {
                conflicts.insert(k.clone());
            } else {
                values.insert(k.clone(), localize(store, &record.value, &snapshot.assets)?);
            }
            changed.insert(k, record.clone());
        }
        // 删除分类或移动二级分类不能级联销毁另一设备尚未上传的草稿。
        // 保留原来的分类上下文，并把发生结构冲突的记录交给用户核对。
        protect_dependencies(&original, &mut values, &mut conflicts);
        let applied = store.settings()?;
        let mut candidate = serde_json::to_value(&applied).map_err(db_error)?;
        for ((kind, id), value) in &values {
            if *kind == wire::Kind::Setting {
                if let Some(v) = value {
                    candidate[id] = v.clone();
                }
            }
        }
        if serde_json::from_value::<Settings>(candidate.clone())
            .map_err(db_error)?
            .validate()
            .is_err()
        {
            let current = serde_json::to_value(applied).map_err(db_error)?;
            for name in wire::SETTINGS {
                if candidate[*name] != current[*name] {
                    let k = (wire::Kind::Setting, (*name).to_owned());
                    conflicts.insert(k.clone());
                    values.insert(k.clone(), original.get(&k).cloned().flatten());
                }
            }
        }
        for k in &conflicts {
            if !changed.contains_key(k) {
                if let Some(record) = base.get(k) {
                    changed.insert(k.clone(), record.clone());
                }
            }
        }
        let settings = store.settings()?;
        let tx = store.conn.unchecked_transaction().map_err(db_error)?;
        local::project_in(&tx, settings, &values, false, false)?;
        for (k, record) in changed {
            let conflict = conflicts.contains(&k);
            local::put_base(&tx, &record, conflict)?;
            if !conflict {
                tx.execute("UPDATE sync_clock SET value=value+1 WHERE id=1", [])
                    .map_err(db_error)?;
                tx.execute("INSERT INTO sync_revisions VALUES(?1,?2,(SELECT value FROM sync_clock WHERE id=1)) ON CONFLICT(kind,id) DO UPDATE SET revision=excluded.revision",params![record.kind.as_str(),record.id]).map_err(db_error)?;
                if record.kind == wire::Kind::Setting {
                    if let Some(value) = &record.value {
                        tx.execute("INSERT INTO sync_desired_settings VALUES(?1,?2) ON CONFLICT(id) DO UPDATE SET value=excluded.value",params![record.id,value.to_string()]).map_err(db_error)?;
                    }
                }
            }
        }
        tx.execute("INSERT INTO sync_meta VALUES('remoteVersion',?1) ON CONFLICT(key) DO UPDATE SET value=excluded.value",[snapshot.version.to_string()]).map_err(db_error)?;
        tx.commit().map_err(db_error)
    }
    fn ack(
        &self,
        app: &tauri::AppHandle,
        ctx: &AccountContext,
        library_id: &str,
        result: &wire::Commit,
    ) -> Result<()> {
        let state = app.state::<AppState>();
        let store = state.store.lock().unwrap();
        state.teams.with_account(ctx, || {
            local::scope(&store, Some(library_id))?;
            if local::meta(&store, "enabled")? != "1" {
                return Err("同步已暂停，请继续同步后核对结果。".into());
            }
            if local::meta(&store, "generation")? != result.generation {
                return Err("个人库恢复世代已变化。".into());
            }
            acknowledge(&store, result)
        })?;
        Ok(())
    }
    pub fn download(
        self: &Arc<Self>,
        app: tauri::AppHandle,
        ids: Vec<String>,
        attachments: bool,
    ) -> Result<()> {
        let state = app.state::<AppState>();
        let ctx = state.teams.account_context()?;
        let (library_id, generation, jobs) = {
            let store = state.store.lock().unwrap();
            if local::meta(&store, "owner")? != ctx.user_id
                || local::meta(&store, "server")? != ctx.server
            {
                return Ok(());
            }
            let mut jobs = vec![];
            let mut values = local::local_records(&store)?;
            for id in ids {
                let Some(value) = values.get_mut(&(wire::Kind::Phrase, id)) else {
                    continue;
                };
                if let Some(value) = value {
                    walk(value, &mut |b| {
                        let path = b
                            .get("path")
                            .and_then(Value::as_str)
                            .ok_or("素材路径缺失。")?;
                        if b["kind"] == "file" && !attachments {
                            return Ok(());
                        }
                        let asset = store
                            .conn
                            .query_row(
                                "SELECT id,size,kind FROM sync_assets WHERE path=?1",
                                [path],
                                |r| {
                                    Ok((
                                        r.get::<_, String>(0)?,
                                        r.get::<_, i64>(1)? as u64,
                                        r.get::<_, String>(2)?,
                                    ))
                                },
                            )
                            .optional()
                            .map_err(db_error)?;
                        if let Some((id, size, kind)) = asset {
                            if !std::fs::metadata(path)
                                .is_ok_and(|m| m.is_file() && m.len() == size)
                            {
                                jobs.push((id, PathBuf::from(path), size, kind));
                            }
                        }
                        Ok(())
                    })?;
                }
            }
            (
                local::identity(&store)?,
                local::meta(&store, "generation")?,
                jobs,
            )
        };
        let jobs: Vec<_> = jobs
            .into_iter()
            .filter_map(|(id, target, size, kind)| {
                let download_key = format!("{library_id}:{generation}:{id}");
                self.downloads
                    .lock()
                    .unwrap()
                    .insert(download_key.clone())
                    .then_some((id, target, size, kind, download_key))
            })
            .collect();
        if jobs.is_empty() {
            return Ok(());
        }
        let this = self.clone();
        tauri::async_runtime::spawn(async move {
            use futures_util::StreamExt;
            futures_util::stream::iter(jobs).for_each_concurrent(2, |(id,target,size,kind,download_key)| {
                let this=this.clone();let app=app.clone();let ctx=ctx.clone();let library_id=library_id.clone();let generation=generation.clone();
                async move {
                let state = app.state::<AppState>();
                let result=async{
                let temp=state.teams.account_download(&ctx,&id,&target,size,kind=="image").await?;
                let store=state.store.lock().unwrap();state.teams.with_account(&ctx,||{local::scope(&store,Some(&library_id))?;if local::meta(&store,"generation")?!=generation{return Err("下载所属数据已恢复。".into());}
                    let token=serde_json::to_string(&external_path(&target)).map_err(db_error)?;
                    let referenced:bool=store.conn.query_row("SELECT EXISTS(SELECT 1 FROM blocks WHERE json_extract(content,'$.path')=?1) OR EXISTS(SELECT 1 FROM recycle_bin WHERE instr(content,?2)>0)",params![external_path(&target),token],|r|r.get(0)).map_err(db_error)?;if !referenced{return Err("内容已删除，旧下载已丢弃。".into());}
                    if !std::fs::metadata(&target).is_ok_and(|m|m.len()==size){temp.persist(&target).map_err(|_|"缓存无法保存。".to_owned())?;}Ok(())})
            }.await;
                if let Err(e) = result {
                    let store = state.store.lock().unwrap();
                    if local::scope(&store, Some(&library_id)).is_ok()
                        && state.teams.with_account(&ctx, || Ok(())).is_ok()
                    {
                        *this.error.lock().unwrap() = e;
                    }
                }
                this.downloads.lock().unwrap().remove(&download_key);
                let _ = app.emit("library-changed", ());
                this.emit(&app);
                }
            }).await;
        });
        Ok(())
    }
}
fn acknowledge(store: &Store, result: &wire::Commit) -> Result<()> {
    let pending: Option<(String, String)> = store
        .conn
        .query_row(
            "SELECT body,revisions FROM sync_inflight WHERE id=1",
            [],
            |r| Ok((r.get(0)?, r.get(1)?)),
        )
        .optional()
        .map_err(db_error)?;
    let Some((body, revision_text)) = pending else {
        return Err("同步请求已变化，旧结果已丢弃。".into());
    };
    let request: wire::Mutation = serde_json::from_str(&body).map_err(db_error)?;
    if request.request_id != result.request_id || request.generation != result.generation {
        return Err("同步请求已变化，旧结果已丢弃。".into());
    }
    let revisions: HashMap<String, u64> = serde_json::from_str(&revision_text).map_err(db_error)?;
    let bases = local::base_records(store)?;
    let tx = store.conn.unchecked_transaction().map_err(db_error)?;
    for record in &result.records {
        if bases
            .get(&(record.kind, record.id.clone()))
            .is_none_or(|r| r.version <= record.version)
        {
            local::put_base(&tx, record, false)?;
        }
        if let Some(revision) = revisions.get(&key(record.kind, &record.id)) {
            tx.execute(
                "DELETE FROM sync_dirty WHERE kind=?1 AND id=?2 AND revision=?3",
                params![record.kind.as_str(), record.id, *revision as i64],
            )
            .map_err(db_error)?;
        }
    }
    for record in &result.conflicts {
        if bases
            .get(&(record.kind, record.id.clone()))
            .is_none_or(|r| r.version <= record.version)
        {
            local::put_base(&tx, record, true)?;
        }
    }
    tx.execute("DELETE FROM sync_inflight WHERE id=1", [])
        .map_err(db_error)?;
    tx.commit().map_err(db_error)
}
fn remap(v: &mut Value, mapping: &HashMap<(wire::Kind, String), String>) {
    match v {
        Value::Object(map) => {
            for (key, value) in map.iter_mut() {
                let kind = match key.as_str() {
                    "rootId" | "groupId" | "parentId" => Some(wire::Kind::Category),
                    _ => None,
                };
                if let Some(kind) = kind {
                    if let Some(id) = value.as_str() {
                        if let Some(new) = mapping.get(&(kind, id.into())) {
                            *value = Value::String(new.clone());
                        }
                    }
                } else {
                    remap(value, mapping);
                }
            }
        }
        Value::Array(v) => {
            for v in v {
                remap(v, mapping);
            }
        }
        _ => {}
    }
}
fn copy_id(store: &Store, kind: wire::Kind, source: &str) -> Result<String> {
    let saved: Option<String> = store
        .conn
        .query_row(
            "SELECT target FROM sync_merge WHERE kind=?1 AND id=?2",
            params![kind.as_str(), source],
            |r| r.get(0),
        )
        .optional()
        .map_err(db_error)?;
    let next = saved.unwrap_or_else(id);
    store
        .conn
        .execute(
            "INSERT OR IGNORE INTO sync_merge VALUES(?1,?2,?3)",
            params![kind.as_str(), source, next],
        )
        .map_err(db_error)?;
    Ok(next)
}
/// 首次合并保持原主键；只有明确选择保留两份的记录及其关联副本分配新身份。
/// 映射先持久化，事务失败后重新预览不会不断生成副本。
fn merge_confirm(
    store: &Store,
    remote: &wire::Snapshot,
    source: &Values,
    choices: &HashMap<String, String>,
) -> Result<()> {
    let mut materials = local::MaterialBatch::default();
    let mut merged = Values::new();
    for r in &remote.records {
        merged.insert(
            (r.kind, r.id.clone()),
            localize(store, &r.value, &remote.assets)?,
        );
    }
    let mut mapping = HashMap::new();
    for (kind, source_id) in source.keys() {
        if choices
            .get(&key(*kind, source_id))
            .is_some_and(|c| c == "both")
            && *kind != wire::Kind::Setting
        {
            mapping.insert(
                (*kind, source_id.clone()),
                copy_id(store, *kind, source_id)?,
            );
        }
    }
    let mut copied_categories: HashSet<_> = mapping
        .keys()
        .filter(|(k, _)| *k == wire::Kind::Category)
        .map(|(_, id)| id.clone())
        .collect();
    loop {
        let mut more = vec![];
        for ((kind, id), v) in source {
            if *kind == wire::Kind::Category
                && v.as_ref()
                    .and_then(|v| v["parentId"].as_str())
                    .is_some_and(|parent| copied_categories.contains(parent))
                && !copied_categories.contains(id)
            {
                more.push(id.clone());
            }
        }
        if more.is_empty() {
            break;
        }
        for id in more {
            copied_categories.insert(id.clone());
            if merged.contains_key(&(wire::Kind::Category, id.clone())) {
                mapping.insert(
                    (wire::Kind::Category, id.clone()),
                    copy_id(store, wire::Kind::Category, &id)?,
                );
            }
        }
    }
    let mut include = HashSet::new();
    for ((kind, id), v) in source {
        if category_refs(*kind, id, v.as_ref())
            .iter()
            .any(|id| copied_categories.contains(id))
        {
            include.insert((*kind, id.clone()));
            if merged.contains_key(&(*kind, id.clone()))
                && *kind != wire::Kind::Setting
                && !mapping.contains_key(&(*kind, id.clone()))
            {
                mapping.insert((*kind, id.clone()), copy_id(store, *kind, id)?);
            }
        }
    }
    let mut local_keys = HashSet::new();
    for ((kind, source_id), value) in source {
        let k = (*kind, source_id.clone());
        let choice = choices
            .get(&key(*kind, source_id))
            .map(String::as_str)
            .unwrap_or("server");
        if !["server", "local", "both"].contains(&choice)
            || (*kind == wire::Kind::Setting && choice == "both")
        {
            return Err("请选择有效的合并方式。".into());
        }
        if merged.contains_key(&k) && choice == "server" && !include.contains(&k) {
            continue;
        }
        let target_id = mapping
            .get(&k)
            .cloned()
            .unwrap_or_else(|| source_id.clone());
        let mut value = value.clone();
        if let Some(v) = value.as_mut() {
            remap_record(*kind, v, &mapping);
            materials.append(copy_materials(store, v)?);
        }
        merged.insert((*kind, target_id.clone()), value);
        local_keys.insert((*kind, target_id));
    }
    let settings = store.settings()?;
    let tx = store.conn.unchecked_transaction().map_err(db_error)?;
    local::project_in(&tx, settings, &merged, false, true)?;
    for table in [
        "sync_records",
        "sync_dirty",
        "sync_inflight",
        "sync_desired_settings",
    ] {
        tx.execute(&format!("DELETE FROM {table}"), [])
            .map_err(db_error)?;
    }
    for r in &remote.records {
        local::put_base(&tx, r, false)?;
    }
    for ((kind, id), value) in &merged {
        if *kind == wire::Kind::Setting {
            if let Some(v) = value {
                tx.execute(
                    "INSERT INTO sync_desired_settings VALUES(?1,?2)",
                    params![id, v.to_string()],
                )
                .map_err(db_error)?;
            }
        }
        local::bump(&tx, *kind, id, local_keys.contains(&(*kind, id.clone())))?;
    }
    for (k, v) in [
        ("generation", remote.generation.clone()),
        ("remoteVersion", remote.version.to_string()),
        ("enabled", "1".into()),
        ("initialized", "1".into()),
        ("reconcile", "0".into()),
    ] {
        tx.execute("INSERT INTO sync_meta VALUES(?1,?2) ON CONFLICT(key) DO UPDATE SET value=excluded.value",params![k,v]).map_err(db_error)?;
    }
    tx.commit().map_err(db_error)?;
    materials.keep();
    Ok(())
}
pub fn related_keys(kind: wire::Kind, id: &str, values: &Values) -> Keys {
    let mut keys = HashSet::from([(kind, id.into())]);
    if kind != wire::Kind::Category {
        return keys;
    }
    let mut cats = HashSet::from([id.to_owned()]);
    loop {
        let before = cats.len();
        for ((k, id), v) in values {
            if *k == wire::Kind::Category
                && v.as_ref()
                    .and_then(|v| v["parentId"].as_str())
                    .is_some_and(|p| cats.contains(p))
            {
                cats.insert(id.clone());
            }
        }
        if before == cats.len() {
            break;
        }
    }
    for ((k, id), v) in values {
        if category_refs(*k, id, v.as_ref())
            .iter()
            .any(|id| cats.contains(id))
        {
            keys.insert((*k, id.clone()));
        }
    }
    keys
}
fn copy_materials(store: &Store, value: &mut Value) -> Result<local::MaterialBatch> {
    let mut materials = local::MaterialBatch::default();
    walk(value, &mut |b| {
        let Some(path) = b.get("path").and_then(Value::as_str) else {
            return Ok(());
        };
        let mut p = Phrase {
            id: String::new(),
            root_id: String::new(),
            group_id: None,
            title: String::new(),
            color: String::new(),
            background: String::new(),
            shortcut: String::new(),
            position: 0,
            segments: vec![Segment {
                blocks: vec![if b["kind"] == "image" {
                    Block::Image {
                        path: path.into(),
                        name: b["name"].as_str().unwrap_or_default().into(),
                    }
                } else {
                    Block::File {
                        path: path.into(),
                        name: b["name"].as_str().unwrap_or_default().into(),
                    }
                }],
            }],
        };
        materials.append(local::safe_materials(store, &mut p)?);
        match &p.segments[0].blocks[0] {
            Block::Image { path, .. } | Block::File { path, .. } => {
                b.insert("path".into(), Value::String(path.clone()));
            }
            _ => {}
        }
        Ok(())
    })?;
    Ok(materials)
}
fn freeze_materials(values: &mut Values, allow_missing: bool) -> Result<tempfile::TempDir> {
    let temp = tempfile::tempdir().map_err(db_error)?;
    let mut copied = HashMap::<String, String>::new();
    for value in values.values_mut().flatten() {
        walk(value, &mut |block| {
            let Some(path) = block.get("path").and_then(Value::as_str) else {
                return Ok(());
            };
            let target = if let Some(target) = copied.get(path) {
                target.clone()
            } else {
                if allow_missing && !Path::new(path).exists() {
                    return Ok(());
                }
                let meta = std::fs::metadata(path)
                    .map_err(|_| "素材缺失，请补齐后再开启同步。".to_owned())?;
                if !meta.is_file() || meta.len() > 128 * 1024 * 1024 {
                    return Err("单个素材不能超过128MB。".into());
                }
                let folder = temp.path().join(id());
                std::fs::create_dir_all(&folder).map_err(db_error)?;
                let target = folder.join(Path::new(path).file_name().ok_or("素材名称无效。")?);
                let size = std::fs::copy(path, &target).map_err(db_error)?;
                if size != meta.len() {
                    return Err("素材发生变化，请重新预览。".into());
                }
                let target = external_path(&target);
                copied.insert(path.into(), target.clone());
                target
            };
            block.insert("path".into(), Value::String(target));
            Ok(())
        })?;
    }
    Ok(temp)
}
pub fn remap_record(
    kind: wire::Kind,
    value: &mut Value,
    mapping: &HashMap<(wire::Kind, String), String>,
) {
    if kind != wire::Kind::Setting {
        if let Some(id) = value["id"].as_str() {
            if let Some(new) = mapping.get(&(kind, id.into())) {
                value["id"] = Value::String(new.clone());
            }
        }
    }
    remap(value, mapping);
    if kind == wire::Kind::Recycle {
        for (array, kind) in [
            ("categories", wire::Kind::Category),
            ("phrases", wire::Kind::Phrase),
            ("common", wire::Kind::Common),
        ] {
            if let Some(records) = value[array].as_array_mut() {
                for record in records {
                    remap_record(kind, record, mapping);
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    fn root(id: &str) -> Value {
        json!({"id":id,"name":"私人分类","parentId":null,"position":0,"background":""})
    }
    fn phrase(id: &str, root: &str, text: &str) -> Value {
        json!({"id":id,"rootId":root,"groupId":null,"title":text,"color":"","background":"","shortcut":"","position":0,"segments":[{"blocks":[{"kind":"text","text":text}]}]})
    }
    fn snapshot(version: u64, values: Vec<(wire::Kind, String, Option<Value>)>) -> wire::Snapshot {
        wire::Snapshot {
            generation: "generation".into(),
            version,
            records: values
                .into_iter()
                .map(|(kind, id, value)| wire::Record {
                    kind,
                    id,
                    version,
                    value,
                })
                .collect(),
            assets: vec![],
        }
    }
    fn put(store: &Store, kind: wire::Kind, id: &str, value: Option<Value>) {
        let mut records = local::local_records(store).unwrap();
        records.insert((kind, id.into()), value);
        let tx = store.conn.unchecked_transaction().unwrap();
        local::project_in(&tx, store.settings().unwrap(), &records, false, false).unwrap();
        local::bump(&tx, kind, id, true).unwrap();
        tx.commit().unwrap();
    }
    #[test]
    fn merge_preserves_ids_duplicates_only_selected_tree_and_retries_without_extra_copies() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        let root_id = id();
        let group = id();
        let p = id();
        let mut group_value = root(&group);
        group_value["parentId"] = json!(root_id);
        let mut p_value = phrase(&p, &root_id, "本机");
        p_value["groupId"] = json!(group);
        let source = Values::from([
            (
                (wire::Kind::Category, root_id.clone()),
                Some(root(&root_id)),
            ),
            (
                (wire::Kind::Category, group.clone()),
                Some(group_value.clone()),
            ),
            ((wire::Kind::Phrase, p.clone()), Some(p_value.clone())),
        ]);
        let remote = snapshot(
            1,
            vec![
                (wire::Kind::Category, root_id.clone(), Some(root(&root_id))),
                (wire::Kind::Category, group.clone(), Some(group_value)),
                (wire::Kind::Phrase, p.clone(), Some(p_value)),
            ],
        );
        let choices = HashMap::from([(key(wire::Kind::Category, &root_id), "both".into())]);
        merge_confirm(&store, &remote, &source, &choices).unwrap();
        let lib = store.library().unwrap();
        assert_eq!(lib.phrases.len(), 2);
        assert_eq!(lib.categories.len(), 4);
        assert!(lib.phrases.iter().any(|v| v.id == p));
        let copy = lib.phrases.iter().find(|v| v.id != p).unwrap();
        assert_ne!(copy.root_id, root_id);
        assert_ne!(copy.group_id.as_deref(), Some(group.as_str()));
        let ids: HashSet<_> = lib.phrases.iter().map(|p| p.id.clone()).collect();
        merge_confirm(&store, &remote, &source, &choices).unwrap();
        assert_eq!(
            store
                .library()
                .unwrap()
                .phrases
                .iter()
                .map(|p| p.id.clone())
                .collect::<HashSet<_>>(),
            ids
        );
    }
    #[test]
    fn remote_category_deletion_keeps_offline_draft_dependencies_and_unrelated_changes() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        let sync = PrivateSync::new(dir.path());
        let r = id();
        let p = id();
        let other = id();
        let op = id();
        sync.apply(
            &store,
            &snapshot(
                1,
                vec![
                    (wire::Kind::Category, r.clone(), Some(root(&r))),
                    (wire::Kind::Phrase, p.clone(), Some(phrase(&p, &r, "旧版"))),
                    (wire::Kind::Category, other.clone(), Some(root(&other))),
                    (
                        wire::Kind::Phrase,
                        op.clone(),
                        Some(phrase(&op, &other, "另一分类")),
                    ),
                ],
            ),
        )
        .unwrap();
        put(
            &store,
            wire::Kind::Phrase,
            &p,
            Some(phrase(&p, &r, "离线草稿")),
        );
        sync.apply(
            &store,
            &snapshot(
                2,
                vec![
                    (wire::Kind::Category, r.clone(), None),
                    (wire::Kind::Phrase, p.clone(), None),
                    (wire::Kind::Category, other.clone(), Some(root(&other))),
                    (
                        wire::Kind::Phrase,
                        op.clone(),
                        Some(phrase(&op, &other, "远端新版")),
                    ),
                ],
            ),
        )
        .unwrap();
        assert_eq!(store.phrase(&p).unwrap().title, "离线草稿");
        assert_eq!(store.phrase(&op).unwrap().title, "远端新版");
        let conflicts = conflict_keys(&store).unwrap();
        assert!(conflicts.contains(&(wire::Kind::Category, r.clone())));
        assert!(conflicts.contains(&(wire::Kind::Phrase, p.clone())));
        let values = local::local_records(&store).unwrap();
        let blocked = HashSet::from([r]);
        assert!(depends_on(
            wire::Kind::Phrase,
            &p,
            values
                .get(&(wire::Kind::Phrase, p.clone()))
                .and_then(Option::as_ref),
            &values,
            &blocked
        ));
        assert!(!depends_on(
            wire::Kind::Phrase,
            &op,
            values
                .get(&(wire::Kind::Phrase, op.clone()))
                .and_then(Option::as_ref),
            &values,
            &blocked
        ));
        sync.apply(
            &store,
            &snapshot(
                1,
                vec![(
                    wire::Kind::Phrase,
                    op.clone(),
                    Some(phrase(&op, &other, "延迟旧响应")),
                )],
            ),
        )
        .unwrap();
        assert_eq!(store.phrase(&op).unwrap().title, "远端新版");
    }
    #[test]
    fn category_conflict_blocks_deleted_descendants_using_their_previous_relationships() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = Store::open(dir.path()).unwrap();
        let sync = PrivateSync::new(dir.path());
        let r = id();
        let g = id();
        let p = id();
        let c = id();
        let mut group = root(&g);
        group["parentId"] = json!(r);
        let mut pv = phrase(&p, &r, "原话术");
        pv["groupId"] = json!(g);
        sync.apply(
            &store,
            &snapshot(
                1,
                vec![
                    (wire::Kind::Category, r.clone(), Some(root(&r))),
                    (wire::Kind::Category, g.clone(), Some(group)),
                    (wire::Kind::Phrase, p.clone(), Some(pv)),
                ],
            ),
        )
        .unwrap();
        let mut category = store
            .categories()
            .unwrap()
            .into_iter()
            .find(|cat| cat.id == r)
            .unwrap();
        category.name = "本机改名".into();
        store.save_category(category).unwrap();
        store.delete_category(&g, false, None, true).unwrap();
        put(
            &store,
            wire::Kind::Common,
            &c,
            Some(json!({"id":c,"text":"无关改动","position":0})),
        );
        let mut remote = root(&r);
        remote["name"] = json!("服务器改名");
        sync.apply(
            &store,
            &snapshot(2, vec![(wire::Kind::Category, r.clone(), Some(remote))]),
        )
        .unwrap();
        let (changes, _) = pending_changes(&store).unwrap();
        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].id, c);
        assert!(local::dirty(&store)
            .unwrap()
            .contains_key(&(wire::Kind::Phrase, p)));
        assert!(local::dirty(&store)
            .unwrap()
            .contains_key(&(wire::Kind::Category, g)));
    }
    #[test]
    fn offline_category_deletion_does_not_orphan_new_remote_children() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = Store::open(dir.path()).unwrap();
        let sync = PrivateSync::new(dir.path());
        let r = id();
        let p = id();
        let g = id();
        let added = id();
        sync.apply(
            &store,
            &snapshot(
                1,
                vec![
                    (wire::Kind::Category, r.clone(), Some(root(&r))),
                    (
                        wire::Kind::Phrase,
                        p.clone(),
                        Some(phrase(&p, &r, "旧话术")),
                    ),
                ],
            ),
        )
        .unwrap();
        store.delete_category(&r, false, None, true).unwrap();
        let mut group = root(&g);
        group["parentId"] = json!(r);
        let mut pv = phrase(&added, &r, "新话术");
        pv["groupId"] = json!(g);
        sync.apply(
            &store,
            &snapshot(
                2,
                vec![
                    (wire::Kind::Category, r.clone(), Some(root(&r))),
                    (wire::Kind::Category, g.clone(), Some(group)),
                    (
                        wire::Kind::Phrase,
                        p.clone(),
                        Some(phrase(&p, &r, "旧话术")),
                    ),
                    (wire::Kind::Phrase, added.clone(), Some(pv)),
                ],
            ),
        )
        .unwrap();
        assert!(store.categories().unwrap().is_empty());
        assert!(store.library().unwrap().phrases.is_empty());
        let conflicts = conflict_keys(&store).unwrap();
        assert!(conflicts.contains(&(wire::Kind::Category, r)));
        assert!(conflicts.contains(&(wire::Kind::Category, g)));
        assert!(conflicts.contains(&(wire::Kind::Phrase, added)));
        assert_eq!(store.recycle_records().unwrap().len(), 1);
        assert!(pending_changes(&store).unwrap().0.is_empty());
    }
    #[test]
    fn conflict_description_exposes_body_segments_and_material_names_for_equal_titles() {
        let p = id();
        let r = id();
        let mut v = phrase(&p, &r, "相同标题");
        v["segments"] = json!([
            {"blocks":[{"kind":"text","text":"本机正文"},{"kind":"image","name":"示例.png","assetId":id()}]},
            {"blocks":[{"kind":"file","name":"价目表.pdf","assetId":id()}]},
        ]);
        let local = describe(wire::Kind::Phrase, &Some(v.clone()));
        v["segments"][0]["blocks"][0]["text"] = json!("服务器正文");
        let remote = describe(wire::Kind::Phrase, &Some(v));
        assert_ne!(local, remote);
        assert!(local.contains("本机正文"));
        assert!(remote.contains("服务器正文"));
        assert!(local.contains("第2段"));
        assert!(local.contains("[图片：示例.png]"));
        assert!(local.contains("[附件：价目表.pdf]"));
        assert!(!local.contains("assetId"));
        assert_eq!(describe(wire::Kind::Phrase, &None), "已删除");
    }
    #[test]
    fn old_ack_preserves_edits_made_during_upload_and_never_replaces_new_base() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        let cid = id();
        put(
            &store,
            wire::Kind::Common,
            &cid,
            Some(json!({"id":cid,"text":"旧草稿","position":0})),
        );
        let rev = local::dirty(&store).unwrap()[&(wire::Kind::Common, cid.clone())];
        let request = wire::Mutation {
            generation: "g".into(),
            request_id: id(),
            changes: vec![],
            new_assets: vec![],
        };
        store
            .conn
            .execute(
                "INSERT INTO sync_inflight VALUES(1,?1,?2)",
                params![
                    serde_json::to_string(&request).unwrap(),
                    serde_json::to_string(&HashMap::from([(key(wire::Kind::Common, &cid), rev)]))
                        .unwrap()
                ],
            )
            .unwrap();
        put(
            &store,
            wire::Kind::Common,
            &cid,
            Some(json!({"id":cid,"text":"新草稿","position":0})),
        );
        let newer = wire::Record {
            kind: wire::Kind::Common,
            id: cid.clone(),
            version: 9,
            value: Some(json!({"id":cid,"text":"远端新","position":0})),
        };
        let tx = store.conn.unchecked_transaction().unwrap();
        local::put_base(&tx, &newer, false).unwrap();
        tx.commit().unwrap();
        let result = wire::Commit {
            generation: "g".into(),
            request_id: request.request_id,
            version: 2,
            records: vec![wire::Record {
                version: 2,
                ..newer
            }],
            conflicts: vec![],
        };
        acknowledge(&store, &result).unwrap();
        assert_eq!(local::dirty(&store).unwrap().len(), 1);
        assert_eq!(
            local::base_records(&store).unwrap()[&(wire::Kind::Common, cid.clone())].version,
            9
        );
        assert_eq!(store.common_phrases().unwrap()[0].text, "新草稿");
        assert!(acknowledge(&store, &result).is_err());
    }
    #[test]
    fn unconfirmed_send_settings_never_reverse_sync_and_record_keys_expire_on_edit() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        let sync = PrivateSync::new(dir.path());
        sync.apply(
            &store,
            &snapshot(
                1,
                vec![
                    (
                        wire::Kind::Setting,
                        "enter".into(),
                        Some(json!("ctrl-enter")),
                    ),
                    (wire::Kind::Setting, "theme".into(), Some(json!("blue"))),
                ],
            ),
        )
        .unwrap();
        assert_eq!(store.settings().unwrap().enter, "enter");
        assert_eq!(store.settings().unwrap().theme, "blue");
        let mut settings = store.settings().unwrap();
        settings.font_size = 14;
        store.save_settings(&settings).unwrap();
        assert!(!local::dirty(&store)
            .unwrap()
            .contains_key(&(wire::Kind::Setting, "enter".into())));
        assert_eq!(
            local::local_records(&store).unwrap()[&(wire::Kind::Setting, "enter".into())],
            Some(json!("ctrl-enter"))
        );
        let c = id();
        put(
            &store,
            wire::Kind::Common,
            &c,
            Some(json!({"id":c,"text":"一","position":0})),
        );
        let key = local::content_key(&store, "common", &c).unwrap();
        put(
            &store,
            wire::Kind::Common,
            &c,
            Some(json!({"id":c,"text":"二","position":0})),
        );
        assert!(local::resolve_key(&store, &key, "common").is_err());
        let another = Store::open(&dir.path().join("other")).unwrap();
        assert!(local::resolve_key(&another, &key, "common").is_err());
    }
    #[test]
    fn preview_freezes_external_assets_and_remaps_recycled_inner_ids() {
        let dir = tempfile::tempdir().unwrap();
        let attachment = dir.path().join("原附件.txt");
        std::fs::write(&attachment, "预览版").unwrap();
        let p = id();
        let root_id = id();
        let r = id();
        let mut pv = phrase(&p, &root_id, "预览");
        pv["segments"][0]["blocks"] =
            json!([{"kind":"file","path":external_path(&attachment),"name":"原附件.txt"}]);
        let mut values = Values::from([((wire::Kind::Phrase, p.clone()), Some(pv.clone()))]);
        let temp = freeze_materials(&mut values, false).unwrap();
        std::fs::write(&attachment, "后来的版本").unwrap();
        let frozen = values.values().next().unwrap().as_ref().unwrap()["segments"][0]["blocks"][0]
            ["path"]
            .as_str()
            .unwrap();
        assert_eq!(std::fs::read_to_string(frozen).unwrap(), "预览版");
        assert!(Path::new(frozen).starts_with(temp.path()));
        let mut recycle = json!({"id":r,"categories":[root(&root_id)],"phrases":[pv]});
        let mapping = HashMap::from([
            ((wire::Kind::Category, root_id.clone()), id()),
            ((wire::Kind::Phrase, p.clone()), id()),
            ((wire::Kind::Recycle, r.clone()), id()),
        ]);
        remap_record(wire::Kind::Recycle, &mut recycle, &mapping);
        assert_eq!(
            recycle["categories"][0]["id"],
            mapping[&(wire::Kind::Category, root_id.clone())]
        );
        assert_eq!(
            recycle["phrases"][0]["id"],
            mapping[&(wire::Kind::Phrase, p)]
        );
        assert_eq!(
            recycle["phrases"][0]["rootId"],
            mapping[&(wire::Kind::Category, root_id)]
        );
    }
}
