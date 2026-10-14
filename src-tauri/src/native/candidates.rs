use crate::{
    delivery::Mode,
    model::Phrase,
    native::{shortcuts::KeyEvent, TargetContext},
};
use serde::Serialize;
use std::collections::HashSet;

/// 候选快照绑定触发时的目标，不因随后更新的目标记录而转投其他输入区。
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CandidatePayload {
    pub shortcut: String,
    pub phrases: Vec<Phrase>,
    pub selected_index: usize,
    pub target: TargetContext,
}

#[derive(Default)]
pub struct CandidateAction {
    pub swallowed: bool,
    pub changed: bool,
    pub submit: Option<(usize, Mode)>,
}

/// 原生层拥有选择状态。消费键保持到松开，候选关闭或发送开始后长按也不会重复提交。
/// 无匹配序列的回放由 ShortcutMatcher 负责；已匹配候选取消时不修改原草稿。
#[derive(Default)]
pub struct CandidateState {
    pub payload: Option<CandidatePayload>,
    pub context: Option<(i64, i64)>,
    consumed: HashSet<u32>,
    submitting: bool,
}
impl CandidateState {
    pub fn open(&mut self, payload: CandidatePayload, context: (i64, i64)) {
        if let Ok(key) = crate::native::parse_shortcut(&payload.shortcut) {
            self.consumed.insert(key.next_key.unwrap_or(key.key));
        }
        self.payload = Some(payload);
        self.context = Some(context);
        self.submitting = false;
    }
    pub fn cancel(&mut self) -> bool {
        self.context = None;
        self.submitting = false;
        self.payload.take().is_some()
    }
    pub fn valid(&self, context: Option<(i64, i64)>) -> bool {
        self.payload.is_some() && self.context == context
    }
    pub fn select(&mut self, index: usize) -> crate::model::Result<()> {
        let payload = self.payload.as_mut().ok_or("快捷键候选已关闭。")?;
        if index >= payload.phrases.len() {
            return Err("候选话术已不存在。".into());
        }
        payload.selected_index = index;
        Ok(())
    }
    pub fn take(&mut self, index: usize) -> crate::model::Result<(String, TargetContext)> {
        self.select(index)?;
        let payload = self.payload.take().unwrap();
        self.context = None;
        self.submitting = false;
        Ok((payload.phrases[index].id.clone(), payload.target))
    }
    pub fn event(
        &mut self,
        event: KeyEvent,
        context: Option<(i64, i64)>,
        modifiers: u32,
    ) -> CandidateAction {
        if self.consumed.contains(&event.key) {
            if !event.down {
                self.consumed.remove(&event.key);
            }
            return CandidateAction {
                swallowed: true,
                ..Default::default()
            };
        }
        if self.payload.is_none() {
            return CandidateAction::default();
        }
        if !self.valid(context) {
            return CandidateAction {
                changed: self.cancel(),
                ..Default::default()
            };
        }
        if !event.down
            || [
                0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5,
            ]
            .contains(&event.key)
        {
            return CandidateAction::default();
        }
        if modifiers != 0 {
            return CandidateAction {
                changed: self.cancel(),
                ..Default::default()
            };
        }
        let payload = self.payload.as_mut().unwrap();
        let mut action = CandidateAction {
            swallowed: true,
            ..Default::default()
        };
        if self.submitting {
            self.consumed.insert(event.key);
            return action;
        }
        match event.key {
            0x26 | 0x28 => {
                payload.selected_index = if event.key == 0x26 {
                    payload.selected_index.saturating_sub(1)
                } else {
                    (payload.selected_index + 1).min(payload.phrases.len() - 1)
                };
                action.changed = true;
            }
            0x1B => {
                action.changed = self.cancel();
            }
            0x20 | 0x0D | 0x30..=0x39 => {
                let index = match event.key {
                    0x30 => 9,
                    0x31..=0x39 => (event.key - 0x31) as usize,
                    _ => payload.selected_index,
                };
                if index < payload.phrases.len() {
                    self.submitting = true;
                    action.submit = Some((
                        index,
                        if event.key == 0x0D {
                            Mode::Send
                        } else {
                            Mode::Insert
                        },
                    ));
                }
            }
            _ => {
                action.swallowed = false;
                action.changed = self.cancel();
            }
        }
        if action.swallowed {
            self.consumed.insert(event.key);
        }
        action
    }
}

