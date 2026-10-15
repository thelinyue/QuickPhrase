mod backup;
mod company_commands;
mod data_directory;
mod delivery;
mod file_library;
mod member_commands;
mod model;
mod native;
mod private_commands;
mod private_local;
mod private_sync;
mod recycle;
mod search_history;
mod startup;
mod store;
mod team;
mod team_commands;
mod team_credentials;
mod transfer;
mod update_activity;
mod update_protocol;
mod updater;

use delivery::{DeliveryReport, Mode};
use model::*;
use native::{NativeState, TargetContext};
use std::{
    fs,
    io::Write,
    path::Path,
    sync::{atomic::Ordering, Arc, Mutex},
};
use store::Store;
use tauri::{Emitter, Manager, State};

/// 应用状态按责任分开：数据库串行事务、待确认导入、发送锁和 Windows 服务。
/// 编辑窗口只提交完整话术，不直接修改数据库或驱动外部窗口。
struct AppState {
    updater: updater::Updater,
    data_directory: Mutex<data_directory::DataDirectory>,
    store: Mutex<Store>,
    device_store: Mutex<Store>,
    private_sync: Arc<private_sync::PrivateSync>,
    transfer: transfer::TransferState,
    delivery: Arc<delivery::DeliveryState>,
    native: Arc<NativeState>,
    teams: Arc<team::TeamClient>,
    sequence_generation: Mutex<u64>,
}
fn changed(app: &tauri::AppHandle, state: &AppState) -> Result<()> {
    refreshed(app, state)?;
    state.private_sync.wake();
    Ok(())
}
/// 远端落盘只刷新视图与绑定；再次唤醒同步会让空队列不断拉取快照。
fn refreshed(app: &tauri::AppHandle, state: &AppState) -> Result<()> {
    let mut library = {
        let store = state.store.lock().unwrap();
        let device = state.device_store.lock().unwrap();
        let settings = private_commands::combined_settings(&store, &device)?;
        private_sync::binding_library(&store, settings)?
    };
    if *state.native.settings.read().unwrap() != library.settings {
        if let Err(error) = state.native.apply_settings(&library.settings) {
            let store = state.store.lock().unwrap();
            if !private_local::bound(&store) {
                return Err(error);
            }
            // 系统占用快捷键时只保留本机生效配置，不清空已同步的云端配置，也不阻塞正文。
            let applied = state.native.settings.read().unwrap().clone();
            let mut value = serde_json::to_value(store.settings()?).map_err(store::db_error)?;
            let old = serde_json::to_value(&applied).map_err(store::db_error)?;
            for key in shanyu_team_model::private::SETTINGS {
                value[*key] = old[*key].clone();
            }
            store
                .conn
                .execute(
                    "UPDATE settings SET content=?1 WHERE id=1",
                    [value.to_string()],
                )
                .map_err(store::db_error)?;
            library = private_sync::binding_library(&store, applied)?;
            let _ = app.emit("native-error", error);
        }
    }
    state.native.update_bindings(&library)?;
    app.emit("library-changed", ())
        .map_err(|e| format!("界面刷新通知失败：{e}"))
}
fn shortcut_conflict(settings: &Settings, phrases: &[Phrase]) -> Result<()> {
    for p in phrases.iter().filter(|p| !p.shortcut.is_empty()) {
        let s = native::parse_shortcut(&p.shortcut)?;
        if settings.shortcut_reserved(&s) {
            return Err("话术快捷键与导航或已启用的序号快捷键冲突，请先修改。".into());
        }
    }
    Ok(())
}
#[tauri::command]
fn get_library(state: State<AppState>) -> Result<private_commands::ClientLibrary> {
    private_commands::library(&state)
}
#[tauri::command]
fn search_content(state: State<AppState>, query: String) -> Result<Vec<store::SearchHit>> {
    let store = state.store.lock().unwrap();
    let mut hits = store.search(&query)?;
    let device = state.device_store.lock().unwrap();
    if store.dir != device.dir {
        hits.extend(
            device
                .search(&query)?
                .into_iter()
                .filter(|hit| hit.kind == "file"),
        );
    }
    Ok(hits)
}
/// 编辑器确认快捷键只校验、不写库；完整保存在事务中再次检查，防止其他窗口并发修改。
#[tauri::command]
fn validate_phrase_shortcut(
    state: State<AppState>,
    id: String,
    shortcut: String,
) -> Result<String> {
    if shortcut.trim().is_empty() {
        return Ok(String::new());
    }
    let parsed = native::parse_shortcut(&shortcut)?;
    let canonical = parsed.canonical();
    let store = state.store.lock().unwrap();
    if let Some(conflict) = store::shortcut_collision(&store.conn, &id, &parsed)? {
        return Err(format!(
            "快捷键「{canonical}」与话术「{conflict}」冲突，请更换。"
        ));
    }
    let settings = state.native.settings.read().unwrap();
    if settings.shortcut_reserved(&parsed) {
        return Err("该快捷键与导航或已启用的序号快捷键冲突，请更换。".into());
    }
    Ok(canonical)
}
/// 搜索仅允许配置好的序号组合键；编辑、录入和模态输入暂停业务快捷键。
#[tauri::command]
fn set_shortcut_input(
    state: State<AppState>,
    window: tauri::Window,
    active: bool,
    search: Option<bool>,
) -> Result<()> {
    #[cfg(windows)]
    {
        let handle = window
            .hwnd()
            .map_err(|e| format!("无法暂停快捷键录入监听：{e}"))?
            .0 as i64;
        if active {
            state.native.shortcut_input.store(handle, Ordering::Release);
        } else {
            let _ = state.native.shortcut_input.compare_exchange(
                handle,
                0,
                Ordering::AcqRel,
                Ordering::Acquire,
            );
        }
        if active && search.unwrap_or(false) {
            state.native.search_input.store(handle, Ordering::Release);
        } else {
            let _ = state.native.search_input.compare_exchange(
                handle,
                0,
                Ordering::AcqRel,
                Ordering::Acquire,
            );
        }
        match window.label() {
            "company-files" => state
                .native
                .management_windows
                .company
                .store(handle, Ordering::Release),
            "settings" => state
                .native
                .management_windows
                .settings
                .store(handle, Ordering::Release),
            "team-management" => state
                .native
                .management_windows
                .teams
                .store(handle, Ordering::Release),
            "user-management" => state
                .native
                .management_windows
                .users
                .store(handle, Ordering::Release),
            _ => {}
        }
        if active && !search.unwrap_or(false) {
            state.native.cancel_candidates();
        }
    }
    Ok(())
}
#[tauri::command]
fn sync_sequence_map(
    state: State<AppState>,
    window: tauri::Window,
    ids: Vec<Option<String>>,
    loading: bool,
    generation: Option<u64>,
) -> Result<()> {
    if window.label() != "main" {
        return Err("序号列表只能由主窗口同步。".into());
    }
    let mut current = state.sequence_generation.lock().unwrap();
    if let Some(generation) = generation {
        if generation < *current {
            return Ok(());
        }
        *current = generation;
    }
    let ids = ids
        .into_iter()
        .take(10)
        .map(|id| {
            id.filter(|id| {
                if id.starts_with("member:") {
                    return state.teams.member_phrase(id, false).is_ok();
                }
                shanyu_team_model::ContentRef::from_key(id).is_none()
                    || state.teams.phrase(id, false).is_ok()
            })
        })
        .collect();
    *state.native.sequence_map.write().unwrap() = (ids, loading);
    Ok(())
}
#[tauri::command]
fn get_shortcut_candidates(state: State<AppState>) -> Option<native::CandidatePayload> {
    state.native.check_candidates();
    state.native.candidate_payload()
}
#[tauri::command]
fn select_shortcut_candidate(state: State<AppState>, index: usize) -> Result<()> {
    state.native.select_candidate(index)
}
#[tauri::command]
fn cancel_shortcut_candidates(state: State<AppState>) {
    state.native.cancel_candidates();
}
#[tauri::command]
fn resize_shortcut_candidates(state: State<AppState>) -> Result<()> {
    state.native.resize_candidates()
}

