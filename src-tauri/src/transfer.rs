//! 统一导入导出的协调层：只在短事务内读取/提交，网络和ZIP处理不持数据库锁。
//! 预览保存固定副本ID及素材；提交结果不明时保留原批次，重启也先核对，不盲目重发。
use crate::{
    backup::{self, ExportSnapshot, PendingImport, SettingsScope},
    model::*,
    private_local as local,
    store::db_error,
    AppState,
};
use serde::{Deserialize, Serialize};
use shanyu_team_model as wire;
use std::{
    collections::HashMap,
    fs,
    path::{Path, PathBuf},
    sync::Mutex,
};
use tauri::State;
use tokio_util::sync::CancellationToken;

#[derive(Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(
    tag = "source",
    rename_all = "lowercase",
    rename_all_fields = "camelCase"
)]
pub enum Target {
    Private {
        library_id: String,
        session_epoch: u64,
        #[serde(default)]
        device_part: bool,
    },
    Team {
        team_id: String,
        version: u64,
        lifecycle_version: u64,
        session_epoch: u64,
    },
}
impl Target {
    fn device_part(&self) -> bool {
        matches!(
            self,
            Self::Private {
                device_part: true,
                ..
            }
        )
    }
    fn same_library(&self, other: &Self) -> bool {
        match (self, other) {
            (
                Self::Private {
                    library_id: a,
                    device_part: ad,
                    ..
                },
                Self::Private {
                    library_id: b,
                    device_part: bd,
                    ..
                },
            ) => a == b && ad == bd,
            (
                Self::Team {
                    team_id: a,
                    lifecycle_version: av,
                    ..
                },
                Self::Team {
                    team_id: b,
                    lifecycle_version: bv,
                    ..
                },
            ) => a == b && av == bv,
            _ => false,
        }
    }
}
#[derive(Clone, Serialize, Deserialize)]
struct Batch {
    target: Target,
    index_name: String,
    label: String,
    epoch: u64,
    server: String,
    owner: String,
    generation: String,
    clock: u64,
    merge: bool,
    restore_settings: bool,
    pending: PendingImport,
    #[serde(default)]
    device_settings: Option<Settings>,
    base: Option<wire::Snapshot>,
    request: Option<wire::Publish>,
    state: String,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Preview {
    #[serde(flatten)]
    preview: backup::ImportPreview,
    target: Target,
    target_label: String,
    merge: bool,
    restore_settings: bool,
    state: String,
    attachments: usize,
}
impl Batch {
    fn preview(&self) -> Preview {
        Preview {
            preview: self.pending.preview.clone(),
            target: self.target.clone(),
            target_label: self.label.clone(),
            merge: self.merge,
            restore_settings: self.restore_settings,
            state: self.state.clone(),
            attachments: self
                .pending
                .library
                .phrases
                .iter()
                .chain(self.pending.recycle_bin.iter().flat_map(|r| &r.phrases))
                .flat_map(|p| &p.segments)
                .flat_map(|s| &s.blocks)
                .filter(|b| matches!(b, Block::File { .. }))
                .count(),
        }
    }
}
#[derive(Default)]
pub struct TransferState {
    operation: Mutex<Option<(String, CancellationToken)>>,
}
struct Operation<'a> {
    _activity: crate::update_activity::Activity,
    state: &'a TransferState,
    id: String,
    cancel: CancellationToken,
}
impl Operation<'_> {
    fn check(&self) -> Result<()> {
        if self.cancel.is_cancelled() {
            Err("操作已取消。".into())
        } else {
            Ok(())
        }
    }
}
impl Drop for Operation<'_> {
    fn drop(&mut self) {
        let mut active = self.state.operation.lock().unwrap();
        if active.as_ref().is_some_and(|(id, _)| id == &self.id) {
            *active = None;
        }
    }
}
impl TransferState {
    pub fn is_active(&self) -> bool {
        self.operation.lock().unwrap().is_some()
    }
    /// 注销清理共享话术批次内的快照和素材；私人批次仍按库身份保留，原导入文件不删除。
    pub fn invalidate_groups(&self, root: &Path) {
        if let Some((_, cancel)) = &*self.operation.lock().unwrap() {
            cancel.cancel();
        }
        let Ok(entries) = fs::read_dir(root) else {
            return;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_file() && entry.file_name().to_string_lossy().starts_with("team-") {
                let _ = fs::remove_file(path);
                continue;
            }
            if !path.is_dir() {
                continue;
            }
            if let Ok(file) = fs::File::open(path.join("batch.json")) {
                if serde_json::from_reader::<_, Batch>(file)
                    .is_ok_and(|b| matches!(b.target, Target::Team { .. }))
                {
                    if let Err(e) = remove_batch_dir(root, &entry.file_name().to_string_lossy()) {
                        log::warn!("共享话术导入缓存清理失败：{e}");
                    }
                }
            }
        }
    }
    pub fn invalidate_removed_groups(&self, root: &Path, removed: &[String]) {
        let Ok(entries) = fs::read_dir(root) else {
            return;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if !path.is_dir() {
                continue;
            }
            let Ok(file) = fs::File::open(path.join("batch.json")) else {
                continue;
            };
            let Ok(batch) = serde_json::from_reader::<_, Batch>(file) else {
                continue;
            };
            if matches!(&batch.target, Target::Team { team_id, .. } if removed.contains(team_id)) {
                if let Err(e) = remove_index(root, &batch)
                    .and_then(|_| remove_batch_dir(root, &batch.pending.preview.id))
                {
                    log::warn!("共享话术导入缓存清理失败：{e}");
                }
            }
        }
    }
    fn begin(&self, id: String) -> Result<Operation<'_>> {
        let activity = crate::update_activity::Activity::enter()?;
        if uuid::Uuid::parse_str(&id).is_err() {
            return Err("操作编号无效。".into());
        }
        let mut active = self.operation.lock().unwrap();
        if active.is_some() {
            return Err("已有导入导出任务，请稍后重试。".into());
        }
        let cancel = CancellationToken::new();
        *active = Some((id.clone(), cancel.clone()));
        Ok(Operation {
            _activity: activity,
            state: self,
            id,
            cancel,
        })
    }
}
fn root(state: &AppState) -> PathBuf {
    state.device_store.lock().unwrap().dir.join("transfer")
}
fn index_name(state: &AppState, target: &Target) -> Result<String> {
    Ok(match target {
        Target::Private { library_id, .. } => {
            if uuid::Uuid::parse_str(library_id).is_err() {
                return Err("个人库编号无效。".into());
            }
            format!(
                "{}-{library_id}.json",
                if target.device_part() {
                    "device"
                } else {
                    "private"
                }
            )
        }
        Target::Team { .. } => format!("team-{}.json", state.teams.transfer_namespace()?),
    })
}
fn valid_index(name: &str) -> bool {
    name.ends_with(".json")
        && name
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"-_.".contains(&b))
}
fn save(state: &AppState, batch: &Batch) -> Result<()> {
    if uuid::Uuid::parse_str(&batch.pending.preview.id).is_err() {
        return Err("导入缓存编号无效。".into());
    }
    let root = root(state);
    let dir = root.join(&batch.pending.preview.id);
    fs::create_dir_all(&dir).map_err(db_error)?;
    if !valid_index(&batch.index_name) {
        return Err("导入索引无效。".into());
    }
    let persist = || {
        for path in [dir.join("batch.json"), root.join(&batch.index_name)] {
            let mut temp = tempfile::NamedTempFile::new_in(&root).map_err(db_error)?;
            serde_json::to_writer(temp.as_file_mut(), batch).map_err(db_error)?;
            temp.as_file().sync_all().map_err(db_error)?;
            temp.persist(path).map_err(db_error)?;
        }
        Ok(())
    };
    match &batch.target {
        Target::Private { .. } => state.teams.with_transfer_epoch(batch.epoch, persist),
        Target::Team {
            team_id,
            lifecycle_version,
            ..
        } => state.teams.with_transfer_guard(
            team_id,
            (&batch.server, &batch.owner, batch.epoch),
            *lifecycle_version,
            false,
            persist,
        ),
    }
}
fn check_target_epoch(state: &AppState, target: &Target) -> Result<()> {
    let epoch = match target {
        Target::Private { session_epoch, .. } | Target::Team { session_epoch, .. } => {
            *session_epoch
        }
    };
    if state.teams.transfer_epoch() != epoch {
        return Err("会话已变化，请重新选择目标库。".into());
    }
    Ok(())
}
fn remove_batch_dir(root: &Path, id: &str) -> Result<()> {
    if uuid::Uuid::parse_str(id).is_err() {
        return Err("导入缓存编号无效。".into());
    }
    let path = root.join(id);
    if !path.exists() {
        return Ok(());
    }
    let base = root.canonicalize().map_err(db_error)?;
    let dir = path.canonicalize().map_err(db_error)?;
    if !dir.starts_with(&base) || dir == base {
        return Err("导入缓存路径无效。".into());
    }
    fs::remove_dir_all(dir).map_err(db_error)
}
fn finish(state: &AppState, batch: &Batch) -> Result<()> {
    let root = root(state);
    if !valid_index(&batch.index_name) {
        return Err("导入索引无效。".into());
    }
    remove_index(&root, batch)?;
    remove_batch_dir(&root, &batch.pending.preview.id)
}
fn remove_index(root: &Path, batch: &Batch) -> Result<()> {
    if !valid_index(&batch.index_name) {
        return Err("导入索引无效。".into());
    }
    let path = root.join(&batch.index_name);
    if !path.exists() {
        return Ok(());
    }
    let current: Batch = serde_json::from_reader(fs::File::open(&path).map_err(db_error)?)
        .map_err(|_| "导入缓存无效。".to_owned())?;
    if current.pending.preview.id == batch.pending.preview.id {
        fs::remove_file(path).map_err(db_error)?;
    }
    Ok(())
}
fn read_batch(state: &AppState, path: &Path, resume: Option<&Target>) -> Result<Option<Batch>> {
    if !path.exists() {
        return Ok(None);
    }
    let mut batch: Batch = serde_json::from_reader(fs::File::open(path).map_err(db_error)?)
        .map_err(|_| "导入缓存无效，请重新选择文件。".to_owned())?;
    // 重启或当前会话主动恢复时保留批次ID；确认IPC还携带当前目标，旧会话不能借恢复绕过失效检查。
    if resume.is_some_and(|t| t.same_library(&batch.target)) {
        batch.epoch = state.teams.transfer_epoch();
        match &mut batch.target {
            Target::Private { session_epoch, .. } | Target::Team { session_epoch, .. } => {
                *session_epoch = batch.epoch
            }
        };
    }
    check(state, &batch, false)?;
    Ok(Some(batch))
}
fn load_target(state: &AppState, target: &Target) -> Result<Option<Batch>> {
    read_batch(
        state,
        &root(state).join(index_name(state, target)?),
        Some(target),
    )
}
fn load(state: &AppState, id: &str, target: &Target) -> Result<Option<Batch>> {
    check_target_epoch(state, target)?;
    if uuid::Uuid::parse_str(id).is_err() {
        return Err("预览编号无效。".into());
    }
    let batch = read_batch(state, &root(state).join(id).join("batch.json"), None)?;
    if batch
        .as_ref()
        .is_some_and(|b| !b.target.same_library(target))
    {
        return Err("目标库已变化，请重新预览。".into());
    }
    Ok(batch)
}
fn clock(store: &crate::store::Store) -> Result<u64> {
    store
        .conn
        .query_row("SELECT value FROM sync_clock WHERE id=1", [], |r| {
            r.get::<_, i64>(0).map(|v| v as u64)
        })
        .map_err(db_error)
}
/// 设备部分独立提交，但仍绑定发起预览的个人库和会话，切号后的旧确认不能落盘。
fn with_import_store<T>(
    state: &AppState,
    target: &Target,
    work: impl FnOnce(&mut crate::store::Store) -> Result<T>,
) -> Result<T> {
    let Target::Private {
        library_id,
        device_part,
        ..
    } = target
    else {
        return Err("请选择个人内容。".into());
    };
    let mut active = state.store.lock().unwrap();
    local::scope(&active, Some(library_id))?;
    if *device_part {
        work(&mut state.device_store.lock().unwrap())
    } else {
        work(&mut active)
    }
}

