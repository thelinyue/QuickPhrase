//! 最小化恢复小条：窗口动作与主窗口共用队列，坐标只存设备库，不进入个人设置同步。
//! 跟随线程只读几何；界面线程执行窗口动作，数据库写入交给阻塞工作池。
use super::{platform::hwnd, NativeState};
use crate::{model::Result, store::db_error, store::Store, AppState};
use rusqlite::OptionalExtension;
use serde::{Deserialize, Serialize};
use std::{
    mem::size_of,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
};
use tauri::Manager;
use windows::Win32::{
    Foundation::{HWND, POINT, RECT},
    Graphics::Gdi::*,
    UI::{
        HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI},
        WindowsAndMessaging::*,
    },
};

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub(super) struct Position {
    pub x: i32,
    pub y: i32,
}
const META: &str = "minimizedFloatPosition";

pub(super) fn read_position(store: &Store) -> Result<Option<Position>> {
    let text: Option<String> = store
        .conn
        .query_row("SELECT value FROM sync_meta WHERE key=?1", [META], |r| {
            r.get(0)
        })
        .optional()
        .map_err(db_error)?;
    text.map(|text| {
        serde_json::from_str(&text).map_err(|_| "悬浮窗位置无法读取，将使用屏幕右侧位置。".into())
    })
    .transpose()
}

fn write_position(store: &Store, position: Position) -> Result<()> {
    let tx = store.conn.unchecked_transaction().map_err(db_error)?;
    tx.execute(
        "INSERT INTO sync_meta VALUES(?1,?2) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
        [META, &serde_json::to_string(&position).map_err(db_error)?],
    )
    .map_err(db_error)?;
    tx.commit().map_err(db_error)
}

pub(super) fn persist_position(app: &tauri::AppHandle) -> Result<()> {
    let state = app.state::<AppState>();
    let device = state.device_store.lock().unwrap();
    // 多次拖动的写入可能在工作池中等待；取得数据库锁后再读最新坐标，旧任务不能写回旧位置。
    let position = *state.native.float_position.lock().unwrap();
    if let Some(position) = position {
        write_position(&device, position)?;
    }
    Ok(())
}

fn remember(state: &NativeState, position: Position) {
    {
        let mut saved = state.float_position.lock().unwrap();
        if *saved == Some(position) {
            return;
        }
        *saved = Some(position);
    }
    if let Some(app) = state.app.get().cloned() {
        tauri::async_runtime::spawn_blocking(move || {
            if let Err(error) = persist_position(&app) {
                app.state::<AppState>().native.notify_error(error);
            }
        });
    }
}

/// 坐标是虚拟桌面的物理像素；负坐标显示器和已拔掉的显示器均钳制到最近工作区。
fn bounds(work: RECT, position: Option<Position>, dpi: u32) -> RECT {
    let width =
        ((96.0 * f64::from(dpi) / 96.0).round() as i32).min((work.right - work.left).max(1));
    let height =
        ((40.0 * f64::from(dpi) / 96.0).round() as i32).min((work.bottom - work.top).max(1));
    let initial = super::docking::standalone_bounds(&work, width, height, dpi);
    let position = position.unwrap_or(Position {
        x: initial.left,
        y: initial.top,
    });
    let left = position.x.clamp(work.left, work.right - width);
    let top = position.y.clamp(work.top, work.bottom - height);
    RECT {
        left,
        top,
        right: left + width,
        bottom: top + height,
    }
}

/// 同一时刻最多挂起一个界面线程动作；还原必须等待它结束，避免旧最小化晚于还原执行。
#[derive(Default)]
pub(super) struct FloatingWindow {
    positioned: bool,
    pending: Arc<AtomicBool>,
}
impl FloatingWindow {
    pub fn ready(&self) -> bool {
        !self.pending.load(Ordering::Acquire)
    }

    fn schedule(
        &mut self,
        state: &Arc<NativeState>,
        action: impl FnOnce(&NativeState) + Send + 'static,
    ) -> bool {
        let Some(app) = state.app.get() else {
            return false;
        };
        self.pending.store(true, Ordering::Release);
        let pending = self.pending.clone();
        let this = state.clone();
        if let Err(error) = app.run_on_main_thread(move || {
            action(&this);
            pending.store(false, Ordering::Release);
        }) {
            self.pending.store(false, Ordering::Release);
            state.notify_error(format!("无法更新最小化悬浮窗：{error}"));
            return false;
        }
        true
    }

    pub unsafe fn minimize(&mut self, state: &Arc<NativeState>, main: HWND, tray: bool) {
        let handle = main.0 as i64;
        self.schedule(state, move |state| {
            // 已到达队列的还原／隐藏请求会撤销意图；过时的系统最小化直接跳过。
            if state.minimized.load(Ordering::Acquire) {
                let _ = ShowWindow(hwnd(handle), if tray { SW_HIDE } else { SW_MINIMIZE });
            }
        });
    }

    unsafe fn should_show(state: &NativeState, main: HWND) -> bool {
        state.minimized.load(Ordering::Acquire)
            && state.settings.read().unwrap().show_floating_on_minimize
            && (!IsWindowVisible(main).as_bool() || IsIconic(main).as_bool())
    }