/// 原生候选提交复用普通话术发送链路；重新读取活动记录，删除或改绑的旧候选不会发送。
#[tauri::command]
async fn submit_shortcut_candidate(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    index: usize,
    mode: Mode,
) -> Result<DeliveryReport> {
    let payload = state
        .native
        .candidate_payload()
        .ok_or("快捷键候选已关闭。")?;
    // 先取走候选，再核对活动记录；已删除或改绑的候选失败后也能退出，长按不会重复提交。
    let (id, target) = state.native.take_candidate(index)?;
    {
        let store = state.store.lock().unwrap();
        let resolved = private_local::resolve_key(&store, &id, "phrase")?;
        let current = store.phrase(&resolved)?;
        if !native::parse_shortcut(&current.shortcut)
            .is_ok_and(|shortcut| shortcut.canonical() == payload.shortcut)
        {
            return Err("候选话术的快捷键已变化，请重新触发。".into());
        }
    }
    deliver_content(app, state, id, "phrase".into(), mode, target, None).await
}
pub(crate) fn queue_candidate_delivery(app: tauri::AppHandle, index: usize, mode: Mode) {
    tauri::async_runtime::spawn(async move {
        let result =
            submit_shortcut_candidate(app.clone(), app.state::<AppState>(), index, mode).await;
        shortcut_delivery_result(&app, result);
    });
}
pub(crate) fn queue_shortcut_delivery(
    app: tauri::AppHandle,
    id: String,
    mode: Mode,
    target: TargetContext,
) {
    tauri::async_runtime::spawn(async move {
        let result = deliver_content(
            app.clone(),
            app.state::<AppState>(),
            id,
            "phrase".into(),
            mode,
            target,
            None,
        )
        .await;
        shortcut_delivery_result(&app, result);
    });
}
fn shortcut_delivery_result(app: &tauri::AppHandle, result: Result<DeliveryReport>) {
    match result {
        Ok(report) => {
            let _ = app.emit("shortcut-delivery", &report);
        }
        Err(error) => {
            log::error!("快捷键提交失败：{error}");
            let _ = app.emit("native-error", error);
        }
    }
}
#[tauri::command]
fn set_common_dock_height(
    state: State<AppState>,
    window: tauri::Window,
    height: i32,
    above_height: Option<i32>,
) -> Result<()> {
    let above_height = above_height.unwrap_or(0);
    if window.label() != "common-dock"
        || !(28..=420).contains(&height)
        || !(0..=height - 28).contains(&above_height)
    {
        return Err("常用短语横条高度无效。".into());
    }
    *state.native.common_geometry.lock().unwrap() = (height, above_height);
    Ok(())
}
/// 浮层只接受列表窗口的悬停请求，内容从本地库按 ID 读取，前端不能伪造发送或修改数据。
#[tauri::command]
fn show_phrase_preview(
    state: State<AppState>,
    window: tauri::Window,
    id: String,
    anchor: native::PreviewAnchor,
) -> Result<()> {
    if !["main", "common-dock"].contains(&window.label()) {
        return Err("当前窗口不能打开话术预览。".into());
    }
    #[cfg(windows)]
    {
        let phrase = if id.starts_with("member:") {
            state.teams.member_phrase(&id, false)?
        } else if shanyu_team_model::ContentRef::from_key(&id).is_some() {
            state.teams.phrase(&id, false)?
        } else {
            let store = state.store.lock().unwrap();
            let id = private_local::resolve_key(&store, &id, "phrase")?;
            store.phrase(&id)?
        };
        state.native.show_preview(&window, phrase, anchor)
    }
    #[cfg(not(windows))]
    Err("话术预览仅支持 Windows。".into())
}
#[tauri::command]
fn get_phrase_preview(
    state: State<AppState>,
    window: tauri::Window,
) -> Result<Option<native::PreviewPayload>> {
    if window.label() != "phrase-preview" {
        return Err("当前窗口不能读取话术预览。".into());
    }
    #[cfg(windows)]
    return Ok(state.native.preview.lock().unwrap().payload.clone());
    #[cfg(not(windows))]
    Ok(None)
}
#[tauri::command]
fn resize_phrase_preview(
    state: State<AppState>,
    window: tauri::Window,
    id: String,
    height: f64,
) -> Result<()> {
    if window.label() != "phrase-preview" {
        return Err("当前窗口不能调整话术预览。".into());
    }
    #[cfg(windows)]
    return state.native.resize_preview(&id, height);
    #[cfg(not(windows))]
    Ok(())
}
#[tauri::command]
fn hide_phrase_preview(state: State<AppState>, id: Option<String>) {
    #[cfg(windows)]
    state.native.hide_preview(id.as_deref());
}
#[tauri::command]
fn save_phrase(
    app: tauri::AppHandle,
    state: State<AppState>,
    phrase: Phrase,
    after_id: Option<String>,
    expected_revision: Option<u64>,
    library_id: Option<String>,
) -> Result<String> {
    shortcut_conflict(
        &state.native.settings.read().unwrap(),
        std::slice::from_ref(&phrase),
    )?;
    let mut store = private_commands::active_store(&state, library_id.as_deref())?;
    if expected_revision.is_some_and(|v| {
        private_local::revision(&store, "phrase", &phrase.id).is_ok_and(|r| r != v)
    }) {
        return Err("话术已更新，请核对新版；当前草稿已保留。".into());
    }
    let id = store.save_phrase(phrase, after_id.as_deref())?;
    drop(store);
    changed(&app, &state)?;
    Ok(id)
}
#[tauri::command]
fn save_category(
    app: tauri::AppHandle,
    state: State<AppState>,
    category: Category,
    library_id: Option<String>,
) -> Result<String> {
    let id = if category.kind == "file" {
        state.device_store.lock().unwrap().save_category(category)?
    } else {
        private_commands::active_store(&state, library_id.as_deref())?.save_category(category)?
    };
    changed(&app, &state)?;
    Ok(id)
}
#[tauri::command]
fn save_common_phrase(
    app: tauri::AppHandle,
    state: State<AppState>,
    phrase: CommonPhrase,
    library_id: Option<String>,
    expected_revision: Option<u64>,
) -> Result<String> {
    let store = private_commands::active_store(&state, library_id.as_deref())?;
    if expected_revision
        .is_some_and(|rev| private_local::revision(&store, "common", &phrase.id).ok() != Some(rev))
    {
        return Err("短语已更新，请核对后重试；草稿已保留。".into());
    }
    let id = store.save_common_phrase(phrase)?;
    drop(store);
    changed(&app, &state)?;
    Ok(id)
}
#[tauri::command]
fn reorder_common_phrases(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
    library_id: Option<String>,
) -> Result<()> {
    private_commands::active_store(&state, library_id.as_deref())?.reorder_common(&ids)?;
    changed(&app, &state)
}
#[tauri::command]
fn delete_category(
    app: tauri::AppHandle,
    state: State<AppState>,
    id: String,
    keep_phrases: Option<bool>,
    target_id: Option<String>,
    recycle: Option<bool>,
    library_id: Option<String>,
) -> Result<()> {
    private_commands::category_store(&state, &id, library_id.as_deref())?.delete_category(
        &id,
        keep_phrases.unwrap_or(false),
        target_id.as_deref(),
        recycle.unwrap_or(true),
    )?;
    changed(&app, &state)
}
#[tauri::command]
fn move_copy_category(
    app: tauri::AppHandle,
    state: State<AppState>,
    id: String,
    target_id: String,
    copy: bool,
    library_id: Option<String>,
) -> Result<usize> {
    let count = private_commands::category_store(&state, &id, library_id.as_deref())?
        .move_copy_category(&id, &target_id, copy)?;
    changed(&app, &state)?;
    Ok(count)
}
#[tauri::command]
fn reorder_categories(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
    kind: String,
    parent_id: Option<String>,
    library_id: Option<String>,
) -> Result<()> {
    if kind == "file" {
        state
            .device_store
            .lock()
            .unwrap()
            .reorder_categories(&ids, &kind, parent_id.as_deref())?;
    } else {
        private_commands::active_store(&state, library_id.as_deref())?.reorder_categories(
            &ids,
            &kind,
            parent_id.as_deref(),
        )?;
    }
    changed(&app, &state)
}
#[tauri::command]
fn delete_item(
    app: tauri::AppHandle,
    state: State<AppState>,
    id: String,
    kind: String,
    recycle: Option<bool>,
    library_id: Option<String>,
) -> Result<()> {
    if kind == "file" {
        state
            .device_store
            .lock()
            .unwrap()
            .delete_item(&id, &kind, false)?;
    } else {
        private_commands::active_store(&state, library_id.as_deref())?.delete_item(
            &id,
            &kind,
            recycle.unwrap_or(true),
        )?;
    }
    changed(&app, &state)
}
#[tauri::command]
fn get_recycle_bin(
    state: State<AppState>,
    query: Option<String>,
    library_id: Option<String>,
) -> Result<Vec<RecycleItem>> {
    private_commands::active_store(&state, library_id.as_deref())?
        .recycle_items(query.as_deref().unwrap_or(""))
}

