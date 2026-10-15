//! 独立窗口左右贴边隐藏。几何和计时独立于企业微信吸附，不改变窗口尺寸或发送目标。
//! 后台仅计算下一帧；裁剪与移动排到界面线程，避免设置保存与原生窗口消息互相等待。
use super::{platform::hwnd, NativeState};
use std::{
    mem::size_of,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::{Duration, Instant},
};
use windows::Win32::{
    Foundation::{HWND, POINT, RECT},
    Graphics::{
        Dwm::{DwmGetWindowAttribute, DWMWA_EXTENDED_FRAME_BOUNDS},
        Gdi::*,
    },
    UI::{HiDpi::GetDpiForWindow, Input::KeyboardAndMouse::*, WindowsAndMessaging::*},
};

#[derive(Clone, Copy, Debug)]
struct Anchor {
    expanded: RECT,
    insets: RECT,
    hidden_x: i32,
    work: RECT,
    dpi: u32,
}

impl Anchor {
    fn new(outer: RECT, frame: RECT, work: RECT, dpi: u32) -> Option<Self> {
        let strip = (2.0 * f64::from(dpi) / 96.0).round().max(1.0) as i32;
        // 用户拖动很容易越过边缘；只要窗口仍与本工作区相交，越界也算贴边。
        // 仅向屏幕内保留 2 DIP 容差，首次显示的 12 DIP 边距仍不会触发。
        let left = frame.left <= work.left + strip && frame.right > work.left;
        let right = frame.right >= work.right - strip && frame.left < work.right;
        if (!left && !right) || frame.right - frame.left > work.right - work.left {
            return None;
        }
        let shift = if left {
            work.left - frame.left
        } else {
            work.right - frame.right
        };
        let expanded = RECT {
            left: outer.left + shift,
            right: outer.right + shift,
            ..outer
        };
        let hidden_x = if left {
            expanded.left - (frame.right - frame.left) + strip
        } else {
            expanded.left + (frame.right - frame.left) - strip
        };
        Some(Self {
            expanded,
            insets: RECT {
                left: frame.left - outer.left,
                top: frame.top - outer.top,
                right: outer.right - frame.right,
                bottom: outer.bottom - frame.bottom,
            },
            hidden_x,
            work,
            dpi,
        })
    }
    fn rect(self, progress: f64) -> RECT {
        let x = (f64::from(self.expanded.left)
            + f64::from(self.hidden_x - self.expanded.left) * progress)
            .round() as i32;
        RECT {
            left: x,
            right: x + self.expanded.right - self.expanded.left,
            ..self.expanded
        }
    }
    /// 设置区域后 DWM 会报告完整外框；显式排除原来不可见的调整大小边框，边条才真正只有 2 DIP。
    fn clip(self, rect: RECT) -> RECT {
        RECT {
            left: self.work.left.max(rect.left + self.insets.left),
            top: self.work.top.max(rect.top + self.insets.top),
            right: self.work.right.min(rect.right - self.insets.right),
            bottom: self.work.bottom.min(rect.bottom - self.insets.bottom),
        }
    }
    fn recover(self, current: RECT, work: RECT) -> RECT {
        let width = current.right - current.left;
        let height = current.bottom - current.top;
        let left = self
            .expanded
            .left
            .clamp(work.left, (work.right - width).max(work.left));
        let top = self
            .expanded
            .top
            .clamp(work.top, (work.bottom - height).max(work.top));
        RECT {
            left,
            top,
            right: left + width,
            bottom: top + height,
        }
    }
}

/// progress 为 0 时完整展开、1 时只剩边条。连续悬停才能展开，反向动画从当前帧接续。
#[derive(Default)]
struct Motion {
    start: Option<Instant>,
    from: f64,
    hidden: bool,
    hover: Option<Instant>,
    hold: bool,
}
impl Motion {
    fn progress(&self, now: Instant) -> f64 {
        let to = if self.hidden { 1.0 } else { 0.0 };
        let Some(start) = self.start else { return to };
        let t = (now.duration_since(start).as_secs_f64() / 0.15).min(1.0);
        self.from + (to - self.from) * (1.0 - (1.0 - t).powi(2))
    }
    fn step(
        &mut self,
        now: Instant,
        inside: bool,
        blocked: bool,
        focused: bool,
        delay: u64,
    ) -> f64 {
        if self.hold && (inside || !focused) {
            self.hold = false;
        }
        let progress = self.progress(now);
        let hide = if blocked || self.hold {
            self.hover = None;
            false
        } else if inside {
            let since = *self.hover.get_or_insert(now);
            self.hidden && now.duration_since(since) < Duration::from_millis(delay)
        } else {
            self.hover = None;
            true
        };
        if hide != self.hidden {
            self.from = progress;
            self.start = Some(now);
            self.hidden = hide;
        }
        self.progress(now)
    }
}

