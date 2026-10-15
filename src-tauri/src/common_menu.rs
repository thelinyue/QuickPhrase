//! 常用短语专用菜单。窗口独立于窄底栏，菜单只回传管理动作，不自行修改数据或投递。
//! 打开请求带来源和一次性标识；切窗、移动或重开后，旧菜单不能作用于新的短语。
use crate::{model::Result, native::NativeState, AppState};
use serde::{Deserialize, Serialize};
use std::sync::atomic::Ordering;
use tauri::{Emitter, Manager, State};
use windows::Win32::{
    Foundation::{HWND, RECT},
    UI::WindowsAndMessaging::*,
};

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Payload {
    pub request_id: String,
    pub library_id: String,
    pub phrase_id: Option<String>,
    pub can_sort: bool,
    pub theme: String,
}

pub struct OpenMenu {
    payload: Payload,
    source: String,
    source_handle: i64,
    source_rect: RECT,
    ready: bool,
}

#[derive(Default)]
pub struct MenuState {
    active: Option<OpenMenu>,
}

fn popup(app: &tauri::AppHandle) -> Result<tauri::WebviewWindow> {
    app.get_webview_window("common-menu")
        .ok_or("短语菜单窗口不可用。".into())
}

fn allowed_source(label: &str) -> bool {
    matches!(label, "main" | "common-dock")
}

/// 菜单先按锚点展开；空间不足时翻到上方／左侧，最后限制在所属工作区。
fn position(x: i32, y: i32, width: i32, height: i32, work: RECT) -> (i32, i32) {
    let left = if x + width > work.right { x - width } else { x };
    let top = if y + height > work.bottom {
        y - height
    } else {
        y
    };
    (
        left.clamp(work.left, (work.right - width).max(work.left)),
        top.clamp(work.top, (work.bottom - height).max(work.top)),
    )
}

/// 不持有菜单锁调用窗口 API，避免 UI 线程与后台跟随轮询相互等待。
fn finish(
    app: &tauri::AppHandle,
    native: &NativeState,
    request: &str,
    restore: bool,
    action: Option<&str>,
) -> Result<()> {
    let current = {
        let mut menu = native.common_menu.lock().unwrap();
        if !menu
            .active
            .as_ref()
            .is_some_and(|m| m.payload.request_id == request)
        {
            return Ok(());
        }
        menu.active.take().unwrap()
    };
    native
        .management_windows
        .common_menu
        .store(0, Ordering::Release);
    popup(app)?
        .hide()
        .map_err(|e| format!("无法关闭短语菜单：{e}"))?;
    if restore {
        if let Some(source) = app.get_webview_window(&current.source) {
            if source.is_visible().unwrap_or(false) && !source.is_minimized().unwrap_or(true) {
                source
                    .set_focus()
                    .map_err(|e| format!("无法恢复短语焦点：{e}"))?;
            }
        }
    }
    app.emit_to(
        &current.source,
        "common-menu-action",
        serde_json::json!({
            "requestId": current.payload.request_id, "action": action, "restore": restore,
        }),
    )
    .map_err(|e| format!("无法通知短语菜单操作：{e}"))
}