fn check(state: &AppState, batch: &Batch, edit: bool) -> Result<()> {
    if state.teams.transfer_epoch() != batch.epoch {
        return Err("会话已变化，请重新预览。".into());
    }
    match &batch.target {
        Target::Private { .. } => with_import_store(state, &batch.target, |store| {
            if local::meta(store, "generation")? != batch.generation {
                return Err("个人库已恢复，请重新预览。".into());
            }
            if edit
                && !store.imported(&batch.pending.preview.id)?
                && (clock(store)? != batch.clock
                    || batch
                        .device_settings
                        .as_ref()
                        .is_some_and(|old| store.settings().as_ref().ok() != Some(old)))
            {
                return Err("内容或设置已更新，请重新预览。".into());
            }
            Ok(())
        }),
        Target::Team {
            team_id,
            lifecycle_version,
            ..
        } => state.teams.check_transfer(
            team_id,
            &batch.server,
            &batch.owner,
            batch.epoch,
            *lifecycle_version,
            edit,
        ),
    }
}

#[tauri::command]
pub fn cancel_data_transfer(state: State<AppState>, operation_id: String) -> Result<()> {
    if let Some((id, cancel)) = &*state.transfer.operation.lock().unwrap() {
        if id == &operation_id {
            cancel.cancel();
        }
    }
    Ok(())
}
#[tauri::command]
pub fn get_import_pending(state: State<AppState>, target: Target) -> Result<Option<Preview>> {
    check_target_epoch(&state, &target)?;
    let batch = load_target(&state, &target)?;
    let batch = batch.filter(|b| b.target.same_library(&target));
    if let Some(batch) = &batch {
        save(&state, batch)?;
    }
    Ok(batch.map(|b| b.preview()))
}
#[tauri::command]
pub fn discard_import(state: State<AppState>, preview_id: String, target: Target) -> Result<()> {
    if state.transfer.operation.lock().unwrap().is_some() {
        return Err("请先等待当前操作完成。".into());
    }
    if let Some(batch) = load(&state, &preview_id, &target)? {
        if batch.pending.preview.id != preview_id {
            return Err("预览已变化。".into());
        }
        if batch.state == "pending" {
            return Err("请先核对上次导入结果。".into());
        }
        finish(&state, &batch)?;
    }
    Ok(())
}
#[tauri::command]
pub async fn preview_import(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    path: String,
    target: Target,
    merge: bool,
    restore_settings: bool,
    operation_id: String,
) -> Result<Preview> {
    let pending = backup::preview(Path::new(&path))?;
    prepare_import(
        app,
        state,
        pending,
        target,
        merge,
        restore_settings,
        operation_id,
    )
    .await
}