#[tauri::command]
fn delete_local_content(
    app: tauri::AppHandle,
    state: State<AppState>,
    kind: String,
    ids: Vec<String>,
    recycle: Option<bool>,
    library_id: Option<String>,
) -> Result<usize> {
    let count = if kind == "file" {
        state
            .device_store
            .lock()
            .unwrap()
            .delete_local_content(&kind, &ids, false)?
    } else {
        private_commands::active_store(&state, library_id.as_deref())?.delete_local_content(
            &kind,
            &ids,
            recycle.unwrap_or(true),
        )?
    };
    changed(&app, &state)?;
    Ok(count)
}
#[tauri::command]
fn get_recycle_detail(
    state: State<AppState>,
    id: String,
    library_id: Option<String>,
) -> Result<RecycleRecord> {
    private_commands::active_store(&state, library_id.as_deref())?.recycle_record(&id)
}

#[tauri::command]
fn restore_recycled(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
    without_shortcuts: Option<bool>,
    library_id: Option<String>,
) -> Result<usize> {
    let count = private_commands::active_store(&state, library_id.as_deref())?
        .restore_recycled(&ids, without_shortcuts.unwrap_or(false))?;
    changed(&app, &state)?;
    Ok(count)
}
#[tauri::command]
fn remove_recycled(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
    library_id: Option<String>,
) -> Result<()> {
    private_commands::active_store(&state, library_id.as_deref())?.remove_recycled(&ids)?;
    changed(&app, &state)
}
#[tauri::command]
fn empty_recycle(
    app: tauri::AppHandle,
    state: State<AppState>,
    library_id: Option<String>,
    ids: Option<Vec<String>>,
) -> Result<usize> {
    let count = {
        let mut store = private_commands::active_store(&state, library_id.as_deref())?;
        let ids = match ids {
            Some(ids) => ids,
            None => store.recycle_records()?.into_iter().map(|r| r.id).collect(),
        };
        store.clear_recycle_records(&ids)?
    };
    changed(&app, &state)?;
    Ok(count)
}