#[tauri::command]
pub fn open_common_menu(
    app: tauri::AppHandle,
    state: State<AppState>,
    window: tauri::Window,
    mut payload: Payload,
    x: f64,
    y: f64,
) -> Result<()> {
    if !allowed_source(window.label())
        || payload.request_id.is_empty()
        || !x.is_finite()
        || !y.is_finite()
    {
        return Err("短语菜单来源或位置无效。".into());
    }
    {
        let store = crate::private_commands::active_store(&state, Some(&payload.library_id))?;
        let phrases = store.common_phrases()?;
        if payload
            .phrase_id
            .as_ref()
            .is_some_and(|id| !phrases.iter().any(|p| &p.id == id))
        {
            return Err("该短语已不存在，请刷新后重试。".into());
        }
        payload.can_sort = phrases.len() > 1;
        payload.theme = state.native.settings.read().unwrap().theme.clone();
    }
    let old = state
        .native
        .common_menu
        .lock()
        .unwrap()
        .active
        .as_ref()
        .map(|m| m.payload.request_id.clone());
    if let Some(old) = old {
        finish(&app, &state.native, &old, false, None)?;
    }
    let source_handle = window
        .hwnd()
        .map_err(|e| format!("无法读取短语窗口：{e}"))?
        .0 as i64;
    let mut source_rect = RECT::default();
    unsafe {
        let handle = HWND(source_handle as *mut _);
        if !IsWindowVisible(handle).as_bool() || IsIconic(handle).as_bool() {
            return Err("短语窗口已隐藏。".into());
        }
        GetWindowRect(handle, &mut source_rect)
            .map_err(|e| format!("无法读取短语窗口位置：{e}"))?;
    }
    let origin = window
        .inner_position()
        .map_err(|e| format!("无法读取短语窗口坐标：{e}"))?;
    let scale = window
        .scale_factor()
        .map_err(|e| format!("无法读取短语窗口缩放：{e}"))?;
    let monitor = window
        .current_monitor()
        .map_err(|e| format!("无法读取菜单显示器：{e}"))?
        .ok_or("无法找到菜单显示器。")?;
    let work = monitor.work_area();
    let width = 144.0;
    let height = if payload.phrase_id.is_some() {
        130.0
    } else {
        66.0
    };
    let (left, top) = position(
        origin.x + (x * scale).round() as i32,
        origin.y + (y * scale).round() as i32,
        (width * scale).round() as i32,
        (height * scale).round() as i32,
        RECT {
            left: work.position.x,
            top: work.position.y,
            right: work.position.x + work.size.width as i32,
            bottom: work.position.y + work.size.height as i32,
        },
    );
    let menu = popup(&app)?;
    let request = payload.request_id.clone();
    state.native.common_menu.lock().unwrap().active = Some(OpenMenu {
        payload: payload.clone(),
        source: window.label().into(),
        source_handle,
        source_rect,
        ready: false,
    });
    state
        .native
        .management_windows
        .common_menu
        .store(source_handle, Ordering::Release);
    let result: Result<()> = (|| {
        crate::native::prepare_common_menu(
            state.native.common_menu_handle.load(Ordering::Acquire),
        )?;
        menu.set_position(tauri::PhysicalPosition::new(left, top))
            .map_err(|e| e.to_string())?;
        menu.set_size(tauri::LogicalSize::new(width, height))
            .map_err(|e| e.to_string())?;
        // 在所属屏幕完成 DPI 切换后，用实际外框补偿原生细边框，避免边缘仍越界一两像素。
        let outer = menu.outer_size().map_err(|e| e.to_string())?;
        let (left, top) = position(
            origin.x + (x * scale).round() as i32,
            origin.y + (y * scale).round() as i32,
            outer.width as i32,
            outer.height as i32,
            RECT {
                left: work.position.x,
                top: work.position.y,
                right: work.position.x + work.size.width as i32,
                bottom: work.position.y + work.size.height as i32,
            },
        );
        menu.set_position(tauri::PhysicalPosition::new(left, top))
            .map_err(|e| e.to_string())?;
        menu.emit("common-menu", payload)
            .map_err(|e| e.to_string())?;
        menu.show().map_err(|e| e.to_string())?;
        menu.set_focus().map_err(|e| e.to_string())?;
        // Tauri 的 show/focus 排队后可能再次写入 APPWINDOW；在同一 UI 队列末尾修正。
        let queued = app.clone();
        let request = request.clone();
        menu.run_on_main_thread(move || {
            let state = queued.state::<AppState>();
            if let Err(error) = crate::native::prepare_common_menu(
                state.native.common_menu_handle.load(Ordering::Acquire),
            ) {
                log::error!("短语菜单窗口设置失败：{error}");
                let _ = queued.emit("native-error", error);
                let _ = finish(&queued, &state.native, &request, false, None);
                return;
            }
            if let Some(current) = state
                .native
                .common_menu
                .lock()
                .unwrap()
                .active
                .as_mut()
                .filter(|m| m.payload.request_id == request)
            {
                current.ready = true;
            };
        })
        .map_err(|e| e.to_string())?;
        Ok(())
    })();
    if let Err(error) = result {
        let _ = finish(&app, &state.native, &request, false, None);
        return Err(format!("无法打开短语菜单：{error}"));
    }
    Ok(())
}