/// 旧完整备份明确分两次提交，个人内容不会携带设备文件或设置跨账号同步。
fn select_import_part(pending: &mut PendingImport, device: bool) -> Result<()> {
    if device {
        if pending.settings_scope != SettingsScope::Device
            && pending.library.files.is_empty()
            && !pending.library.categories.iter().any(|c| c.kind == "file")
        {
            return Err("此备份不含本机文件或设备设置，请选择个人内容。".into());
        }
        pending.preview.notes.push(format!("本次仅导入本机文件与设备设置。{}条话术、{}条短语及{}条回收记录请另选个人内容导入。两部分分别提交。",
            pending.library.phrases.len(), pending.library.common_phrases.len(), pending.recycle_bin.len()));
        pending.library.categories.retain(|c| c.kind == "file");
        pending.library.phrases.clear();
        pending.library.common_phrases.clear();
        pending.recycle_bin.clear();
        pending.images.clear();
        pending.materials.clear();
    } else {
        if pending.settings_scope == SettingsScope::Device || !pending.library.files.is_empty() {
            pending.preview.notes.push(format!("本次导入个人内容；{}个文件关联及设备设置请另选“本机文件与设备设置”导入。两部分分别提交。", pending.library.files.len()));
        }
        pending.library.categories.retain(|c| c.kind == "phrase");
        pending.library.files.clear();
        if pending.settings_scope == SettingsScope::Device {
            pending.settings_scope = SettingsScope::Portable;
        }
    }
    Ok(())
}