#[tauri::command]
fn rename_linked_file(
    app: tauri::AppHandle,
    state: State<AppState>,
    id: String,
    name: String,
) -> Result<()> {
    state.device_store.lock().unwrap().rename_file(&id, &name)?;
    changed(&app, &state)
}
#[tauri::command]
fn move_linked_files(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
    category_id: String,
) -> Result<()> {
    state
        .device_store
        .lock()
        .unwrap()
        .move_files(&ids, &category_id)?;
    changed(&app, &state)
}
#[tauri::command]
fn remove_linked_files(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
) -> Result<()> {
    state.device_store.lock().unwrap().remove_files(&ids)?;
    changed(&app, &state)
}
#[tauri::command]
fn reorder_files(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
    category_id: String,
) -> Result<()> {
    state
        .device_store
        .lock()
        .unwrap()
        .reorder_files(&ids, &category_id)?;
    changed(&app, &state)
}
#[tauri::command]
fn copy_linked_files(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
) -> Result<()> {
    let _guard = state.delivery.acquire()?;
    let paths = state
        .device_store
        .lock()
        .unwrap()
        .linked_files(&ids)?
        .into_iter()
        .map(|f| f.path)
        .collect::<Vec<_>>();
    let owner = app
        .get_webview_window("main")
        .ok_or("主窗口不存在。")?
        .hwnd()
        .map_err(|e| e.to_string())?
        .0 as i64;
    native::copy_files(owner, &paths)
}
#[tauri::command]
fn open_linked_file(state: State<AppState>, id: String, reveal: Option<bool>) -> Result<()> {
    let files = state.device_store.lock().unwrap().linked_files(&[id])?;
    native::open_file(&files[0].path, reveal.unwrap_or(false))
}
#[tauri::command]
fn move_copy_phrase(
    app: tauri::AppHandle,
    state: State<AppState>,
    id: String,
    root_id: String,
    group_id: Option<String>,
    copy: bool,
    library_id: Option<String>,
) -> Result<String> {
    let id = private_commands::active_store(&state, library_id.as_deref())?
        .move_copy(&id, root_id, group_id, copy)?;
    changed(&app, &state)?;
    Ok(id)
}
#[tauri::command]
fn sort_item(
    app: tauri::AppHandle,
    state: State<AppState>,
    id: String,
    kind: String,
    position: usize,
    library_id: Option<String>,
) -> Result<()> {
    if kind == "file" {
        state
            .device_store
            .lock()
            .unwrap()
            .sort(&id, &kind, position)?;
    } else {
        private_commands::active_store(&state, library_id.as_deref())?
            .sort(&id, &kind, position)?;
    }
    changed(&app, &state)
}
#[tauri::command]
fn add_files(
    app: tauri::AppHandle,
    state: State<AppState>,
    category_id: String,
    paths: Vec<String>,
) -> Result<()> {
    if paths.is_empty() {
        return Err("请先选择文件。".into());
    }
    state
        .device_store
        .lock()
        .unwrap()
        .add_files(&category_id, paths)?;
    changed(&app, &state)
}
#[tauri::command]
fn relink_file(
    app: tauri::AppHandle,
    state: State<AppState>,
    id: String,
    path: String,
    category_id: String,
) -> Result<()> {
    state
        .device_store
        .lock()
        .unwrap()
        .relink(&id, &path, &category_id)?;
    changed(&app, &state)
}
#[tauri::command(async)]
fn import_images(
    state: State<AppState>,
    paths: Vec<String>,
    library_id: Option<String>,
) -> Result<Vec<Block>> {
    let dir = private_commands::active_store(&state, library_id.as_deref())?
        .dir
        .clone();
    backup::import_images(&dir, &paths)
}
#[tauri::command(async)]
fn import_pasted_images(
    state: State<AppState>,
    images: Vec<Vec<u8>>,
    library_id: Option<String>,
) -> Result<Vec<Block>> {
    let dir = private_commands::active_store(&state, library_id.as_deref())?
        .dir
        .clone();
    backup::import_pasted_images(&dir, &images)
}
#[tauri::command(async)]
fn get_file_icon(state: State<AppState>, path: String) -> Result<String> {
    native::file_icon(&path, &state.store.lock().unwrap().dir)
}
#[tauri::command]
fn get_target(state: State<AppState>) -> Option<TargetContext> {
    state.native.target.lock().unwrap().clone()
}
#[tauri::command]
fn native_status(state: State<AppState>) -> Option<String> {
    state.native.error.lock().unwrap().clone()
}