#[tauri::command]
pub fn get_common_menu(state: State<AppState>, window: tauri::Window) -> Result<Option<Payload>> {
    if window.label() != "common-menu" {
        return Err("此窗口不能读取短语菜单。".into());
    }
    Ok(state
        .native
        .common_menu
        .lock()
        .unwrap()
        .active
        .as_ref()
        .map(|m| m.payload.clone()))
}

#[tauri::command]
pub fn choose_common_menu(
    app: tauri::AppHandle,
    state: State<AppState>,
    window: tauri::Window,
    request_id: String,
    action: String,
) -> Result<()> {
    if window.label() != "common-menu" {
        return Err("此窗口不能操作短语菜单。".into());
    }
    let payload = state
        .native
        .common_menu
        .lock()
        .unwrap()
        .active
        .as_ref()
        .filter(|m| m.payload.request_id == request_id)
        .map(|m| m.payload.clone())
        .ok_or("短语菜单已失效。")?;
    if !matches!(action.as_str(), "add" | "edit" | "delete" | "sort")
        || (matches!(action.as_str(), "edit" | "delete") && payload.phrase_id.is_none())
    {
        return Err("短语菜单操作无效。".into());
    }
    {
        let store = crate::private_commands::active_store(&state, Some(&payload.library_id))?;
        let phrases = store.common_phrases()?;
        if payload
            .phrase_id
            .as_ref()
            .is_some_and(|id| !phrases.iter().any(|p| &p.id == id))
            || (action == "sort" && phrases.len() < 2)
        {
            return Err("短语已变化，请重新打开菜单。".into());
        }
    }
    finish(&app, &state.native, &request_id, true, Some(&action))
}

#[tauri::command]
pub fn close_common_menu(
    app: tauri::AppHandle,
    state: State<AppState>,
    window: tauri::Window,
    request_id: String,
    restore: bool,
) -> Result<()> {
    let allowed = state
        .native
        .common_menu
        .lock()
        .unwrap()
        .active
        .as_ref()
        .is_some_and(|m| window.label() == "common-menu" || window.label() == m.source);
    if !allowed {
        return Ok(());
    }
    finish(
        &app,
        &state.native,
        &request_id,
        restore && window.label() == "common-menu",
        None,
    )
}

/// 只读 Win32 状态的轮询；隐藏动作排回 UI 线程，旧请求不能关掉后来打开的菜单。
pub unsafe fn poll(app: &tauri::AppHandle, native: &NativeState, foreground: HWND) {
    let request = {
        let menu = native.common_menu.lock().unwrap();
        menu.active
            .as_ref()
            .filter(|m| {
                let source = HWND(m.source_handle as *mut _);
                let mut rect = RECT::default();
                m.ready
                    && (!IsWindowVisible(source).as_bool()
                        || IsIconic(source).as_bool()
                        || GetWindowRect(source, &mut rect).is_err()
                        || rect != m.source_rect
                        || foreground.0 as i64 != native.common_menu_handle.load(Ordering::Acquire))
            })
            .map(|m| m.payload.request_id.clone())
    };
    if let Some(request) = request {
        let app = app.clone();
        let queued = app.clone();
        let _ = app.run_on_main_thread(move || {
            let state = queued.state::<AppState>();
            let _ = finish(&queued, &state.native, &request, false, None);
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn popup_stays_in_work_area_at_edges_and_negative_coordinates() {
        for scale in [1.0, 1.25, 1.5, 2.0] {
            let work = RECT {
                left: -1920,
                top: -200,
                right: 0,
                bottom: 880,
            };
            let w = (144.0 * scale) as i32;
            let h = (130.0 * scale) as i32;
            for (x, y) in [(-1919, -199), (-5, 875), (-1000, 350)] {
                let (left, top) = position(x, y, w, h, work);
                assert!(left >= work.left && left + w <= work.right);
                assert!(top >= work.top && top + h <= work.bottom);
            }
            assert_eq!(position(-5, 875, w, h, work), (-5 - w, 875 - h));
        }
        assert!(allowed_source("main") && allowed_source("common-dock"));
        assert!(!allowed_source("settings") && !allowed_source("common-menu"));
    }
}