fn import_settings(pending: &mut PendingImport, current: &Settings, device: bool) -> Result<()> {
    let mut next = serde_json::to_value(current).map_err(db_error)?;
    let incoming = serde_json::to_value(&pending.library.settings).map_err(db_error)?;
    for (key, value) in incoming.as_object().ok_or("备份设置格式无效。")? {
        if wire::private::SETTINGS.contains(&key.as_str()) != device {
            next[key] = value.clone();
        }
    }
    pending.library.settings = serde_json::from_value(next).map_err(db_error)?;
    Ok(())
}

#[tauri::command]
pub fn has_legacy_private_content(state: State<AppState>, library_id: String) -> Result<bool> {
    let store = state.store.lock().unwrap();
    local::scope(&store, Some(&library_id))?;
    let device = state.device_store.lock().unwrap();
    if store.dir == device.dir {
        return Ok(false);
    }
    device.conn.query_row("SELECT EXISTS(SELECT 1 FROM categories WHERE kind='phrase') OR EXISTS(SELECT 1 FROM common_phrases) OR EXISTS(SELECT 1 FROM recycle_bin)", [], |r| r.get(0)).map_err(db_error)
}

/// 恢复入口只读取旧本机内容，随后复用普通导入的素材冻结、事务和批次回执。
#[tauri::command]
pub async fn preview_legacy_private_content(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    target: Target,
    operation_id: String,
) -> Result<Preview> {
    let Target::Private {
        library_id,
        device_part: false,
        ..
    } = &target
    else {
        return Err("旧本机内容只能找回到个人话术。".into());
    };
    check_target_epoch(&state, &target)?;
    let pending = {
        let store = state.store.lock().unwrap();
        local::scope(&store, Some(library_id))?;
        let device = state.device_store.lock().unwrap();
        if store.dir == device.dir {
            return Err("当前正在使用本机内容，无需重复找回。".into());
        }
        let mut library = device.library()?;
        library.categories.retain(|c| c.kind == "phrase");
        library.files.clear();
        PendingImport {
            preview: backup::ImportPreview {
                id: id(),
                kind: "backup".into(),
                filename: "旧本机内容".into(),
                categories: 0,
                phrases: 0,
                common_phrases: 0,
                files: 0,
                images: 0,
                recycled: 0,
                rows: vec![],
                notes: vec![
                    "原数据保留；重复发起找回可能产生副本。本机文件关联及设备设置保持原样。".into(),
                ],
            },
            library,
            recycle_bin: device.recycle_records()?,
            images: HashMap::new(),
            materials: HashMap::new(),
            material_dir: None,
            settings_scope: SettingsScope::None,
        }
    };
    prepare_import(app, state, pending, target, false, false, operation_id).await
}

async fn prepare_import(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    mut pending: PendingImport,
    target: Target,
    merge: bool,
    restore_settings: bool,
    operation_id: String,
) -> Result<Preview> {
    let operation = state.transfer.begin(operation_id)?;
    check_target_epoch(&state, &target)?;
    let previous = load_target(&state, &target)?;
    if previous.as_ref().is_some_and(|b| b.state == "pending") {
        return Err("上次导入结果待确认，请先核对。".into());
    }
    if matches!(target, Target::Team { .. }) {
        let _ = state.teams.refresh_authorization(&app).await;
    }
    let mut batch = Batch {
        target: target.clone(),
        index_name: index_name(&state, &target)?,
        label: String::new(),
        epoch: state.teams.transfer_epoch(),
        server: String::new(),
        owner: String::new(),
        generation: String::new(),
        clock: 0,
        merge,
        restore_settings,
        pending: pending.clone(),
        device_settings: None,
        base: None,
        request: None,
        state: "ready".into(),
    };
    match &target {
        Target::Private { .. } => {
            with_import_store(&state, &target, |store| {
                let device = target.device_part();
                select_import_part(&mut pending, device)?;
                batch.label = if device {
                    "本机文件与设备设置".into()
                } else if local::bound(store) {
                    format!("个人话术 · {}", local::meta(store, "username")?)
                } else {
                    "个人话术".into()
                };
                batch.generation = local::meta(store, "generation")?;
                batch.clock = clock(store)?;
                if device {
                    batch.device_settings = Some(store.settings()?);
                }
                backup::note_merge_ambiguity(&mut pending, &store.categories()?, merge);
                import_settings(&mut pending, &store.settings()?, device)?;
                if pending.settings_scope == SettingsScope::None {
                    batch.restore_settings = false;
                }
                if !device && local::meta(store, "enabled")? == "1" {
                    pending.preview.notes.push(format!(
                        "导入后将同步到账号 {}。",
                        local::meta(store, "username")?
                    ));
                }
                Ok(())
            })?;
        }
        Target::Team {
            team_id,
            version,
            lifecycle_version,
            ..
        } => {
            let (server, owner, epoch) = state.teams.transfer_identity()?;
            state.teams.check_transfer(
                team_id,
                &server,
                &owner,
                epoch,
                *lifecycle_version,
                true,
            )?;
            let snapshot = state.teams.cached_snapshot(team_id)?;
            if snapshot.version != *version {
                return Err("共享话术已更新，请刷新后预览。".into());
            }
            let categories = snapshot
                .categories
                .iter()
                .map(|c| Category {
                    id: c.id.clone(),
                    name: c.name.clone(),
                    kind: "phrase".into(),
                    parent_id: c.parent_id.clone(),
                    position: c.position,
                    background: c.background.clone(),
                })
                .collect::<Vec<_>>();
            backup::note_merge_ambiguity(&mut pending, &categories, merge);
            batch.label = format!("共享话术 · {}", state.teams.view(team_id)?.team.name);
            batch.server = server;
            batch.owner = owner;
            batch.epoch = epoch;
            batch.base = Some(snapshot);
            batch.restore_settings = false;
            pending.preview.notes.push(format!(
                "不导入：{}条常用短语、{}条回收记录、{}个文件关联及个人设置。",
                pending.library.common_phrases.len(),
                pending.recycle_bin.len(),
                pending.library.files.len()
            ));
            pending.library.common_phrases.clear();
            pending.recycle_bin.clear();
            pending.library.files.clear();
            pending.library.categories.retain(|c| c.kind == "phrase");
            pending.settings_scope = SettingsScope::None;
        }
    }
    pending
        .preview
        .notes
        .push("导入独立副本，不覆盖现有话术。同名分类仅在同级唯一匹配时合并。".into());
    if batch.restore_settings {
        pending
            .preview
            .notes
            .push("发送和快捷键设置需另行确认；占用不影响正文导入。".into());
    }
    backup::prepare_ids(&mut pending)?;
    backup::recount(&mut pending);
    let dir = root(&state).join(&pending.preview.id);
    let result = backup::freeze_materials(&mut pending, &dir.join("materials"), &|| {
        operation.check()?;
        check(&state, &batch, false)
    });
    if let Err(e) = result {
        let _ = remove_batch_dir(&root(&state), &pending.preview.id);
        return Err(e);
    }
    batch.pending = pending;
    let persist = (|| {
        operation.check()?;
        check(&state, &batch, false)?;
        if let Some(old) = previous {
            finish(&state, &old)?;
        }
        save(&state, &batch)
    })();
    if let Err(e) = persist {
        let _ = remove_batch_dir(&root(&state), &batch.pending.preview.id);
        return Err(e);
    }
    Ok(batch.preview())
}