#[cfg(windows)]
mod window {
    use super::*;
    use crate::native::{
        platform::{capture, hwnd},
        NativeState,
    };
    use std::{mem::size_of, sync::atomic::Ordering};
    use tauri::Emitter;
    use windows::Win32::{
        Foundation::*,
        Graphics::Gdi::*,
        UI::{HiDpi::GetDpiForWindow, WindowsAndMessaging::*},
    };

    /// 候选不激活窗口，焦点始终留在触发的输入控件。提交前仍核对窗口与控件句柄。
    pub(crate) unsafe fn input_context() -> Option<(i64, i64)> {
        let foreground = GetForegroundWindow();
        let mut gui = GUITHREADINFO {
            cbSize: size_of::<GUITHREADINFO>() as u32,
            ..Default::default()
        };
        if foreground.0.is_null()
            || GetGUIThreadInfo(GetWindowThreadProcessId(foreground, None), &mut gui).is_err()
            || gui.hwndFocus.0.is_null()
        {
            return None;
        }
        Some((foreground.0 as i64, gui.hwndFocus.0 as i64))
    }

    /// 光标位置优先；不可用时靠近目标窗口下方。统一夹在工作区内，兼容负坐标显示器和不同 DPI。
    fn bounds(anchor: RECT, caret: bool, work: RECT, width: i32, height: i32, gap: i32) -> RECT {
        let width = width.min(work.right - work.left);
        let height = height.min(work.bottom - work.top);
        let x = anchor.left.clamp(work.left, work.right - width);
        let y = if caret {
            anchor.top - gap - height
        } else {
            anchor.bottom - gap - height
        }
        .clamp(work.top, work.bottom - height);
        RECT {
            left: x,
            top: y,
            right: x + width,
            bottom: y + height,
        }
    }

