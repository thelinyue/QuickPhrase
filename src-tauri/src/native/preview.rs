use crate::model::Phrase;
use serde::{Deserialize, Serialize};

/// 前端只提交话术行的客户区坐标；原生层复核鼠标仍在该行，并按真实鼠标位置定位。
#[derive(Deserialize)]
pub struct PreviewAnchor {
    pub left: f64,
    pub top: f64,
    pub right: f64,
    pub bottom: f64,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PreviewPayload {
    pub phrase: Phrase,
    pub font_size: u8,
    pub title_color: String,
    pub theme: String,
    pub max_height: f64,
}

#[cfg(windows)]
pub use platform::PreviewState;

#[cfg(windows)]
mod platform {
    use super::*;
    use crate::{
        model::Result,
        native::{
            platform::{hwnd, pid},
            NativeState,
        },
    };
    use std::time::{Duration, Instant};
    use windows::Win32::{
        Foundation::{HWND, POINT, RECT},
        UI::{
            Input::KeyboardAndMouse::{GetAsyncKeyState, VK_ESCAPE},
            WindowsAndMessaging::*,
        },
    };

    /// 预览常驻但不激活。鼠标可从原话术行跨入浮层滚动；离开两者、源窗口移动或隐藏时收起。
    /// 后台只读取缓存句柄和 Win32 状态，不等待 Tauri 主线程，避免与设置保存互相阻塞。
    #[derive(Default)]
    pub struct PreviewState {
        pub payload: Option<PreviewPayload>,
        pub source: i64,
        source_bounds: RECT,
        anchor: RECT,
        pointer: POINT,
        work: RECT,
        scale: f64,
        foreground_pid: u32,
        outside: Option<Instant>,
    }
    fn contains(rect: RECT, point: POINT) -> bool {
        point.x >= rect.left && point.x < rect.right && point.y >= rect.top && point.y < rect.bottom
    }
    /// 原生外框尺寸含不可见边框，前端测量的是客户区；补偿后短文不会出现多余滚动条。
    unsafe fn client_insets(window: HWND) -> Result<(i32, i32)> {
        let mut outer = RECT::default();
        let mut client = RECT::default();
        GetWindowRect(window, &mut outer).map_err(|e| format!("无法读取预览窗口范围：{e}"))?;
        GetClientRect(window, &mut client).map_err(|e| format!("无法读取预览内容范围：{e}"))?;
        Ok((
            (outer.right - outer.left) - (client.right - client.left),
            (outer.bottom - outer.top) - (client.bottom - client.top),
        ))
    }
    /// 提示始终从鼠标右下方展开，屏幕边缘也不翻转或反向移动；超出屏幕的部分由系统裁切。
    fn bounds(pointer: POINT, width: i32, height: i32, gap: i32) -> RECT {
        let x = pointer.x + gap;
        let y = pointer.y + gap;
        RECT {
            left: x,
            top: y,
            right: x + width,
            bottom: y + height,
        }
    }
    /// 固定右下方展开，只使用鼠标下方的客户区高度；不设480 DIP上限，也不反向挪动。
    fn available_height(pointer: POINT, work: RECT, scale: f64, vertical_inset: i32) -> f64 {
        let gap = (12.0 * scale).round() as i32;
        (work.bottom - pointer.y - gap - vertical_inset).max(0) as f64 / scale
    }
    impl PreviewState {
        unsafe fn hide(&mut self, preview: HWND) {
            self.payload = None;
            self.outside = None;
            let _ = ShowWindow(preview, SW_HIDE);
        }
        pub unsafe fn update(&mut self, preview: HWND, foreground: HWND) {
            if self.payload.is_none() {
                return;
            }
            let source = hwnd(self.source);
            let mut current = RECT::default();
            if !IsWindowVisible(source).as_bool()
                || IsIconic(source).as_bool()
                || GetWindowRect(source, &mut current).is_err()
                || current != self.source_bounds
                || (pid(foreground) != std::process::id() && pid(foreground) != self.foreground_pid)
                || GetAsyncKeyState(VK_ESCAPE.0 as i32) < 0
            {
                self.hide(preview);
                return;
            }
            let mut point = POINT::default();
            let mut popup = RECT::default();
            let _ = GetCursorPos(&mut point);
            let in_popup = IsWindowVisible(preview).as_bool()
                && GetWindowRect(preview, &mut popup).is_ok()
                && contains(popup, point);
            if contains(self.anchor, point) || in_popup {
                self.outside = None;
            } else if self.outside.get_or_insert_with(Instant::now).elapsed()
                >= Duration::from_millis(200)
            {
                self.hide(preview);
            }
        }
    }
    impl NativeState {
        pub fn show_preview(
            &self,
            source: &tauri::Window,
            phrase: Phrase,
            anchor: PreviewAnchor,
        ) -> Result<()> {
            if ![anchor.left, anchor.top, anchor.right, anchor.bottom]
                .iter()
                .all(|v| v.is_finite())
                || anchor.right <= anchor.left
                || anchor.bottom <= anchor.top
            {
                return Err("话术预览位置无效。".into());
            }
            let position = source
                .inner_position()
                .map_err(|e| format!("无法读取话术窗口位置：{e}"))?;
            let scale = source
                .scale_factor()
                .map_err(|e| format!("无法读取话术窗口缩放：{e}"))?;
            let monitor = source
                .current_monitor()
                .map_err(|e| format!("无法读取显示器工作区：{e}"))?
                .ok_or("话术窗口不在可用显示器上。")?;
            let work = monitor.work_area();
            let margin = (8.0 * scale).round() as i32;
            let work = RECT {
                left: work.position.x + margin,
                top: work.position.y + margin,
                right: work.position.x + work.size.width as i32 - margin,
                bottom: work.position.y + work.size.height as i32 - margin,
            };
            let anchor = RECT {
                left: position.x + (anchor.left * scale).round() as i32,
                top: position.y + (anchor.top * scale).round() as i32,
                right: position.x + (anchor.right * scale).round() as i32,
                bottom: position.y + (anchor.bottom * scale).round() as i32,
            };
            let source_handle = source
                .hwnd()
                .map_err(|e| format!("无法读取话术窗口句柄：{e}"))?
                .0 as i64;
            let mut source_bounds = RECT::default();
            let mut pointer = POINT::default();
            unsafe {
                let _ = GetCursorPos(&mut pointer);
                // 延时结束时再次核对真实鼠标，快速掠过和已经隐藏的窗口不会弹出旧预览。
                if !IsWindowVisible(hwnd(source_handle)).as_bool() || !contains(anchor, pointer) {
                    return Ok(());
                }
                GetWindowRect(hwnd(source_handle), &mut source_bounds)
                    .map_err(|e| format!("无法读取话术窗口范围：{e}"))?;
            }
            let settings = self.settings.read().unwrap().clone();
            let preview_handle = hwnd(self.windows.get().ok_or("Windows服务尚未启动。")?.2);
            // 隐藏窗口首次布局可能仍保留系统标题栏尺寸，刷新非客户区后再测量实际边框。
            unsafe {
                SetWindowPos(
                    preview_handle,
                    None,
                    0,
                    0,
                    0,
                    0,
                    SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE,
                )
                .map_err(|e| format!("无法准备话术预览窗口：{e}"))?;
            }
            let (_, vertical_inset) = unsafe { client_insets(preview_handle)? };
            let max_height = available_height(pointer, work, scale, vertical_inset);
            if max_height < 1.0 {
                self.hide_preview(None);
                return Ok(());
            }
            let payload = PreviewPayload {
                phrase,
                font_size: settings.font_size,
                title_color: settings.title_color,
                theme: settings.theme,
                max_height,
            };
            *self.preview.lock().unwrap() = PreviewState {
                payload: Some(payload.clone()),
                source: source_handle,
                source_bounds,
                anchor,
                pointer,
                work,
                scale,
                foreground_pid: unsafe { pid(GetForegroundWindow()) },
                outside: None,
            };
            self.app
                .get()
                .and_then(|app| app.get_webview_window("phrase-preview"))
                .ok_or("话术预览窗口尚未创建。")?
                .emit("phrase-preview", &payload)
                .map_err(|e| format!("无法刷新话术预览：{e}"))
        }
        pub fn resize_preview(&self, id: &str, height: f64) -> Result<()> {
            if !height.is_finite() || height <= 0.0 {
                return Err("话术预览高度无效。".into());
            }
            let mut state = self.preview.lock().unwrap();
            let Some(payload) = &state.payload else {
                return Ok(());
            };
            if payload.phrase.id != id {
                return Ok(());
            }
            let preview = hwnd(self.windows.get().ok_or("Windows服务尚未启动。")?.2);
            let (horizontal_inset, vertical_inset) = unsafe { client_insets(preview)? };
            let width = ((300.0 * state.scale).round() as i32 + horizontal_inset)
                .min(state.work.right - state.work.left);
            let height =
                (height.min(payload.max_height) * state.scale).ceil() as i32 + vertical_inset;
            let rect = bounds(
                state.pointer,
                width,
                height,
                (12.0 * state.scale).round() as i32,
            );
            unsafe {
                SetWindowPos(
                    preview,
                    Some(HWND_TOPMOST),
                    rect.left,
                    rect.top,
                    width,
                    height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW,
                )
                .map_err(|e| format!("无法显示话术预览：{e}"))?;
            }
            state.outside = None;
            Ok(())
        }
        pub fn hide_preview(&self, id: Option<&str>) {
            let mut state = self.preview.lock().unwrap();
            if state.payload.is_none() {
                return;
            }
            if id.is_some_and(|id| state.payload.as_ref().is_none_or(|p| p.phrase.id != id)) {
                return;
            }
            if let Some(windows) = self.windows.get() {
                unsafe {
                    state.hide(hwnd(windows.2));
                }
            }
        }
    }
    use tauri::{Emitter, Manager};
    #[cfg(test)]
    mod tests {
        use super::*;
        #[test]
        fn preview_height_uses_remaining_work_area_at_each_scale() {
            for origin in [-1080, 0, 1080] {
                let work = RECT {
                    left: 0,
                    top: origin,
                    right: 1920,
                    bottom: origin + 1040,
                };
                for scale in [1.0, 1.5, 2.0] {
                    let gap = (12.0_f64 * scale).round() as i32;
                    let pointer = POINT {
                        x: 100,
                        y: origin + 100,
                    };
                    let available = available_height(pointer, work, scale, 2);
                    assert_eq!(available, (940 - gap - 2) as f64 / scale);
                    if scale == 1.0 {
                        assert!(available > 480.0);
                    }
                    for y in [origin + 990, origin + 1040 - gap, origin + 1050] {
                        let remaining = available_height(POINT { x: 100, y }, work, scale, 2);
                        assert!(remaining >= 0.0);
                        if remaining > 0.0 {
                            assert!(y + gap + (remaining * scale).ceil() as i32 + 2 <= work.bottom);
                        } else {
                            assert!(y + gap + 2 >= work.bottom);
                        }
                    }
                }
            }
        }
        #[test]
        fn preview_keeps_pointer_direction_even_at_screen_edges() {
            for origin in [-1920, 0, 1920] {
                let work = RECT {
                    left: origin + 8,
                    top: 8,
                    right: origin + 1912,
                    bottom: 1032,
                };
                for scale in [1.0, 1.5, 2.0] {
                    let width = (300.0 * scale) as i32;
                    let height = (400.0 * scale) as i32;
                    let gap = (12.0 * scale) as i32;
                    for x in [100, 1890] {
                        for y in [100, 1000] {
                            let pointer = POINT { x: origin + x, y };
                            let rect = bounds(pointer, width, height, gap);
                            assert_eq!((rect.left - pointer.x, rect.top - pointer.y), (gap, gap));
                            assert_eq!(
                                (rect.right - rect.left, rect.bottom - rect.top),
                                (width, height)
                            );
                            if x == 1890 {
                                assert!(rect.right > work.right);
                            }
                            if y == 1000 {
                                assert!(rect.bottom > work.bottom);
                            }
                            assert!(!contains(rect, pointer));
                        }
                    }
                }
            }
        }
    }
}