#[derive(Serialize)]
pub struct ImportResult {
    pub state: String,
    pub message: String,
}
fn complete(state: &AppState, batch: &Batch) -> ImportResult {
    let warning = finish(state, batch).err();
    ImportResult {
        state: "completed".into(),
        message: warning
            .map(|_| "导入完成，批次清理待重试。".into())
            .unwrap_or_else(|| "导入完成".into()),
    }
}
async fn resolve(
    app: &tauri::AppHandle,
    state: &AppState,
    batch: &mut Batch,
) -> Result<ImportResult> {
    check(state, batch, false)?;
    let Target::Team { team_id, .. } = &batch.target else {
        if with_import_store(state, &batch.target, |store| {
            store.imported(&batch.pending.preview.id)
        })? {
            return Ok(complete(state, batch));
        }
        return Ok(ImportResult {
            state: "ready".into(),
            message: "尚未导入".into(),
        });
    };
    let Some(request) = &batch.request else {
        return Ok(ImportResult {
            state: "ready".into(),
            message: "尚未导入".into(),
        });
    };
    let current = state.teams.transfer_snapshot(app, team_id).await?;
    check(state, batch, false)?;
    let base = batch.base.as_ref().ok_or("导入基准缺失。")?;
    let present = request
        .phrases
        .iter()
        .filter(|p| !base.phrases.iter().any(|old| old.id == p.id))
        .all(|p| current.phrases.iter().any(|v| v.id == p.id))
        && request
            .categories
            .iter()
            .filter(|c| !base.categories.iter().any(|old| old.id == c.id))
            .all(|c| current.categories.iter().any(|v| v.id == c.id));
    if current.version > request.expected_version && present {
        return Ok(complete(state, batch));
    }
    if current.version == request.expected_version {
        batch.state = "ready".into();
        save(state, batch)?;
        return Ok(ImportResult {
            state: "ready".into(),
            message: "尚未提交，可使用同一批次重试。".into(),
        });
    }
    if current.version > request.expected_version {
        let _ = state
            .teams
            .cleanup_transfer(team_id, &request.new_assets)
            .await;
        batch.state = "invalid".into();
        save(state, batch)?;
        return Ok(ImportResult {
            state: "invalid".into(),
            message: "共享话术已更新，请重新预览。".into(),
        });
    }
    Err("结果待确认，请稍后核对。".into())
}
#[tauri::command]
pub async fn check_import_result(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    preview_id: String,
    target: Target,
    operation_id: String,
) -> Result<ImportResult> {
    let operation = state.transfer.begin(operation_id)?;
    let mut batch = load(&state, &preview_id, &target)?.ok_or("请重新预览。")?;
    if batch.pending.preview.id != preview_id {
        return Err("预览已失效。".into());
    }
    operation.check()?;
    resolve(&app, &state, &mut batch).await
}
#[tauri::command]
pub async fn refresh_import_preview(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    preview_id: String,
    target: Target,
    operation_id: String,
) -> Result<Option<Preview>> {
    let operation = state.transfer.begin(operation_id)?;
    let mut batch = load(&state, &preview_id, &target)?.ok_or("请重新选择文件。")?;
    check(&state, &batch, false)?;
    operation.check()?;
    if batch.request.is_some() {
        let result = resolve(&app, &state, &mut batch).await?;
        if result.state == "completed" {
            return Ok(None);
        }
        if result.state == "ready" {
            return Ok(Some(batch.preview()));
        }
        if result.state == "pending" {
            return Err("请先核对上次导入结果。".into());
        }
        batch.request = None;
    }
    let import_target = batch.target.clone();
    match &mut batch.target {
        Target::Private { .. } => {
            let completed = with_import_store(&state, &import_target, |store| {
                if store.imported(&batch.pending.preview.id)? {
                    return Ok(true);
                }
                batch.clock = clock(store)?;
                if import_target.device_part() {
                    batch.device_settings = Some(store.settings()?);
                }
                import_settings(
                    &mut batch.pending,
                    &store.settings()?,
                    import_target.device_part(),
                )?;
                backup::note_merge_ambiguity(&mut batch.pending, &store.categories()?, batch.merge);
                Ok(false)
            })?;
            if completed {
                finish(&state, &batch)?;
                return Ok(None);
            }
        }
        Target::Team {
            team_id, version, ..
        } => {
            let _ = state.teams.refresh_authorization(&app).await;
            let base = state.teams.transfer_snapshot(&app, team_id).await?;
            let categories = base
                .categories
                .iter()
                .map(|c| Category {
                    id: c.id.clone(),
                    name: c.name.clone(),
                    kind: "phrase".into(),
                    parent_id: c.parent_id.clone(),
                    position: c.position,
                    background: c.background.clone(),
                })
                .collect::<Vec<_>>();
            backup::note_merge_ambiguity(&mut batch.pending, &categories, batch.merge);
            *version = base.version;
            batch.base = Some(base);
        }
    }
    operation.check()?;
    check(&state, &batch, true)?;
    batch.state = "ready".into();
    save(&state, &batch)?;
    Ok(Some(batch.preview()))
}
#[tauri::command]
pub async fn confirm_import(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    preview_id: String,
    target: Target,
    operation_id: String,
) -> Result<ImportResult> {
    let operation = state.transfer.begin(operation_id)?;
    let mut batch = load(&state, &preview_id, &target)?.ok_or("请先选择文件并预览。")?;
    if batch.pending.preview.id != preview_id {
        return Err("此预览已失效。".into());
    }
    check(&state, &batch, true)?;
    operation.check()?;
    if batch.state == "pending" {
        return resolve(&app, &state, &mut batch).await;
    }
    if batch.state == "invalid" {
        return Err("共享话术已更新，请重新预览。".into());
    }
    match &batch.target {
        Target::Private { .. } => {
            with_import_store(&state, &batch.target, |store| {
                if local::meta(store, "generation")? != batch.generation {
                    return Err("个人库已恢复，请重新预览。".into());
                }
                if !store.imported(&batch.pending.preview.id)?
                    && (clock(store)? != batch.clock
                        || batch
                            .device_settings
                            .as_ref()
                            .is_some_and(|old| store.settings().as_ref().ok() != Some(old)))
                {
                    return Err("内容或设置已更新，请重新预览。".into());
                }
                let mut pending = batch.pending.clone();
                import_settings(&mut pending, &store.settings()?, batch.target.device_part())?;
                state.teams.with_transfer_epoch(batch.epoch, || {
                    backup::apply_prepared(
                        store,
                        &pending,
                        batch.merge,
                        batch.restore_settings,
                        batch.target.device_part(),
                    )
                })
            })?;
            let mut result = complete(&state, &batch);
            if let Err(e) = crate::changed(&app, &state) {
                result.message = format!("导入完成，设置未生效：{e}");
            }
            Ok(result)
        }
        Target::Team {
            team_id, version, ..
        } => {
            let team_id = team_id.clone();
            if batch.request.is_none() {
                let base = batch.base.clone().ok_or("导入基准缺失。")?;
                if state.teams.cached_snapshot(&team_id)?.version != *version {
                    return Err("共享话术已更新，请重新预览。".into());
                }
                let request = state
                    .teams
                    .prepare_transfer(&team_id, base, &batch.pending, batch.merge)
                    .await?;
                batch.request = Some(request);
                if let Err(e) = check(&state, &batch, true).and_then(|_| save(&state, &batch)) {
                    let _ = state
                        .teams
                        .cleanup_transfer(&team_id, &batch.request.as_ref().unwrap().new_assets)
                        .await;
                    return Err(e);
                }
            }
            if operation.check().is_err() {
                let _ = state
                    .teams
                    .cleanup_transfer(&team_id, &batch.request.as_ref().unwrap().new_assets)
                    .await;
                batch.request = None;
                save(&state, &batch)?;
                return Err("操作已取消。".into());
            }
            check(&state, &batch, true)?;
            batch.state = "pending".into();
            save(&state, &batch)?;
            // 开始提交后不响应取消，不把中断当失败；先获取确定回执或保留待核对状态。
            match state
                .teams
                .publish(&app, &team_id, batch.request.clone().unwrap())
                .await
            {
                Ok(()) => Ok(complete(&state, &batch)),
                Err(_) => match resolve(&app, &state, &mut batch).await {
                    Ok(result) => Ok(result),
                    Err(_) => Ok(ImportResult {
                        state: "pending".into(),
                        message: "结果待确认，请核对后再操作。".into(),
                    }),
                },
            }
        }
    }
}