    impl NativeState {
        pub fn candidate_payload(&self) -> Option<CandidatePayload> {
            self.candidates.lock().unwrap().payload.clone()
        }
        pub fn publish_candidates(&self) {
            let payload = self.candidate_payload();
            if payload.is_none() {
                if let Some(window) = self.candidate_window.get() {
                    unsafe {
                        let _ = ShowWindow(hwnd(*window), SW_HIDE);
                    }
                }
            }
            if let Some(app) = self.app.get() {
                let _ = app.emit_to("shortcut-candidates", "shortcut-candidates", payload);
            }
        }
        pub fn cancel_candidates(&self) {
            if self.candidates.lock().unwrap().cancel() {
                self.publish_candidates();
            }
        }
        pub fn check_candidates(&self) {
            let mut state = self.candidates.lock().unwrap();
            let context = unsafe { input_context() };
            if state.payload.is_some()
                && (!state.valid(context)
                    || self.management_windows.is_open()
                    || self.busy.load(Ordering::Acquire))
            {
                log::info!(
                    "快捷键候选已取消：输入焦点 {:?} → {:?}，管理界面={}，发送中={}。",
                    state.context,
                    context,
                    self.management_windows.is_open(),
                    self.busy.load(Ordering::Acquire)
                );
                state.cancel();
                drop(state);
                self.publish_candidates();
            }
        }
        pub fn select_candidate(&self, index: usize) -> crate::model::Result<()> {
            self.check_candidates();
            self.candidates.lock().unwrap().select(index)?;
            self.publish_candidates();
            Ok(())
        }
        pub fn take_candidate(
            &self,
            index: usize,
        ) -> crate::model::Result<(String, TargetContext)> {
            self.check_candidates();
            let result = self.candidates.lock().unwrap().take(index);
            self.publish_candidates();
            result
        }
        pub(crate) unsafe fn fire_shortcut(&self, shortcut: &str) {
            let Some(context) = input_context() else {
                return;
            };
            let target = capture().or_else(|| self.target.lock().unwrap().clone());
            let Some(target) = target else {
                self.notify_error("请先聚焦目标输入区，再使用快捷键。".into());
                return;
            };
            *self.target.lock().unwrap() = Some(target.clone());
            let settings = self.settings.read().unwrap().clone();
            if settings.sequence_hotkeys {
                if let Some(index) = settings.sequence_bindings.iter().position(|s| {
                    crate::native::parse_shortcut(s).is_ok_and(|s| s.canonical() == shortcut)
                }) {
                    let mapping = self.sequence_map.read().unwrap();
                    if !mapping.1 {
                        if let Some(Some(id)) = mapping.0.get(index) {
                            crate::queue_shortcut_delivery(
                                self.app.get().unwrap().clone(),
                                id.clone(),
                                if settings.sequence_direct_send {
                                    Mode::Send
                                } else {
                                    Mode::Insert
                                },
                                target,
                            );
                        }
                    }
                    return;
                }
            }
            if !settings.custom_hotkeys {
                return;
            }
            let phrases = self
                .shortcut_phrases
                .read()
                .unwrap()
                .iter()
                .filter(|p| {
                    crate::native::parse_shortcut(&p.shortcut)
                        .is_ok_and(|s| s.canonical() == shortcut)
                })
                .cloned()
                .collect::<Vec<_>>();
            if phrases.is_empty() {
                return;
            }
            self.hide_preview(None);
            let payload = CandidatePayload {
                shortcut: shortcut.into(),
                phrases,
                selected_index: 0,
                target,
            };
            self.candidates.lock().unwrap().open(payload, context);
            self.publish_candidates();
        }
        pub fn resize_candidates(&self) -> crate::model::Result<()> {
            self.check_candidates();
            let state = self.candidates.lock().unwrap();
            let Some(payload) = &state.payload else {
                return Ok(());
            };
            let window = hwnd(*self.candidate_window.get().ok_or("候选窗口尚未创建。")?);
            let target = hwnd(payload.target.hwnd);
            unsafe {
                let monitor = MonitorFromWindow(target, MONITOR_DEFAULTTONEAREST);
                let mut info = MONITORINFO {
                    cbSize: size_of::<MONITORINFO>() as u32,
                    ..Default::default()
                };
                if !GetMonitorInfoW(monitor, &mut info).as_bool() {
                    return Err("无法读取候选所在的屏幕工作区。".into());
                }
                let scale = GetDpiForWindow(target).max(96) as f64 / 96.0;
                let mut gui = GUITHREADINFO {
                    cbSize: size_of::<GUITHREADINFO>() as u32,
                    ..Default::default()
                };
                let mut anchor = RECT::default();
                GetWindowRect(target, &mut anchor)
                    .map_err(|e| format!("无法读取候选目标位置：{e}"))?;
                let mut caret = false;
                if GetGUIThreadInfo(GetWindowThreadProcessId(target, None), &mut gui).is_ok()
                    && gui.hwndFocus.0 as i64 == payload.target.focus
                    && !gui.hwndCaret.0.is_null()
                    && gui.rcCaret.bottom > gui.rcCaret.top
                {
                    let mut point = POINT {
                        x: gui.rcCaret.left,
                        y: gui.rcCaret.top,
                    };
                    if ClientToScreen(gui.hwndCaret, &mut point).as_bool() {
                        anchor.left = point.x;
                        anchor.top = point.y;
                        caret = true;
                    }
                }
                let gap = (6.0 * scale).round() as i32;
                let work = RECT {
                    left: info.rcWork.left + gap,
                    top: info.rcWork.top + gap,
                    right: info.rcWork.right - gap,
                    bottom: info.rcWork.bottom - gap,
                };
                let rect = bounds(
                    anchor,
                    caret,
                    work,
                    (370.0 * scale).round() as i32,
                    ((54 + 30 * payload.phrases.len().min(10)) as f64 * scale).round() as i32,
                    gap,
                );
                SetWindowPos(
                    window,
                    Some(HWND_TOPMOST),
                    rect.left,
                    rect.top,
                    rect.right - rect.left,
                    rect.bottom - rect.top,
                    SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_SHOWWINDOW,
                )
                .map_err(|e| format!("无法显示快捷键候选：{e}"))?;
            }
            Ok(())
        }
    }
    #[cfg(test)]
    mod tests {
        use super::*;
        #[test]
        fn candidates_stay_above_caret_and_inside_work_area_at_all_scales() {
            for origin in [-1920, 0, 1920] {
                for scale in [1.0, 1.5, 2.0] {
                    let work = RECT {
                        left: origin,
                        top: 0,
                        right: origin + 1920,
                        bottom: 1040,
                    };
                    for (x, y) in [(900, 900), (1910, 20)] {
                        let anchor = RECT {
                            left: origin + x,
                            top: y,
                            right: origin + x + 1,
                            bottom: y + 20,
                        };
                        let rect = bounds(
                            anchor,
                            true,
                            work,
                            (370.0 * scale) as i32,
                            (354.0 * scale) as i32,
                            6,
                        );
                        assert!(
                            rect.left >= work.left
                                && rect.top >= work.top
                                && rect.right <= work.right
                                && rect.bottom <= work.bottom
                        );
                        if y == 900 {
                            assert!(rect.bottom < anchor.top);
                        }
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn state() -> CandidateState {
        let mut state = CandidateState::default();
        let phrase = Phrase {
            id: "A".into(),
            root_id: "R".into(),
            group_id: None,
            title: "A".into(),
            color: String::new(),
            background: String::new(),
            shortcut: "Q+W".into(),
            position: 0,
            segments: vec![],
        };
        state.open(
            CandidatePayload {
                shortcut: "Q+W".into(),
                phrases: (0..12)
                    .map(|i| Phrase {
                        id: i.to_string(),
                        ..phrase.clone()
                    })
                    .collect(),
                selected_index: 0,
                target: TargetContext {
                    hwnd: 10,
                    focus: 11,
                    pid: 12,
                },
            },
            (10, 11),
        );
        state
    }
    fn key(key: u32, down: bool) -> KeyEvent {
        KeyEvent {
            key,
            down,
            extended: false,
        }
    }
    #[test]
    fn selects_beyond_ten_and_uses_distinct_insert_send_actions() {
        for (key_code, index, mode) in [
            (0x30, 9, Mode::Insert),
            (0x33, 2, Mode::Insert),
            (0x20, 11, Mode::Insert),
            (0x0D, 11, Mode::Send),
        ] {
            let mut s = state();
            s.select(11).unwrap();
            let action = s.event(key(key_code, true), Some((10, 11)), 0);
            assert_eq!(action.submit, Some((index, mode)));
            assert!(s
                .event(key(key_code, true), Some((10, 11)), 0)
                .submit
                .is_none());
            s.take(index).unwrap();
            assert!(s.event(key(key_code, false), Some((10, 11)), 0).swallowed);
        }
    }
    #[test]
    fn navigation_escape_and_changed_input_never_submit() {
        let mut s = state();
        s.event(key(0x28, true), Some((10, 11)), 0);
        assert_eq!(s.payload.as_ref().unwrap().selected_index, 1);
        s.event(key(0x28, false), Some((10, 11)), 0);
        let changed = s.event(key(0x0D, true), Some((10, 99)), 0);
        assert!(changed.changed);
        assert!(changed.submit.is_none());
        assert!(!changed.swallowed);
        let mut s = state();
        assert!(s.event(key(0x1B, true), Some((10, 11)), 0).swallowed);
        assert!(s.payload.is_none());
    }
}