#[tauri::command]
async fn deliver_content(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    id: String,
    kind: String,
    mode: Mode,
    target: TargetContext,
    trigger_key: Option<u32>,
) -> Result<DeliveryReport> {
    let guard = state.delivery.acquire()?;
    native::consume_delivery_key(trigger_key);
    if state.native.target.lock().unwrap().as_ref() != Some(&target) {
        return Err("目标上下文已变化，请重新聚焦输入区后返回闪语。".into());
    }
    let shared = if kind == "phrase" && id.starts_with("member:") {
        Some(state.teams.check_member_delivery(&app, &id).await?)
    } else if kind == "phrase" && shanyu_team_model::ContentRef::from_key(&id).is_some() {
        Some(state.teams.check_delivery(&app, &id).await?)
    } else {
        None
    };
    let (settings, segments) = {
        let store = state.store.lock().unwrap();
        let settings = store.settings()?;
        let private_id = if shared.is_none() && kind == "phrase" {
            Some(private_local::resolve_key(&store, &id, "phrase")?)
        } else {
            None
        };
        let segments = match kind.as_str() {
            "phrase" => match shared {
                Some(phrase) => phrase.segments,
                None => {
                    let phrase = store.phrase(private_id.as_deref().unwrap())?;
                    if !private_local::materials_ready(&store, &phrase) {
                        return Err("素材未缓存，请下载后重新触发。".into());
                    }
                    phrase.segments
                }
            },
            "common" => {
                let id = private_local::resolve_key(&store, &id, "common")?;
                let phrase = store
                    .common_phrases()?
                    .into_iter()
                    .find(|p| p.id == id)
                    .ok_or("常用短语已不存在。")?;
                vec![Segment {
                    blocks: vec![Block::Text { text: phrase.text }],
                }]
            }
            "file" => {
                let file = state
                    .device_store
                    .lock()
                    .unwrap()
                    .library()?
                    .files
                    .into_iter()
                    .find(|f| f.id == id)
                    .ok_or("文件关联已不存在。")?;
                vec![Segment {
                    blocks: vec![Block::File {
                        path: file.path,
                        name: file.name,
                    }],
                }]
            }
            _ => return Err("内容类型无效。".into()),
        };
        (settings, segments)
    };
    let owner = app
        .get_webview_window("main")
        .ok_or("主窗口已关闭。")?
        .hwnd()
        .map_err(|e| e.to_string())?
        .0 as i64;
    let cancelled = state.delivery.cancelled.clone();
    let _ = app.emit("delivery-busy", true);
    let event_app = app.clone();
    let result = tauri::async_runtime::spawn_blocking(move || {
        let _guard = guard;
        let mut driver = native::WindowsDriver::new(owner, target, cancelled.clone());
        delivery::execute(&segments, mode, &settings, &mut driver, &cancelled)
    })
    .await
    .map_err(|e| format!("发送任务无法完成：{e}"));
    let _ = event_app.emit("delivery-busy", false);
    state.teams.end_delivery();
    if let Ok(report) = &result {
        if let Some(e) = &report.error {
            log::error!("发送中断：{e}");
        }
    }
    result
}
#[tauri::command]
fn cancel_delivery(state: State<AppState>) {
    state.delivery.cancelled.store(true, Ordering::Release);
}
#[tauri::command]
fn copy_phrase_text(app: tauri::AppHandle, state: State<AppState>, id: String) -> Result<()> {
    let _guard = state.delivery.acquire()?;
    let text = if id.starts_with("member:") {
        state.teams.member_phrase(&id, false)?.text()
    } else if shanyu_team_model::ContentRef::from_key(&id).is_some() {
        state.teams.phrase(&id, false)?.text()
    } else {
        let store = state.store.lock().unwrap();
        let id = private_local::resolve_key(&store, &id, "phrase")?;
        store.phrase(&id)?.text()
    };
    let owner = app
        .get_webview_window("main")
        .ok_or("主窗口不存在。")?
        .hwnd()
        .map_err(|e| e.to_string())?
        .0 as i64;
    native::copy_text(owner, &text)
}
#[tauri::command]
fn save_settings(
    app: tauri::AppHandle,
    state: State<AppState>,
    settings: Settings,
    library_id: Option<String>,
) -> Result<()> {
    let store = private_commands::active_store(&state, library_id.as_deref())?;
    let old = store.settings()?;
    settings.validate()?;
    let mut reserved = settings.clone();
    if settings.sequence_bindings != old.sequence_bindings {
        reserved.sequence_hotkeys = true;
    }
    shortcut_conflict(&reserved, &store.library()?.phrases)?;
    state.native.apply_settings(&settings)?;
    if let Err(e) = store.save_settings(&settings) {
        let _ = state.native.apply_settings(&old);
        return Err(e);
    }
    {
        let device = state.device_store.lock().unwrap();
        if device.dir != store.dir {
            let mut values = serde_json::to_value(device.settings()?).map_err(store::db_error)?;
            let incoming = serde_json::to_value(&settings).map_err(store::db_error)?;
            for (key, value) in incoming.as_object().unwrap() {
                if !shanyu_team_model::private::SETTINGS.contains(&key.as_str()) {
                    values[key] = value.clone();
                }
            }
            let device_settings: Settings =
                serde_json::from_value(values).map_err(store::db_error)?;
            device.save_settings(&device_settings)?;
        }
    }
    drop(store);
    changed(&app, &state)
}
#[tauri::command(async)]
fn export_template(path: String) -> Result<()> {
    backup::excel_template(Path::new(&path))
}
#[tauri::command]
fn cache_info(state: State<AppState>) -> u64 {
    let dir = state.store.lock().unwrap().dir.clone();
    ["thumbnails", "temp"]
        .iter()
        .flat_map(|name| fs::read_dir(dir.join(name)).into_iter().flatten())
        .filter_map(|e| e.ok())
        .filter_map(|e| e.metadata().ok())
        .filter(|m| m.is_file())
        .map(|m| m.len())
        .sum()
}
#[tauri::command(async)]
fn clear_cache(state: State<AppState>, kind: String) -> Result<()> {
    if !["thumbnails", "temp"].contains(&kind.as_str()) {
        return Err("只能清理缩略图和临时文件。".into());
    }
    let root = state
        .store
        .lock()
        .unwrap()
        .dir
        .canonicalize()
        .map_err(|e| e.to_string())?;
    let path = root.join(&kind);
    if path.canonicalize().map_err(|e| e.to_string())? != path {
        return Err("缓存目录指向了其他位置，未执行清理。".into());
    }
    fs::remove_dir_all(&path).map_err(|e| format!("缓存清理失败：{e}"))?;
    fs::create_dir_all(path).map_err(|e| format!("缓存目录无法重建：{e}"))?;
    Ok(())
}
#[tauri::command]
fn open_directory(state: State<AppState>, kind: String) -> Result<()> {
    let path = if kind == "cache" {
        state.store.lock().unwrap().dir.join("thumbnails")
    } else {
        state.data_directory.lock().unwrap().root()
    };
    std::process::Command::new("explorer.exe")
        .arg(path)
        .spawn()
        .map_err(|e| format!("文件夹无法打开：{e}"))?;
    Ok(())
}
#[tauri::command]
fn get_data_directory(state: State<AppState>) -> data_directory::DirectoryInfo {
    state.data_directory.lock().unwrap().info()
}
/// 更换只保存离线迁移请求；未保存的窗口草稿及主动系统操作完成后才允许重启。
#[tauri::command(async)]
fn change_data_directory(
    app: tauri::AppHandle,
    state: State<AppState>,
    path: String,
) -> Result<()> {
    let _activity = update_activity::Activity::enter()?;
    if app.webview_windows().keys().any(|label| {
        [
            "editor",
            "file-editor",
            "common-editor",
            "common-sort",
            "category-sort",
            "file-sort",
            "team-management",
            "user-management",
            "delete-confirm",
        ]
        .contains(&label.as_str())
    }) {
        return Err("请先保存并关闭编辑、排序或管理窗口。".into());
    }
    if state.delivery.busy.load(Ordering::Acquire)
        || state.transfer.is_active()
        || state.teams.has_active_company_transfers()
    {
        return Err("请等待发送、导入导出和文件传输完成。".into());
    }
    state
        .data_directory
        .lock()
        .unwrap()
        .prepare(Path::new(&path))?;
    app.request_restart();
    Ok(())
}
#[tauri::command]
fn get_minimized_float_theme(state: State<AppState>) -> String {
    state.native.settings.read().unwrap().theme.clone()
}
#[tauri::command]
fn window_action(app: tauri::AppHandle, state: State<AppState>, action: String) -> Result<()> {
    match action.as_str() {
        "hide" => state.native.hide(),
        "show" => state.native.show("show"),
        "start-using" => {
            if let Some(guide) = app.get_webview_window("usage-guide") {
                let native = state.native.clone();
                // 关闭前台窗口会触发 Windows 焦点还原；销毁后再唤起，避免还原覆盖主窗口激活。
                guide.on_window_event(move |event| {
                    if matches!(event, tauri::WindowEvent::Destroyed) {
                        native.show("show");
                    }
                });
                guide.close().map_err(|_| "使用指引暂时无法关闭。")?;
            } else {
                state.native.show("show");
            }
        }
        "minimize" => state.native.minimize(),
        "exit" => {
            state.native.persist_float_position();
            state.teams.stop_company_uploads();
            state.native.running.store(false, Ordering::Release);
            app.exit(0);
        }
        _ => return Err("窗口操作无效。".into()),
    }
    Ok(())
}
#[tauri::command]
fn report_error(message: String) {
    log::error!(
        "客户端错误：{}",
        message.chars().take(2000).collect::<String>()
    );
}

