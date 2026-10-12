mod backup;
mod delivery;
mod file_library;
mod model;
mod native;
mod recycle;
mod store;

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
    store: Mutex<Store>,
    pending: Mutex<Option<backup::PendingImport>>,
    delivery: Arc<delivery::DeliveryState>,
    native: Arc<NativeState>,
}
fn changed(app: &tauri::AppHandle, state: &AppState) -> Result<()> {
    let library = state.store.lock().unwrap().library()?;
    state.native.update_bindings(&library)?;
    app.emit("library-changed", ())
        .map_err(|e| format!("界面刷新通知失败：{e}"))
}
fn shortcut_conflict(settings: &Settings, phrases: &[Phrase]) -> Result<()> {
    for p in phrases.iter().filter(|p| !p.shortcut.is_empty()) {
        let s = native::parse_shortcut(&p.shortcut)?;
        if settings.shortcut_reserved(&s) {
            return Err(format!(
                "话术「{}」的快捷键与导航或已启用的序号快捷键冲突，请先修改。",
                p.label()
            ));
        }
    }
    Ok(())
}
#[tauri::command]
fn get_library(state: State<AppState>) -> Result<Library> {
    state.store.lock().unwrap().library()
}
#[tauri::command]
fn search_content(state: State<AppState>, query: String) -> Result<Vec<store::SearchHit>> {
    state.store.lock().unwrap().search(&query)
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
        if window.label() == "settings" {
            state
                .native
                .settings_window
                .store(handle, Ordering::Release);
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
) -> Result<()> {
    if window.label() != "main" {
        return Err("序号列表只能由主窗口同步。".into());
    }
    *state.native.sequence_map.write().unwrap() = (ids.into_iter().take(10).collect(), loading);
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
        let current = state.store.lock().unwrap().phrase(&id)?;
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
) -> Result<()> {
    if window.label() != "common-dock" || !(28..=420).contains(&height) {
        return Err("常用短语横条高度无效。".into());
    }
    state.native.common_height.store(height, Ordering::Release);
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
        let phrase = state.store.lock().unwrap().phrase(&id)?;
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
) -> Result<String> {
    shortcut_conflict(
        &state.native.settings.read().unwrap(),
        std::slice::from_ref(&phrase),
    )?;
    let id = state
        .store
        .lock()
        .unwrap()
        .save_phrase(phrase, after_id.as_deref())?;
    changed(&app, &state)?;
    Ok(id)
}
#[tauri::command]
fn save_category(
    app: tauri::AppHandle,
    state: State<AppState>,
    category: Category,
) -> Result<String> {
    let id = state.store.lock().unwrap().save_category(category)?;
    changed(&app, &state)?;
    Ok(id)
}
#[tauri::command]
fn save_common_phrase(
    app: tauri::AppHandle,
    state: State<AppState>,
    phrase: CommonPhrase,
) -> Result<String> {
    let id = state.store.lock().unwrap().save_common_phrase(phrase)?;
    changed(&app, &state)?;
    Ok(id)
}
#[tauri::command]
fn reorder_common_phrases(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
) -> Result<()> {
    state.store.lock().unwrap().reorder_common(&ids)?;
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
) -> Result<()> {
    state.store.lock().unwrap().delete_category(
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
) -> Result<usize> {
    let count = state
        .store
        .lock()
        .unwrap()
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
) -> Result<()> {
    state
        .store
        .lock()
        .unwrap()
        .reorder_categories(&ids, &kind, parent_id.as_deref())?;
    changed(&app, &state)
}
#[tauri::command]
fn delete_item(
    app: tauri::AppHandle,
    state: State<AppState>,
    id: String,
    kind: String,
    recycle: Option<bool>,
) -> Result<()> {
    state
        .store
        .lock()
        .unwrap()
        .delete_item(&id, &kind, recycle.unwrap_or(true))?;
    changed(&app, &state)
}
#[tauri::command]
fn get_recycle_bin(state: State<AppState>, query: Option<String>) -> Result<Vec<RecycleItem>> {
    state
        .store
        .lock()
        .unwrap()
        .recycle_items(query.as_deref().unwrap_or(""))
}
#[tauri::command]
fn delete_local_content(
    app: tauri::AppHandle,
    state: State<AppState>,
    kind: String,
    ids: Vec<String>,
    recycle: Option<bool>,
) -> Result<usize> {
    let count =
        state
            .store
            .lock()
            .unwrap()
            .delete_local_content(&kind, &ids, recycle.unwrap_or(true))?;
    changed(&app, &state)?;
    Ok(count)
}
#[tauri::command]
fn get_recycle_detail(state: State<AppState>, id: String) -> Result<RecycleRecord> {
    state.store.lock().unwrap().recycle_record(&id)
}
#[tauri::command]
fn restore_recycled(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
    without_shortcuts: Option<bool>,
) -> Result<usize> {
    let count = state
        .store
        .lock()
        .unwrap()
        .restore_recycled(&ids, without_shortcuts.unwrap_or(false))?;
    changed(&app, &state)?;
    Ok(count)
}
#[tauri::command]
fn remove_recycled(app: tauri::AppHandle, state: State<AppState>, ids: Vec<String>) -> Result<()> {
    state.store.lock().unwrap().remove_recycled(&ids)?;
    changed(&app, &state)
}
#[tauri::command]
fn empty_recycle(app: tauri::AppHandle, state: State<AppState>) -> Result<()> {
    state.store.lock().unwrap().empty_recycle()?;
    changed(&app, &state)
}
#[tauri::command]
fn rename_linked_file(
    app: tauri::AppHandle,
    state: State<AppState>,
    id: String,
    name: String,
) -> Result<()> {
    state.store.lock().unwrap().rename_file(&id, &name)?;
    changed(&app, &state)
}
#[tauri::command]
fn move_linked_files(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
    category_id: String,
) -> Result<()> {
    state.store.lock().unwrap().move_files(&ids, &category_id)?;
    changed(&app, &state)
}
#[tauri::command]
fn remove_linked_files(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
) -> Result<()> {
    state.store.lock().unwrap().remove_files(&ids)?;
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
        .store
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
        .store
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
    let files = state.store.lock().unwrap().linked_files(&[id])?;
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
) -> Result<String> {
    let id = state
        .store
        .lock()
        .unwrap()
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
) -> Result<()> {
    state.store.lock().unwrap().sort(&id, &kind, position)?;
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
    state.store.lock().unwrap().add_files(&category_id, paths)?;
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
        .store
        .lock()
        .unwrap()
        .relink(&id, &path, &category_id)?;
    changed(&app, &state)
}
#[tauri::command(async)]
fn import_images(state: State<AppState>, paths: Vec<String>) -> Result<Vec<Block>> {
    let dir = state.store.lock().unwrap().dir.clone();
    backup::import_images(&dir, &paths)
}
#[tauri::command(async)]
fn import_pasted_images(state: State<AppState>, images: Vec<Vec<u8>>) -> Result<Vec<Block>> {
    let dir = state.store.lock().unwrap().dir.clone();
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
    let (settings, segments) = {
        let store = state.store.lock().unwrap();
        let settings = store.settings()?;
        let segments = match kind.as_str() {
            "phrase" => store.phrase(&id)?.segments,
            "common" => {
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
                let file = store
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
    let text = state.store.lock().unwrap().phrase(&id)?.text();
    let owner = app
        .get_webview_window("main")
        .ok_or("主窗口不存在。")?
        .hwnd()
        .map_err(|e| e.to_string())?
        .0 as i64;
    native::copy_text(owner, &text)
}
#[tauri::command]
fn save_settings(app: tauri::AppHandle, state: State<AppState>, settings: Settings) -> Result<()> {
    let store = state.store.lock().unwrap();
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
    drop(store);
    changed(&app, &state)
}
#[tauri::command(async)]
fn preview_import(state: State<AppState>, path: String) -> Result<backup::ImportPreview> {
    let parsed = backup::preview(Path::new(&path))?;
    let preview = parsed.preview.clone();
    *state.pending.lock().unwrap() = Some(parsed);
    Ok(preview)
}
#[tauri::command(async)]
fn confirm_import(
    app: tauri::AppHandle,
    state: State<AppState>,
    preview_id: String,
    merge: bool,
    restore_settings: bool,
) -> Result<()> {
    let pending = state
        .pending
        .lock()
        .unwrap()
        .take()
        .ok_or("请先选择文件并预览。")?;
    if pending.preview.id != preview_id {
        *state.pending.lock().unwrap() = Some(pending);
        return Err("此预览已失效，请重新预览。".into());
    }
    let result = (|| -> Result<()> {
        let mut store = state.store.lock().unwrap();
        let old = store.settings()?;
        let restore = restore_settings && pending.preview.kind == "backup";
        if restore {
            shortcut_conflict(&pending.library.settings, &store.library()?.phrases)?;
            state.native.apply_settings(&pending.library.settings)?;
        }
        if let Err(e) = backup::apply(&mut store, &pending, merge, restore) {
            if restore {
                let _ = state.native.apply_settings(&old);
            }
            return Err(e);
        }
        Ok(())
    })();
    if let Err(e) = result {
        *state.pending.lock().unwrap() = Some(pending);
        return Err(e);
    }
    changed(&app, &state)
}
#[tauri::command(async)]
fn export_data(
    state: State<AppState>,
    path: String,
    kind: String,
    category_id: Option<String>,
) -> Result<()> {
    backup::export(
        &state.store.lock().unwrap(),
        Path::new(&path),
        &kind,
        category_id.as_deref(),
    )
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
    let dir = state.store.lock().unwrap().dir.clone();
    let path = if kind == "cache" {
        dir.join("thumbnails")
    } else {
        dir
    };
    std::process::Command::new("explorer.exe")
        .arg(path)
        .spawn()
        .map_err(|e| format!("文件夹无法打开：{e}"))?;
    Ok(())
}
#[tauri::command]
fn window_action(app: tauri::AppHandle, state: State<AppState>, action: String) -> Result<()> {
    match action.as_str() {
        "hide" => state.native.hide(),
        "show" => state.native.show("show"),
        "minimize" => {
            if state.native.settings.read().unwrap().tray {
                state.native.hide();
            } else if let Some(w) = app.get_webview_window("main") {
                w.minimize().map_err(|e| e.to_string())?;
            }
        }
        "exit" => {
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
    tauri::Builder::default()
        // 重复启动保持现有跟随／手动隐藏状态，只有托盘和快捷键主动唤起窗口。
        .plugin(tauri_plugin_single_instance::init(|_, _, _| {}))
        .plugin(tauri_plugin_dialog::init())
        .on_page_load(|_webview, _| {
            #[cfg(windows)]
            native::square_window_corners(&_webview.window());
        })
        .setup(|app| {
            let dir = app.path().app_local_data_dir()?;
            #[cfg(debug_assertions)]
            let dir = std::env::var_os("SHANYU_TEST_DATA_DIR")
                .map(std::path::PathBuf::from)
                .unwrap_or(dir);
            let store = Store::open(&dir).map_err(std::io::Error::other)?;
            let settings = store.settings().map_err(std::io::Error::other)?;
            app.asset_protocol_scope()
                .allow_directory(dir.join("images"), true)?;
            app.asset_protocol_scope()
                .allow_directory(dir.join("thumbnails"), true)?;
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
                .update_bindings(&store.library().map_err(std::io::Error::other)?)
                .map_err(std::io::Error::other)?;
            app.manage(AppState {
                store: Mutex::new(store),
                pending: Mutex::new(None),
                delivery,
                native: native.clone(),
            });
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
            native.start(app.handle()).map_err(std::io::Error::other)?;
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
                // 普通启动和开机启动均由跟随线程决定可见性，无目标时常驻托盘。
            }
            log::info!("闪语已启动，用户数据目录：{}", dir.display());
            Ok(())
        })
        .on_window_event(|window, event| {
            if let Some(state) = window.try_state::<AppState>() {
                if window.label() == "settings" {
                    if matches!(event, tauri::WindowEvent::Destroyed) {
                        let handle = state.native.settings_window.swap(0, Ordering::AcqRel);
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
                        state
                            .native
                            .settings_window
                            .store(handle.0 as i64, Ordering::Release);
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
        .invoke_handler(tauri::generate_handler![
            get_library,
            search_content,
            save_phrase,
            validate_phrase_shortcut,
            set_shortcut_input,
            sync_sequence_map,
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
            preview_import,
            confirm_import,
            export_data,
            export_template,
            cache_info,
            clear_cache,
            open_directory,
            window_action,
            report_error
        ])
        .run(context)
        .unwrap_or_else(|error| {
            native::startup_error(&format!(
                "闪语启动失败：{error}\n请检查 WebView2 运行时及用户数据目录权限。"
            ));
            std::process::exit(1);
        });
}