#[tauri::command]
pub async fn export_data(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    path: String,
    kind: String,
    category_id: Option<String>,
    target: Target,
    operation_id: String,
) -> Result<()> {
    if target.device_part() {
        return Err("设备部分仅用于分类导入，请选择个人话术导出。".into());
    }
    let operation = state.transfer.begin(operation_id)?;
    check_target_epoch(&state, &target)?;
    if matches!(target, Target::Team { .. }) {
        let _ = state.teams.refresh_authorization(&app).await;
    }
    let epoch = state.teams.transfer_epoch();
    let mut generation = String::new();
    let mut group_identity = None;
    let mut private_assets: HashMap<String, (String, u64, bool)> = HashMap::new();
    let mut group_assets = HashMap::new();
    let mut snapshot = match &target {
        Target::Private { library_id, .. } => {
            let store = state.store.lock().unwrap();
            local::scope(&store, Some(library_id))?;
            generation = local::meta(&store, "generation")?;
            let mut library = store.library()?;
            let portable = local::bound(&store);
            if portable {
                let mut desired = serde_json::to_value(&library.settings).map_err(db_error)?;
                for ((kind, key), value) in local::local_records(&store)? {
                    if kind == wire::private::Kind::Setting {
                        if let Some(value) = value {
                            desired[key] = value;
                        }
                    }
                }
                library.settings = serde_json::from_value(desired).map_err(db_error)?;
            }
            let mut stmt = store
                .conn
                .prepare("SELECT path,id,size,kind FROM sync_assets")
                .map_err(db_error)?;
            for row in stmt
                .query_map([], |r| {
                    Ok((
                        r.get::<_, String>(0)?,
                        r.get::<_, String>(1)?,
                        r.get::<_, i64>(2)?,
                        r.get::<_, String>(3)?,
                    ))
                })
                .map_err(db_error)?
            {
                let (path, id, size, kind) = row.map_err(db_error)?;
                private_assets.insert(path, (id, size as u64, kind == "image"));
            }
            ExportSnapshot {
                library,
                recycled: store.recycle_records()?,
                source: "private".into(),
                settings_scope: if portable {
                    SettingsScope::Portable
                } else {
                    SettingsScope::Device
                },
            }
        }
        Target::Team {
            team_id,
            version,
            lifecycle_version,
            ..
        } => {
            let identity = state.teams.transfer_identity()?;
            state.teams.check_transfer(
                team_id,
                &identity.0,
                &identity.1,
                identity.2,
                *lifecycle_version,
                false,
            )?;
            let raw = state.teams.cached_snapshot(team_id)?;
            if raw.version != *version {
                return Err("共享话术已更新，请刷新后导出。".into());
            }
            let mut view = state.teams.view(team_id)?;
            if view.team.version != raw.version
                || view.team.lifecycle_version != raw.lifecycle_version
            {
                return Err("共享话术已更新，请刷新后导出。".into());
            }
            for p in &raw.phrases {
                // 按ID匹配而不依赖缓存排序，素材引用固定在这次导出的版本。
                let local = view
                    .phrases
                    .iter_mut()
                    .find(|v| v.content_ref.id == p.id)
                    .ok_or("共享话术引用已变化。")?;
                for (block, local_block) in p
                    .segments
                    .iter()
                    .flat_map(|s| &s.blocks)
                    .zip(local.phrase.segments.iter_mut().flat_map(|s| &mut s.blocks))
                {
                    if let (
                        Some((id, asset_kind)),
                        Block::Image { path, name } | Block::File { path, name },
                    ) = (block.asset(), local_block)
                    {
                        *path = state.teams.transfer_asset_path(
                            team_id,
                            id,
                            name,
                            asset_kind == "image",
                        )?;
                        group_assets.insert(
                            path.clone(),
                            (id.to_owned(), name.clone(), asset_kind == "image"),
                        );
                    }
                }
            }
            group_identity = Some(identity);
            ExportSnapshot {
                library: Library {
                    categories: view.categories,
                    phrases: view
                        .phrases
                        .into_iter()
                        .map(|p| {
                            let mut local = p.phrase;
                            local.id = p.content_ref.id;
                            local
                        })
                        .collect(),
                    files: vec![],
                    common_phrases: vec![],
                    settings: Settings::default(),
                    data_dir: String::new(),
                },
                recycled: vec![],
                source: "team".into(),
                settings_scope: SettingsScope::None,
            }
        }
    };
    snapshot.filter(&kind, category_id.as_deref())?;
    let check_scope = || -> Result<()> {
        operation.check()?;
        if state.teams.transfer_epoch() != epoch {
            return Err("会话已变化，导出已取消。".into());
        }
        match &target {
            Target::Private { library_id, .. } => {
                let store = state.store.lock().unwrap();
                local::scope(&store, Some(library_id))?;
                if local::meta(&store, "generation")? != generation {
                    return Err("个人库已恢复，导出已取消。".into());
                }
            }
            Target::Team {
                team_id,
                lifecycle_version,
                ..
            } => {
                let identity = group_identity.as_ref().unwrap();
                state.teams.check_transfer(
                    team_id,
                    &identity.0,
                    &identity.1,
                    identity.2,
                    *lifecycle_version,
                    false,
                )?;
            }
        }
        Ok(())
    };
    let staging = tempfile::tempdir().map_err(db_error)?;
    let mut copied: HashMap<String, String> = HashMap::new();
    if kind == "backup" {
        for block in snapshot
            .library
            .phrases
            .iter_mut()
            .chain(snapshot.recycled.iter_mut().flat_map(|r| &mut r.phrases))
            .flat_map(|p| &mut p.segments)
            .flat_map(|s| &mut s.blocks)
        {
            let (path, image) = match block {
                Block::Image { path, .. } => (path, true),
                Block::File { path, .. } => (path, false),
                _ => continue,
            };
            check_scope()?;
            if let Some(target) = copied.get(path) {
                *path = target.clone();
                continue;
            }
            let target_file = staging.path().join(id());
            let ready = fs::metadata(&*path).is_ok_and(|m| {
                m.is_file()
                    && m.len() > 0
                    && private_assets
                        .get(path)
                        .is_none_or(|(_, size, _)| m.len() == *size)
            });
            if !ready {
                match &target {
                    Target::Private { .. } => {
                        let (id, size, image) = private_assets
                            .get(path)
                            .ok_or("源文件缺失，无法完成导出。")?;
                        let ctx = state
                            .teams
                            .account_context()
                            .map_err(|_| "素材未缓存，请联网后导出。".to_owned())?;
                        {
                            let store = state.store.lock().unwrap();
                            if local::meta(&store, "owner")? != ctx.user_id
                                || local::meta(&store, "server")? != ctx.server
                            {
                                return Err("素材所属账号已变化。".into());
                            }
                        }
                        let temp = tokio::select! { _ = operation.cancel.cancelled() => return Err("操作已取消。".into()), result = state.teams.account_download(&ctx, id, &target_file, *size, *image) => result? };
                        check_scope()?;
                        temp.persist(&target_file).map_err(db_error)?;
                    }
                    Target::Team { team_id, .. } => {
                        if state.teams.status().offline {
                            return Err("素材未缓存，请联网后导出。".into());
                        }
                        let (id, name, image) =
                            group_assets.get(path).ok_or("共享话术素材引用无效。")?;
                        tokio::select! { _ = operation.cancel.cancelled() => return Err("操作已取消。".into()), result = state.teams.transfer_download(&app, team_id, id, name, *image) => result? };
                        check_scope()?;
                        backup::copy_material(Path::new(path), &target_file, &check_scope)?;
                    }
                }
            } else {
                backup::copy_material(Path::new(path), &target_file, &check_scope)?;
            }
            if image {
                backup::decode_image(&fs::read(&target_file).map_err(db_error)?)?;
            }
            let new_path = external_path(&target_file);
            copied.insert(path.clone(), new_path.clone());
            *path = new_path;
        }
    }
    let temp = backup::write_export(
        snapshot,
        Path::new(&path),
        &kind,
        category_id.is_some(),
        &check_scope,
    )?;
    check_scope()?;
    match &target {
        Target::Private { library_id, .. } => {
            let store = state.store.lock().unwrap();
            local::scope(&store, Some(library_id))?;
            if local::meta(&store, "generation")? != generation {
                return Err("个人库已恢复，导出已取消。".into());
            }
            state.teams.with_transfer_epoch(epoch, || {
                operation.check()?;
                temp.persist(&path).map_err(db_error)?;
                Ok(())
            })
        }
        Target::Team {
            team_id,
            lifecycle_version,
            ..
        } => {
            let identity = group_identity.as_ref().unwrap();
            state.teams.with_transfer_guard(
                team_id,
                (&identity.0, &identity.1, identity.2),
                *lifecycle_version,
                false,
                || {
                    operation.check()?;
                    temp.persist(&path).map_err(db_error)?;
                    Ok(())
                },
            )
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn split_backup_keeps_personal_and_device_content_and_settings_separate() {
        let dir = tempfile::tempdir().unwrap();
        let store = crate::store::Store::open(dir.path()).unwrap();
        let mut library = store.library().unwrap();
        library.settings.theme = "blue".into();
        library.settings.teams_first = true;
        library.categories = vec![Category {
            id: id(),
            name: "文件".into(),
            kind: "file".into(),
            parent_id: None,
            position: 0,
            background: String::new(),
        }];
        let pending = PendingImport {
            library,
            recycle_bin: vec![],
            images: HashMap::new(),
            materials: HashMap::new(),
            material_dir: None,
            settings_scope: SettingsScope::Device,
            preview: backup::ImportPreview {
                id: id(),
                kind: "backup".into(),
                filename: "完整备份".into(),
                categories: 0,
                phrases: 0,
                common_phrases: 0,
                files: 0,
                images: 0,
                recycled: 0,
                rows: vec![],
                notes: vec![],
            },
        };
        let mut personal = pending.clone();
        select_import_part(&mut personal, false).unwrap();
        import_settings(&mut personal, &store.settings().unwrap(), false).unwrap();
        assert!(personal.library.categories.is_empty());
        assert_eq!(personal.library.settings.theme, "blue");
        assert!(!personal.library.settings.teams_first);
        assert_eq!(personal.preview.notes.len(), 1);
        let mut device = pending;
        select_import_part(&mut device, true).unwrap();
        import_settings(&mut device, &store.settings().unwrap(), true).unwrap();
        assert_eq!(device.library.categories.len(), 1);
        assert_eq!(
            device.library.settings.theme,
            store.settings().unwrap().theme
        );
        assert!(device.library.settings.teams_first);
        assert_eq!(device.preview.notes.len(), 1);
        store
            .conn
            .execute(
                "INSERT INTO sync_desired_settings VALUES('enter', ?1)",
                ["\"ctrl-enter\""],
            )
            .unwrap();
        let mut store = store;
        backup::prepare_ids(&mut device).unwrap();
        backup::apply_prepared(&mut store, &device, false, true, true).unwrap();
        let desired: String = store
            .conn
            .query_row(
                "SELECT value FROM sync_desired_settings WHERE id='enter'",
                [],
                |r| r.get(0),
            )
            .unwrap();
        assert_eq!(desired, "\"ctrl-enter\"");
        assert_eq!(store.settings().unwrap().enter, "enter");
        assert!(store.settings().unwrap().teams_first);
        assert!(select_import_part(&mut personal, true).is_err());
    }
}
