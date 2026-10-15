//! 清空确认独立于话术删除窗口。库身份固定到关闭，写入与关窗互斥，窗口操作不持有库锁。
use crate::{model::Result, private_commands, AppState};
use std::sync::atomic::Ordering;
use tauri::{Emitter, Manager, State};
use windows::Win32::{
    Foundation::{HWND, RECT},
    UI::WindowsAndMessaging::{
        GetClientRect, GetForegroundWindow, GetWindowRect, GetWindowThreadProcessId, SetWindowPos,
        SWP_NOACTIVATE, SWP_NOZORDER,
    },
};

pub const LABEL: &str = "search-history-confirm";

pub struct Session {
    library_id: String,
    busy: bool,
}

fn source(app: &tauri::AppHandle) -> Option<tauri::WebviewWindow> {
    app.get_webview_window("main")
        .filter(|w| w.is_visible().unwrap_or(false) && !w.is_minimized().unwrap_or(true))
}

#[tauri::command(async)]
pub fn open_search_history_confirm(
    app: tauri::AppHandle,
    state: State<AppState>,
    window: tauri::Window,
    library_id: String,
) -> Result<()> {
    if window.label() != "main" {
        return Err("只能从主窗口清空搜索记录。".into());
    }
    let _activity = crate::update_activity::Activity::enter()?;
    drop(private_commands::active_store(&state, Some(&library_id))?);
    let parent = source(&app).ok_or("来源窗口已关闭或隐藏。")?;
    {
        let mut session = state.native.search_history_confirm.lock().unwrap();
        if session.is_some() {
            drop(session);
            if let Some(existing) = app.get_webview_window(LABEL) {
                existing
                    .set_focus()
                    .map_err(|e| format!("无法激活清空确认：{e}"))?;
            }
            return Ok(());
        }
        *session = Some(Session {
            library_id: library_id.clone(),
            busy: false,
        });
    }
    // 创建期间也暂停业务快捷键与贴边收起，防止主窗口在确认窗口出现前消失。
    state
        .native
        .management_windows
        .search_history_confirm
        .store(1, Ordering::Release);
    let result = (|| -> Result<()> {
        let monitor = parent
            .current_monitor()
            .map_err(|e| e.to_string())?
            .ok_or("无法读取确认窗口的显示器。")?;
        let work = *monitor.work_area();
        let scale = monitor.scale_factor();
        let width = 384.0_f64.min(work.size.width as f64 / scale);
        let height = 256.0_f64.min(work.size.height as f64 / scale);
        let url = format!("index.html#{LABEL}?libraryId={library_id}");
        let popup =
            tauri::WebviewWindowBuilder::new(&app, LABEL, tauri::WebviewUrl::App(url.into()))
                .title("提示")
                .inner_size(width, height)
                .decorations(false)
                .resizable(false)
                .minimizable(false)
                .maximizable(false)
                .skip_taskbar(true)
                .visible(false)
                .general_autofill_enabled(false)
                .build()
                .map_err(|e| format!("无法打开清空确认：{e}"))?;
        // 先移到来源显示器，再使用窗口实际 DPI 限制尺寸和位置。
        popup
            .set_position(tauri::PhysicalPosition::new(
                work.position.x,
                work.position.y,
            ))
            .map_err(|e| e.to_string())?;
        let actual = popup.scale_factor().map_err(|e| e.to_string())?;
        drop(private_commands::active_store(&state, Some(&library_id))?);
        if source(&app).is_none()
            || state
                .native
                .search_history_confirm
                .lock()
                .unwrap()
                .is_none()
        {
            return Err("来源已失效，请重新打开清空确认。".into());
        }
        popup.show().map_err(|e| e.to_string())?;
        popup.set_focus().map_err(|e| e.to_string())?;
        // show 会重新计算无装饰客户区；在同一 UI 队列末尾校正，不能用显示前的标题栏差值。
        let shown = popup.clone();
        popup
            .run_on_main_thread(move || {
                let popup = shown;
                let result = (|| -> Result<()> {
                    crate::native::prepare_common_menu(
                        popup.hwnd().map_err(|e| e.to_string())?.0 as i64,
                    )?;
                    // 无装饰窗口仍可能被 Tauri 按标题栏高度补偿；按实际客户区差值一次校正。
                    unsafe {
                        let handle = HWND(popup.hwnd().map_err(|e| e.to_string())?.0);
                        let mut outer = RECT::default();
                        let mut client = RECT::default();
                        GetWindowRect(handle, &mut outer).map_err(|e| e.to_string())?;
                        GetClientRect(handle, &mut client).map_err(|e| e.to_string())?;
                        let width = ((384.0 * actual).round() as i32 + outer.right
                            - outer.left
                            - client.right)
                            .min(work.size.width as i32);
                        let height = ((256.0 * actual).round() as i32 + outer.bottom
                            - outer.top
                            - client.bottom)
                            .min(work.size.height as i32);
                        SetWindowPos(
                            handle,
                            None,
                            work.position.x + (work.size.width as i32 - width) / 2,
                            work.position.y + (work.size.height as i32 - height) / 2,
                            width,
                            height,
                            SWP_NOACTIVATE | SWP_NOZORDER,
                        )
                        .map_err(|e| format!("无法调整清空确认窗口：{e}"))?;
                    }
                    Ok(())
                })();
                if let Err(error) = result {
                    log::error!("清空确认窗口尺寸调整失败：{error}");
                }
            })
            .map_err(|e| e.to_string())?;
        Ok(())
    })();
    if result.is_err() {
        finish(&app, &state, false)?;
    }
    result
}