#[derive(Default)]
pub(super) struct EdgeHide {
    anchor: Option<Anchor>,
    motion: Motion,
    moving: bool,
    last_rect: Option<RECT>,
    pending: Arc<AtomicBool>,
    failed: Arc<AtomicBool>,
    /// 显式唤起还可能发生在上一帧移动未完成时，保留意图到界面线程回执之后。
    reveal: bool,
}

fn contains(rect: RECT, point: POINT) -> bool {
    point.x >= rect.left && point.x < rect.right && point.y >= rect.top && point.y < rect.bottom
}

impl EdgeHide {
    pub fn ready(&self) -> bool {
        !self.pending.load(Ordering::Acquire)
    }
    pub fn reveal(&mut self) {
        self.reveal = true;
    }

    /// 不同步发送窗口消息。队列最多一帧，完成前暂停其他定位，防止旧帧覆盖吸附或恢复位置。
    fn place(
        &mut self,
        state: &NativeState,
        main: HWND,
        rect: RECT,
        clip: Option<RECT>,
        fallback: RECT,
    ) {
        if self.last_rect == Some(rect) && clip.is_some() {
            return;
        }
        let Some(app) = state.app.get() else {
            return;
        };
        self.last_rect = Some(rect);
        let handle = main.0 as i64;
        let pending = self.pending.clone();
        let failed = self.failed.clone();
        pending.store(true, Ordering::Release);
        let result = app.run_on_main_thread(move || unsafe {
            let main = hwnd(handle);
            let region = clip.map(|work| {
                CreateRectRgn(
                    (work.left - rect.left).max(0),
                    (work.top - rect.top).max(0),
                    (work.right - rect.left).min(rect.right - rect.left),
                    (work.bottom - rect.top).min(rect.bottom - rect.top),
                )
            });
            let clipped =
                region.is_none_or(|r| !r.is_invalid()) && SetWindowRgn(main, region, true) != 0;
            if !clipped {
                if let Some(region) = region {
                    let _ = DeleteObject(region.into());
                }
            }
            // 区域成功后由 Windows 持有；失败时恢复完整窗口，不让用户困在不可见边条里。
            let moved = clipped
                && SetWindowPos(
                    main,
                    None,
                    rect.left,
                    rect.top,
                    0,
                    0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER,
                )
                .is_ok();
            if !moved {
                log::error!("贴边窗口显示失败，已尝试恢复完整窗口。");
                let _ = SetWindowRgn(main, None, true);
                let _ = SetWindowPos(
                    main,
                    None,
                    fallback.left,
                    fallback.top,
                    0,
                    0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER,
                );
                failed.store(true, Ordering::Release);
            }
            pending.store(false, Ordering::Release);
        });
        if let Err(error) = result {
            self.pending.store(false, Ordering::Release);
            self.failed.store(true, Ordering::Release);
            state.notify_error(format!("无法更新贴边窗口：{error}"));
        }
    }

    /// 模式切换前先恢复展开矩形和完整区域；返回 false 表示仍有界面线程动作待完成。
    pub unsafe fn clear(&mut self, state: &NativeState, main: HWND) -> bool {
        if self.pending.load(Ordering::Acquire) {
            return false;
        }
        if let Some(anchor) = self.anchor.take() {
            self.place(state, main, anchor.expanded, None, anchor.expanded);
        }
        self.motion = Motion::default();
        self.moving = false;
        self.reveal = false;
        self.last_rect = None;
        !self.pending.load(Ordering::Acquire)
    }