    pub unsafe fn update(&mut self, state: &Arc<NativeState>, main: HWND) {
        if !self.ready() {
            return;
        }
        let Some(handle) = state.float_window.get().copied() else {
            return;
        };
        let window = hwnd(handle);
        if !Self::should_show(state, main) {
            if IsWindowVisible(window).as_bool() {
                let main = main.0 as i64;
                self.schedule(state, move |state| {
                    if !Self::should_show(state, hwnd(main)) {
                        let _ = ShowWindow(hwnd(handle), SW_HIDE);
                    }
                });
            }
            return;
        }
        let mut gui = GUITHREADINFO {
            cbSize: size_of::<GUITHREADINFO>() as u32,
            ..Default::default()
        };
        if GetGUIThreadInfo(GetWindowThreadProcessId(window, None), &mut gui).is_err()
            || gui.flags.0 & GUI_INMOVESIZE.0 != 0
        {
            return;
        }
        let mut current = RECT::default();
        if GetWindowRect(window, &mut current).is_err() {
            return;
        }
        let position = if self.positioned {
            Some(Position {
                x: current.left,
                y: current.top,
            })
        } else {
            *state.float_position.lock().unwrap()
        };
        let monitor = position.map_or_else(
            || MonitorFromWindow(main, MONITOR_DEFAULTTONEAREST),
            |p| MonitorFromPoint(POINT { x: p.x, y: p.y }, MONITOR_DEFAULTTONEAREST),
        );
        let mut info = MONITORINFO {
            cbSize: size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        if !GetMonitorInfoW(monitor, &mut info).as_bool() {
            return;
        }
        let (mut dpi, mut dpi_y) = (96, 96);
        let _ = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut dpi, &mut dpi_y);
        let desired = bounds(info.rcWork, position, dpi);
        if !IsWindowVisible(window).as_bool() || current != desired {
            let main = main.0 as i64;
            if self.schedule(state, move |state| {
                // 同时裁剪原生窗口和命中区域，避免 CSS 圆角外仍显示方形背景或拦截鼠标。
                // 区域成功交给系统后由系统释放；失败时仍由调用方释放。
                let diameter = (24.0 * f64::from(dpi) / 96.0).round() as i32;
                let region = CreateRoundRectRgn(
                    0,
                    0,
                    desired.right - desired.left + 1,
                    desired.bottom - desired.top + 1,
                    diameter,
                    diameter,
                );
                if region.is_invalid() || SetWindowRgn(hwnd(handle), Some(region), true) == 0 {
                    if !region.is_invalid() {
                        let _ = DeleteObject(region.into());
                    }
                    state.notify_error("悬浮窗圆角暂时无法应用，可继续单击打开闪语。".into());
                }
                let show = Self::should_show(state, hwnd(main));
                let flags = SWP_NOACTIVATE
                    | SWP_NOOWNERZORDER
                    | if show {
                        SWP_SHOWWINDOW
                    } else {
                        SET_WINDOW_POS_FLAGS::default()
                    };
                if let Err(error) = SetWindowPos(
                    hwnd(handle),
                    Some(HWND_TOPMOST),
                    desired.left,
                    desired.top,
                    desired.right - desired.left,
                    desired.bottom - desired.top,
                    flags,
                ) {
                    state.notify_error(format!(
                        "悬浮窗暂时无法显示，请通过托盘或快捷键打开闪语：{error}"
                    ));
                }
            }) {
                self.positioned = true;
            }
        } else {
            remember(
                state,
                Position {
                    x: current.left,
                    y: current.top,
                },
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn positions_survive_reopen_without_becoming_personal_settings() {
        let dir = tempfile::tempdir().unwrap();
        let position = Position { x: -1200, y: 240 };
        {
            let store = Store::open(dir.path()).unwrap();
            assert_eq!(read_position(&store).unwrap(), None);
            let before = store.settings().unwrap();
            write_position(&store, position).unwrap();
            assert_eq!(store.settings().unwrap(), before);
        }
        assert_eq!(
            read_position(&Store::open(dir.path()).unwrap()).unwrap(),
            Some(position)
        );
    }
    #[test]
    fn defaults_and_offscreen_positions_fit_work_area_at_each_dpi() {
        let work = RECT {
            left: -1920,
            top: -120,
            right: 0,
            bottom: 960,
        };
        for dpi in [96, 144, 192] {
            let rect = bounds(work, None, dpi);
            assert_eq!(work.right - rect.right, 12 * dpi as i32 / 96);
            assert_eq!(rect.right - rect.left, dpi as i32);
            assert_eq!(rect.bottom - rect.top, 40 * dpi as i32 / 96);
            for position in [
                Position {
                    x: i32::MIN,
                    y: i32::MIN,
                },
                Position {
                    x: i32::MAX,
                    y: i32::MAX,
                },
            ] {
                let rect = bounds(work, Some(position), dpi);
                assert!(
                    rect.left >= work.left
                        && rect.right <= work.right
                        && rect.top >= work.top
                        && rect.bottom <= work.bottom
                );
            }
        }
    }
}