/// 与原有清空 IPC 共用事务；仅专用窗口需要额外的来源身份和提交锁。
pub fn begin(app: &tauri::AppHandle, state: &AppState, library_id: &str) -> Result<()> {
    if source(app).is_none() {
        return Err("来源窗口已关闭或隐藏，请重新打开清空确认。".into());
    }
    let mut session = state.native.search_history_confirm.lock().unwrap();
    let current = session
        .as_mut()
        .filter(|s| s.library_id == library_id)
        .ok_or("清空确认已失效，请重新打开。")?;
    if current.busy {
        return Err("正在清空搜索记录，请稍候。".into());
    }
    current.busy = true;
    Ok(())
}

pub fn end(state: &AppState) {
    if let Some(current) = state.native.search_history_confirm.lock().unwrap().as_mut() {
        current.busy = false;
    }
}

fn finish(app: &tauri::AppHandle, state: &AppState, restore: bool) -> Result<()> {
    let current = {
        let mut session = state.native.search_history_confirm.lock().unwrap();
        if session.as_ref().is_some_and(|s| s.busy) {
            return Err("正在清空搜索记录，请稍候。".into());
        }
        session.take()
    };
    let Some(current) = current else {
        return Ok(());
    };
    let mut pid = 0;
    unsafe {
        GetWindowThreadProcessId(GetForegroundWindow(), Some(&mut pid));
    }
    let restore = restore
        && pid == std::process::id()
        && private_commands::active_store(state, Some(&current.library_id)).is_ok();
    if let Some(popup) = app.get_webview_window(LABEL) {
        popup
            .destroy()
            .map_err(|e| format!("无法关闭清空确认：{e}"))?;
    }
    state
        .native
        .management_windows
        .search_history_confirm
        .store(0, Ordering::Release);
    if restore {
        if let Some(parent) = source(app) {
            parent
                .set_focus()
                .map_err(|e| format!("无法恢复搜索焦点：{e}"))?;
            parent
                .emit("search-history-focus", current.library_id)
                .map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

#[tauri::command(async)]
pub fn close_search_history_confirm(
    app: tauri::AppHandle,
    state: State<AppState>,
    window: tauri::Window,
) -> Result<()> {
    if window.label() != LABEL {
        return Err("清空确认来源无效。".into());
    }
    finish(&app, &state, true)
}

pub fn window_event(window: &tauri::Window, event: &tauri::WindowEvent) {
    if window.label() != LABEL {
        return;
    }
    if let tauri::WindowEvent::CloseRequested { api, .. } = event {
        api.prevent_close();
        let app = window.app_handle().clone();
        let _ = window.run_on_main_thread(move || {
            let _ = finish(&app, &app.state::<AppState>(), true);
        });
    }
    if matches!(event, tauri::WindowEvent::Destroyed) {
        window
            .state::<AppState>()
            .native
            .search_history_confirm
            .lock()
            .unwrap()
            .take();
    }
}

/// 跟随线程检测来源隐藏和切库；排入 UI 队列关闭，避免持库锁调用窗口 API。
pub fn poll(app: &tauri::AppHandle, native: &crate::native::NativeState) {
    let library_id = native
        .search_history_confirm
        .lock()
        .unwrap()
        .as_ref()
        .filter(|s| !s.busy)
        .map(|s| s.library_id.clone());
    let Some(library_id) = library_id else {
        return;
    };
    let state = app.state::<AppState>();
    let valid_library = match state.store.try_lock() {
        Ok(store) => crate::private_local::scope(&store, Some(&library_id)).is_ok(),
        Err(_) => return,
    };
    if !valid_library || source(app).is_none() {
        let queued = app.clone();
        let _ = app.run_on_main_thread(move || {
            let _ = finish(&queued, &queued.state::<AppState>(), false);
        });
    }
}