    pub unsafe fn update(&mut self, state: &NativeState, main: HWND, foreground: HWND) {
        if self.pending.load(Ordering::Acquire) {
            return;
        }
        if self.failed.swap(false, Ordering::AcqRel) {
            self.anchor = None;
            self.last_rect = None;
            self.motion = Motion::default();
            state.notify_error("贴边隐藏暂未生效，主窗口已恢复；可重新拖到边缘再试。".into());
        }
        let settings = state.settings.read().unwrap().clone();
        if !settings.edge_auto_hide || !IsWindowVisible(main).as_bool() || IsIconic(main).as_bool()
        {
            self.clear(state, main);
            return;
        }
        let mut gui = GUITHREADINFO {
            cbSize: size_of::<GUITHREADINFO>() as u32,
            ..Default::default()
        };
        if GetGUIThreadInfo(GetWindowThreadProcessId(main, None), &mut gui).is_err() {
            return;
        }
        let moving = gui.flags.0 & GUI_INMOVESIZE.0 != 0;
        let mut rect = RECT::default();
        if GetWindowRect(main, &mut rect).is_err() {
            return;
        }
        if moving {
            // 开始拖动时撤销裁剪，但绝不把正在被用户移动的窗口拉回旧位置。
            if self.anchor.take().is_some() {
                self.place(state, main, rect, None, rect);
                self.motion = Motion::default();
                self.last_rect = None;
            }
            self.moving = true;
            return;
        }
        let mut monitor = MONITORINFO {
            cbSize: size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        // 收起后的大部分外框在屏幕外，必须使用展开位置确定所属显示器。
        let reference = self.anchor.map_or(rect, |a| a.expanded);
        if !GetMonitorInfoW(
            MonitorFromRect(&reference, MONITOR_DEFAULTTONEAREST),
            &mut monitor,
        )
        .as_bool()
        {
            return;
        }
        let dpi = GetDpiForWindow(main).max(96);
        if self.moving {
            self.moving = false;
            let mut frame = rect;
            let _ = DwmGetWindowAttribute(
                main,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                std::ptr::from_mut(&mut frame).cast(),
                size_of::<RECT>() as u32,
            );
            self.anchor = Anchor::new(rect, frame, monitor.rcWork, dpi);
            self.last_rect = None;
        }
        let Some(anchor) = self.anchor else {
            self.reveal = false;
            return;
        };
        // 显示器工作区、DPI 或外部定位改变时取消旧几何；不能把旧屏幕的隐藏坐标当作新位置。
        if anchor.work != monitor.rcWork || anchor.dpi != dpi {
            // 拔掉屏幕或系统改变 DPI 时，按当前实际尺寸恢复到仍可访问的工作区。
            self.anchor = Some(Anchor {
                expanded: anchor.recover(rect, monitor.rcWork),
                ..anchor
            });
            self.clear(state, main);
            return;
        }
        if self.last_rect.is_some_and(|expected| expected != rect) {
            self.anchor = None;
            self.place(state, main, rect, None, rect);
            self.last_rect = None;
            self.motion = Motion::default();
            return;
        }
        let mut pointer = POINT::default();
        if GetCursorPos(&mut pointer).is_err() {
            return;
        }
        let inside = contains(anchor.clip(rect), pointer);
        let preview_open = state.preview.lock().unwrap().source == main.0 as i64
            && state
                .windows
                .get()
                .is_some_and(|windows| IsWindowVisible(hwnd(windows.2)).as_bool());
        // 同一 UI 线程也承载设置窗口：菜单、捕获和输入只在属于主窗口时阻止其收起。
        let settings_window = state.management_windows.settings.load(Ordering::Acquire);
        let settings_foreground = settings_window != 0 && foreground.0 as i64 == settings_window;
        let blocked = (!settings_foreground
            && (gui.flags.0 & (GUI_INMENUMODE.0 | GUI_POPUPMENUMODE.0 | GUI_SYSTEMMENUMODE.0)
                != 0
                || !gui.hwndCapture.0.is_null()))
            || GetAsyncKeyState(VK_LBUTTON.0 as i32) < 0
            || GetAsyncKeyState(VK_RBUTTON.0 as i32) < 0
            || state.busy.load(Ordering::Acquire)
            || state.management_windows.blocks_edge_hide()
            || (state.shortcut_input.load(Ordering::Acquire) != 0 && !settings_foreground)
            || preview_open;
        if self.reveal {
            self.reveal = false;
            self.motion = Motion {
                hold: true,
                ..Default::default()
            };
            self.place(state, main, anchor.expanded, None, anchor.expanded);
            return;
        }
        let progress = self.motion.step(
            Instant::now(),
            inside,
            blocked,
            foreground == main,
            settings.edge_reveal_delay,
        );
        let next = anchor.rect(progress);
        if self.last_rect != Some(next) {
            self.place(
                state,
                main,
                next,
                (progress > 0.0).then_some(anchor.clip(next)),
                anchor.expanded,
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn rect(l: i32, t: i32, r: i32, b: i32) -> RECT {
        RECT {
            left: l,
            top: t,
            right: r,
            bottom: b,
        }
    }
    #[test]
    fn edges_preserve_width_and_leave_two_dip_at_each_scale() {
        for dpi in [96, 144, 192] {
            let work = rect(-1920, 0, 0, 1040);
            let strip = (2 * dpi / 96) as i32;
            for outer in [rect(-1920, 100, -1590, 820), rect(-330, 100, 0, 820)] {
                let a = Anchor::new(outer, outer, work, dpi).unwrap();
                let hidden = a.rect(1.0);
                assert_eq!(hidden.right - hidden.left, 330);
                assert_eq!(
                    (hidden.right.min(work.right) - hidden.left.max(work.left)).max(0),
                    strip
                );
            }
            assert!(
                Anchor::new(rect(-342, 0, -12, 720), rect(-342, 0, -12, 720), work, dpi).is_none()
            );
        }
    }
    #[test]
    fn dragged_past_edge_still_hides_and_reveals_at_work_area_boundary() {
        for dpi in [96, 120, 144, 192] {
            let work = rect(-1920, 0, 0, 1032);
            let strip = (2.0 * f64::from(dpi) / 96.0).round() as i32;
            for overshoot in [13, 40, 120] {
                for left in [true, false] {
                    let x = if left {
                        work.left - overshoot
                    } else {
                        -332 + overshoot
                    };
                    let frame = rect(x, 215, x + 332, 937);
                    let outer = rect(x - 7, 215, x + 339, 944);
                    let anchor = Anchor::new(outer, frame, work, dpi)
                        .expect("拖过边缘但仍与工作区相交时应进入贴边状态");
                    let expanded = anchor.clip(anchor.rect(0.0));
                    assert_eq!(expanded.right - expanded.left, 332);
                    assert_eq!(
                        if left { expanded.left } else { expanded.right },
                        if left { work.left } else { work.right }
                    );
                    let hidden = anchor.clip(anchor.rect(1.0));
                    assert_eq!(hidden.right - hidden.left, strip);
                    assert_eq!(anchor.rect(1.0).right - anchor.rect(1.0).left, 346);
                }
            }
            for frame in [
                rect(-1908, 215, -1576, 937),
                rect(-344, 215, -12, 937),
                rect(-2252, 215, -1920, 937),
                rect(0, 215, 332, 937),
            ] {
                assert!(Anchor::new(frame, frame, work, dpi).is_none());
            }
        }
    }
    #[test]
    fn hidden_region_excludes_invisible_resize_borders() {
        let work = rect(0, 0, 1920, 1032);
        let outer = rect(1581, 151, 1927, 880);
        let frame = rect(1588, 151, 1920, 873);
        let anchor = Anchor::new(outer, frame, work, 96).unwrap();
        let hidden = anchor.rect(1.0);
        assert_eq!(hidden.right - hidden.left, 346);
        assert_eq!(anchor.clip(hidden), rect(1918, 151, 1920, 873));
        assert_eq!(anchor.clip(anchor.expanded), frame);
        let resized = rect(1911, 151, 2430, 1244);
        let recovered = anchor.recover(resized, rect(-1920, 0, 0, 1032));
        assert_eq!(recovered.right, 0);
        assert_eq!(recovered.left, -519);
        assert_eq!(recovered.top, 0);
    }
    #[test]
    fn brief_hover_cancels_and_reentry_starts_full_delay() {
        let t = Instant::now();
        let mut m = Motion {
            hidden: true,
            ..Default::default()
        };
        m.step(t, true, false, false, 500);
        assert_eq!(
            m.step(t + Duration::from_millis(220), false, false, false, 500),
            1.0
        );
        m.step(t + Duration::from_millis(300), true, false, false, 500);
        assert_eq!(
            m.step(t + Duration::from_millis(799), true, false, false, 500),
            1.0
        );
        m.step(t + Duration::from_millis(800), true, false, false, 500);
        assert_eq!(
            m.step(t + Duration::from_millis(950), true, false, false, 500),
            0.0
        );
    }
    #[test]
    fn reverse_continues_from_current_frame_and_zero_delay_reveals() {
        let t = Instant::now();
        let mut m = Motion::default();
        m.step(t, false, false, false, 0);
        let partial = m.progress(t + Duration::from_millis(60));
        assert!(partial > 0.0 && partial < 1.0);
        assert_eq!(
            m.step(t + Duration::from_millis(60), true, false, false, 0),
            partial
        );
        assert_eq!(
            m.step(t + Duration::from_millis(210), true, false, false, 0),
            0.0
        );
    }
    #[test]
    fn explicit_show_holds_until_pointer_visit_or_focus_loss_and_input_pauses() {
        let t = Instant::now();
        let mut m = Motion {
            hold: true,
            ..Default::default()
        };
        assert_eq!(m.step(t, false, false, true, 500), 0.0);
        m.step(t, true, false, true, 500);
        m.step(t, false, false, true, 500);
        assert_eq!(
            m.step(t + Duration::from_millis(150), false, false, true, 500),
            1.0
        );
        m.step(t + Duration::from_millis(150), false, true, true, 500);
        assert_eq!(
            m.step(t + Duration::from_millis(300), false, true, true, 500),
            0.0
        );
        let mut m = Motion {
            hold: true,
            ..Default::default()
        };
        m.step(t, false, false, false, 500);
        assert_eq!(
            m.step(t + Duration::from_millis(150), false, false, false, 500),
            1.0
        );
    }
}