/// 日志写在用户数据目录，提供中文错误上下文，升级和卸载不移除用户日志。
struct FileLogger(Mutex<fs::File>);
impl log::Log for FileLogger {
    fn enabled(&self, m: &log::Metadata<'_>) -> bool {
        m.level() <= log::Level::Info
    }
    fn log(&self, r: &log::Record<'_>) {
        if self.enabled(r.metadata()) {
            let seconds = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_secs();
            if let Ok(mut file) = self.0.lock() {
                let _ = writeln!(file, "{seconds} [{}] {}", r.level(), r.args());
            }
        }
    }
    fn flush(&self) {
        if let Ok(mut f) = self.0.lock() {
            let _ = f.flush();
        }
    }
}

pub fn run() {
    #[allow(unused_mut)]
    let mut context = tauri::generate_context!();
    // Debug 验收使用独立单实例标识与用户数据，避免接管正在运行的正式客户端。
    #[cfg(debug_assertions)]
    if std::env::var_os("SHANYU_TEST_DATA_DIR").is_some() {
        context.config_mut().identifier.push_str(".qa");
    }
    // 显式使用稳定的 Windows 应用身份，让快捷方式、任务栏和通知识别同一产品。
    // 启动器造成的任务管理器父子分组仍由 Windows 决定，不为显示分组重启进程。
    #[cfg(windows)]
    {
        let identity: Vec<u16> = context
            .config()
            .identifier
            .encode_utf16()
            .chain(Some(0))
            .collect();
        if let Err(error) = unsafe {
            windows::Win32::UI::Shell::SetCurrentProcessExplicitAppUserModelID(
                windows::core::PCWSTR(identity.as_ptr()),
            )
        } {
            eprintln!("Windows 应用标识设置失败：{error}");
        }
    }
    let launch = Arc::new(startup::LaunchIntent::new(
        !std::env::args().any(|arg| arg == "--autostart"),
    ));
    let duplicate_launch = launch.clone();
    let page_launch = launch.clone();
    tauri::Builder::default()
        .manage(launch.clone())
        // 重复手动启动复用现有窗口；自启不能解除用户的手动隐藏。
        .plugin(tauri_plugin_single_instance::init(move |app, args, _| {
            if !args.iter().any(|arg| arg == "--autostart") {
                duplicate_launch.request_show();
                startup::dispatch(app, &duplicate_launch);
            }
        }))
        .plugin(tauri_plugin_dialog::init())
        .on_page_load(move |_webview, payload| {
            #[cfg(windows)]
            native::square_window_corners(&_webview.window());
            if _webview.label() == "main"
                && payload.event() == tauri::webview::PageLoadEvent::Finished
            {
                page_launch.page_ready();
                startup::dispatch(_webview.app_handle(), &page_launch);
            }
        })
        .setup(move |app| {
            let dir = app.path().app_local_data_dir()?;
            #[cfg(debug_assertions)]
            let dir = std::env::var_os("SHANYU_TEST_DATA_DIR")
                .map(std::path::PathBuf::from)
                .unwrap_or(dir);
            let mut data_directory =
                data_directory::DataDirectory::startup(&dir).map_err(std::io::Error::other)?;
            let dir = data_directory.root();
            let fresh = !dir.join("shanyu.sqlite3").exists();
            let device_store = Store::open(&dir).map_err(std::io::Error::other)?;
            startup::initialize_guide(&device_store, fresh).map_err(std::io::Error::other)?;
            let private_sync = private_sync::PrivateSync::new(&dir);
            let store = private_sync
                .startup(&device_store)
                .map_err(std::io::Error::other)?;
            let settings = private_commands::combined_settings(&store, &device_store)
                .map_err(std::io::Error::other)?;
            app.asset_protocol_scope()
                .allow_directory(dir.join("images"), true)?;
            app.asset_protocol_scope()
                .allow_directory(dir.join("thumbnails"), true)?;
            app.asset_protocol_scope()
                .allow_directory(dir.join("private"), true)?;
            if let Ok(file) = fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(dir.join("shanyu.log"))
            {
                let _ = log::set_boxed_logger(Box::new(FileLogger(Mutex::new(file))));
                log::set_max_level(log::LevelFilter::Info);
            }
            let delivery = Arc::new(delivery::DeliveryState::default());
            let native = Arc::new(NativeState::new(
                settings.clone(),
                delivery.busy.clone(),
                delivery.cancelled.clone(),
            ));
            native
                .update_bindings(
                    &private_sync::binding_library(&store, settings.clone())
                        .map_err(std::io::Error::other)?,
                )
                .map_err(std::io::Error::other)?;
            let teams = team::TeamClient::new(&dir).map_err(std::io::Error::other)?;
            data_directory
                .finish_cleanup()
                .map_err(std::io::Error::other)?;
            let show_directory_result = data_directory.show_result;
            let updater = updater::Updater::new(&device_store);
            app.manage(AppState {
                updater,
                data_directory: Mutex::new(data_directory),
                store: Mutex::new(store),
                device_store: Mutex::new(device_store),
                private_sync: private_sync.clone(),
                transfer: transfer::TransferState::default(),
                delivery,
                native: native.clone(),
                teams,
                sequence_generation: Mutex::new(0),
            });
            updater::start_background(app.handle().clone());
            tauri::WebviewWindowBuilder::new(
                app,
                "common-dock",
                tauri::WebviewUrl::App("index.html#common-dock".into()),
            )
            .title("常用短语 · 闪语")
            .inner_size(600.0, 34.0)
            .decorations(false)
            .resizable(false)
            .skip_taskbar(true)
            .focused(false)
            .visible(false)
            // Rust 创建的常驻窗口也关闭表单建议，与主窗口和动态编辑窗口一致。
            .general_autofill_enabled(false)
            .build()?;
            // 无激活浮层可以滚动和关闭，查看正文不会替换当前聊天输入目标。
            tauri::WebviewWindowBuilder::new(
                app,
                "phrase-preview",
                tauri::WebviewUrl::App("index.html#phrase-preview".into()),
            )
            .title("话术预览 · 闪语")
            .inner_size(300.0, 160.0)
            .decorations(false)
            .shadow(false)
            .resizable(false)
            .skip_taskbar(true)
            .focused(false)
            .focusable(false)
            .always_on_top(true)
            .visible(false)
            .general_autofill_enabled(false)
            .build()?;
            tauri::WebviewWindowBuilder::new(
                app,
                "shortcut-candidates",
                tauri::WebviewUrl::App("index.html#shortcut-candidates".into()),
            )
            .title("快捷键候选 · 闪语")
            .inner_size(370.0, 84.0)
            .decorations(false)
            .shadow(false)
            .resizable(false)
            .skip_taskbar(true)
            .focused(false)
            .focusable(false)
            .always_on_top(true)
            .visible(false)
            .general_autofill_enabled(false)
            .build()?;
            // 恢复小条预先创建，首次最小化直接显示；页面独立于话术库与输入监测。
            tauri::WebviewWindowBuilder::new(
                app,
                "minimized-float",
                tauri::WebviewUrl::App("index.html#minimized-float".into()),
            )
            .title("悬浮窗 · 闪语")
            .inner_size(96.0, 40.0)
            .decorations(false)
            .shadow(false)
            .resizable(false)
            .maximizable(false)
            .minimizable(false)
            .closable(false)
            .skip_taskbar(true)
            .focused(false)
            .focusable(false)
            .always_on_top(true)
            .visible(false)
            .general_autofill_enabled(false)
            .build()?;
            native.start(app.handle()).map_err(std::io::Error::other)?;
            team_commands::start(app.handle());
            private_sync.start(app.handle().clone());
            for label in ["main", "common-dock"] {
                if let Some(window) = app.get_webview_window(label) {
                    native::install_webview_shortcuts(&window, native.clone())
                        .map_err(std::io::Error::other)?;
                }
            }
            use tauri::{
                menu::{Menu, MenuItem},
                tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
            };
            let items = [
                MenuItem::with_id(app, "toggle", "显示／隐藏闪语", true, None::<&str>)?,
                MenuItem::with_id(app, "search", "搜索话术", true, None::<&str>)?,
                MenuItem::with_id(app, "settings", "设置", true, None::<&str>)?,
                MenuItem::with_id(app, "exit", "退出闪语", true, None::<&str>)?,
            ];
            let menu = Menu::with_items(
                app,
                &items
                    .iter()
                    .map(|i| i as &dyn tauri::menu::IsMenuItem<_>)
                    .collect::<Vec<_>>(),
            )?;
            let mut tray = TrayIconBuilder::with_id("main-tray")
                .tooltip("闪语")
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_menu_event(|app, event| {
                    let state = app.state::<AppState>();
                    match event.id.as_ref() {
                        "toggle" => state.native.toggle(),
                        "search" => state.native.show("search"),
                        "settings" => state.native.show("settings"),
                        "exit" => {
                            state.native.persist_float_position();
                            state.teams.stop_company_uploads();
                            state.native.running.store(false, Ordering::Release);
                            app.exit(0);
                        }
                        _ => {}
                    }
                })
                .on_tray_icon_event(|tray, event| {
                    if matches!(
                        event,
                        TrayIconEvent::Click {
                            button: MouseButton::Left,
                            button_state: MouseButtonState::Up,
                            ..
                        }
                    ) {
                        tray.app_handle().state::<AppState>().native.toggle();
                    }
                });
            if let Some(icon) = app.default_window_icon() {
                tray = tray.icon(icon.clone());
            }
            tray.build(app)?;
            if let Some(window) = app.get_webview_window("main") {
                window.set_always_on_top(settings.pinned)?;
                window.set_resizable(settings.free_resize)?;
                // 主窗口为窄侧栏，配置中禁用最大化，避免标题栏双击与吸附定位分离外框和 WebView 尺寸。
                // 保留独立编辑／排序窗口的最大化能力，以及主窗口的拖动、拉宽和最小化。
                // 自启只跟随；手动启动通过同一原生队列恢复并显示，无目标时独立打开。
            }
            launch.services_ready();
            startup::dispatch(app.handle(), &launch);
            log::info!("闪语已启动，用户数据目录：{}", dir.display());
            if show_directory_result {
                let window = tauri::WebviewWindowBuilder::new(
                    app,
                    "settings",
                    tauri::WebviewUrl::App("index.html#settings?tab=%E5%85%B3%E4%BA%8E".into()),
                )
                .title("设置 · 闪语")
                .inner_size(594.0, 620.0)
                .min_inner_size(480.0, 480.0)
                .decorations(false)
                .center()
                .general_autofill_enabled(false);
                #[cfg(debug_assertions)]
                let window = if std::env::var_os("SHANYU_TEST_DATA_DIR").is_some() {
                    window.visible(false).focused(false)
                } else {
                    window
                };
                window.build()?;
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            if let Some(state) = window.try_state::<AppState>() {
                let management_window = match window.label() {
                    "company-files" => Some(&state.native.management_windows.company),
                    "settings" => Some(&state.native.management_windows.settings),
                    "team-management" => Some(&state.native.management_windows.teams),
                    "user-management" => Some(&state.native.management_windows.users),
                    _ => None,
                };
                if let Some(management_window) = management_window {
                    if matches!(event, tauri::WindowEvent::Destroyed) {
                        let handle = management_window.swap(0, Ordering::AcqRel);
                        let _ = state.native.shortcut_input.compare_exchange(
                            handle,
                            0,
                            Ordering::AcqRel,
                            Ordering::Acquire,
                        );
                        let _ = state.native.search_input.compare_exchange(
                            handle,
                            0,
                            Ordering::AcqRel,
                            Ordering::Acquire,
                        );
                    } else if let Ok(handle) = window.hwnd() {
                        management_window.store(handle.0 as i64, Ordering::Release);
                    }
                }
            }
            if window.label() == "main" {
                if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                    let state = window.state::<AppState>();
                    if state.native.settings.read().unwrap().tray {
                        api.prevent_close();
                        state.native.hide();
                    }
                }
            }
        })
        .invoke_handler(|invoke| {
            #[cfg(debug_assertions)]
            if invoke.message.command() == "qa_load_update" {
                let handler: fn(tauri::ipc::Invoke<tauri::Wry>) -> bool =
                    tauri::generate_handler![updater::qa_load_update];
                return handler(invoke);
            }
            let handler: fn(tauri::ipc::Invoke<tauri::Wry>) -> bool = tauri::generate_handler![
                updater::get_update_status,
                updater::save_update_config,
                updater::check_update,
                updater::download_update,
                updater::cancel_update,
                updater::install_update,
                updater::ensure_update_idle,
                search_history::get_search_history,
                search_history::record_search_history,
                search_history::clear_search_history,
                startup::get_usage_guide_state,
                startup::mark_usage_guide_shown,
                company_commands::get_company_files,
                company_commands::prepare_company_upload,
                company_commands::enqueue_company_uploads,
                company_commands::upload_company_file,
                company_commands::query_company_upload,
                company_commands::get_company_transfers,
                company_commands::clear_company_transfers,
                company_commands::download_company_file,
                company_commands::cancel_company_transfer,
                company_commands::manage_company_file,
                company_commands::save_company_private_phrase,
                company_commands::save_company_file_as,
                private_commands::get_private_status,
                private_commands::get_private_settings,
                private_commands::preview_private_sync,
                private_commands::confirm_private_sync,
                private_commands::cancel_private_sync_preview,
                private_commands::pause_private_sync,
                private_commands::get_private_conflicts,
                private_commands::resolve_private_conflict,
                private_commands::apply_private_settings,
                private_commands::download_private_materials,
                private_commands::backup_private_server,
                team_commands::get_team_status,
                team_commands::get_team_view,
                team_commands::team_login,
                team_commands::team_logout,
                team_commands::team_refresh,
                team_commands::team_change_password,
                team_commands::save_team_phrase,
                team_commands::save_team_category,
                team_commands::delete_team_content,
                team_commands::get_team_recycle,
                team_commands::restore_team_record,
                team_commands::purge_team_record,
                team_commands::empty_team_recycle,
                team_commands::prepare_team_phrase,
                team_commands::preload_team_images,
                team_commands::copy_team_to_private,
                team_commands::publish_private_to_team,
                team_commands::team_admin,
                team_commands::backup_team_server,
                team_commands::search_scoped_content,
                member_commands::get_member_view,
                member_commands::get_member_overview,
                member_commands::save_member_share,
                member_commands::set_member_subscription,
                member_commands::download_member_materials,
                get_library,
                search_content,
                save_phrase,
                validate_phrase_shortcut,
                set_shortcut_input,
                sync_sequence_map,
                get_minimized_float_theme,
                get_shortcut_candidates,
                select_shortcut_candidate,
                submit_shortcut_candidate,
                cancel_shortcut_candidates,
                resize_shortcut_candidates,
                set_common_dock_height,
                show_phrase_preview,
                get_phrase_preview,
                resize_phrase_preview,
                hide_phrase_preview,
                save_category,
                save_common_phrase,
                reorder_common_phrases,
                delete_category,
                move_copy_category,
                reorder_categories,
                delete_item,
                delete_local_content,
                get_recycle_bin,
                get_recycle_detail,
                restore_recycled,
                remove_recycled,
                empty_recycle,
                rename_linked_file,
                move_linked_files,
                remove_linked_files,
                reorder_files,
                copy_linked_files,
                open_linked_file,
                move_copy_phrase,
                sort_item,
                add_files,
                relink_file,
                import_images,
                import_pasted_images,
                get_file_icon,
                get_target,
                native_status,
                deliver_content,
                cancel_delivery,
                copy_phrase_text,
                save_settings,
                transfer::preview_import,
                transfer::has_legacy_private_content,
                transfer::preview_legacy_private_content,
                transfer::confirm_import,
                transfer::export_data,
                transfer::cancel_data_transfer,
                transfer::get_import_pending,
                transfer::discard_import,
                transfer::check_import_result,
                transfer::refresh_import_preview,
                export_template,
                cache_info,
                clear_cache,
                open_directory,
                get_data_directory,
                change_data_directory,
                window_action,
                report_error
            ];
            handler(invoke)
        })
        .run(context)
        .unwrap_or_else(|error| {
            native::startup_error(&format!(
                "闪语启动失败：{error}\n请检查 WebView2 运行时及用户数据目录权限。"
            ));
            std::process::exit(1);
        });
}
