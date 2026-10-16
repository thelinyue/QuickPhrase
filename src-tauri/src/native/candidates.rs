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
    pub session_id: String,
    pub layout_key: String,
    pub max_width: f64,
    pub max_height: f64,
    pub shortcut: String,
    pub phrases: Vec<Phrase>,
    pub selected_index: usize,
    pub target: TargetContext,
    /// 成员候选必须沿用触发时的账号，防止切号后相同来源引用被旧候选投递。
    pub member_epoch: Option<u64>,
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
    /// 仅记录本次候选已通过身份校验的测量；移动目标时复用尺寸，不要求前端重测正文。
    measured_size: Option<(f64, f64)>,
}
impl CandidateState {
    pub fn open(&mut self, payload: CandidatePayload, context: (i64, i64)) {
        if let Ok(key) = crate::native::parse_shortcut(&payload.shortcut) {
            self.consumed.insert(key.next_key.unwrap_or(key.key));
        }
        self.payload = Some(payload);
        self.context = Some(context);
        self.submitting = false;
        self.measured_size = None;
    }
    pub fn cancel(&mut self) -> bool {
        self.measured_size = None;
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
        self.measured_size = None;
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

    /// 输入区优先上方、其次下方；两侧不足时选择较宽裕的一侧。回退窗口锚点已内缩12 DIP。
    fn bounds(anchor: RECT, caret: bool, work: RECT, width: i32, height: i32, gap: i32) -> RECT {
        let width = width.min(work.right - work.left);
        let height = height.min(work.bottom - work.top);
        let x = anchor.left.clamp(work.left, work.right - width);
        let above = anchor.top - gap - work.top;
        let below = work.bottom - anchor.bottom - gap;
        let y = if caret {
            if above >= height || (below < height && above >= below) {
                anchor.top - gap - height
            } else {
                anchor.bottom + gap
            }
        } else {
            anchor.bottom - height
        }
        .clamp(work.top, work.bottom - height);
        RECT {
            left: x,
            top: y,
            right: x + width,
            bottom: y + height,
        }
    }

    fn intersection(a: RECT, b: RECT) -> Option<RECT> {
        let rect = RECT {
            left: a.left.max(b.left),
            top: a.top.max(b.top),
            right: a.right.min(b.right),
            bottom: a.bottom.min(b.bottom),
        };
        (rect.right > rect.left && rect.bottom > rect.top).then_some(rect)
    }

    /// 浏览器等应用可能把整个客户区报告为焦点控件，不能把它当成实际输入区。
    fn input_rect(focus: RECT, client: RECT) -> bool {
        intersection(focus, client).is_some()
            && !((focus.right - focus.left) as i64 * 10 >= (client.right - client.left) as i64 * 9
                && (focus.bottom - focus.top) as i64 * 10
                    >= (client.bottom - client.top) as i64 * 9)
    }

    /// 仅接受当前目标内的可见光标和控件；窗口级回退使用可见交集，避免出屏窗口拉走候选。
    unsafe fn input_anchor(
        target: &TargetContext,
        work: RECT,
        scale: f64,
    ) -> crate::model::Result<(RECT, bool)> {
        let window = hwnd(target.hwnd);
        let mut outer = RECT::default();
        GetWindowRect(window, &mut outer).map_err(|_| "无法读取候选目标位置。")?;
        let visible = intersection(outer, work).unwrap_or(work);
        let mut client = RECT::default();
        GetClientRect(window, &mut client).map_err(|_| "无法读取候选目标输入区域。")?;
        let mut origin = POINT::default();
        if ClientToScreen(window, &mut origin).as_bool() {
            client.left += origin.x;
            client.right += origin.x;
            client.top += origin.y;
            client.bottom += origin.y;
        } else {
            client = outer;
        }
        let mut gui = GUITHREADINFO {
            cbSize: size_of::<GUITHREADINFO>() as u32,
            ..Default::default()
        };
        if GetGUIThreadInfo(GetWindowThreadProcessId(window, None), &mut gui).is_ok()
            && gui.hwndFocus.0 as i64 == target.focus
            && GetAncestor(gui.hwndFocus, GA_ROOT) == window
            && IsWindowVisible(gui.hwndFocus).as_bool()
        {
            if !gui.hwndCaret.0.is_null()
                && GetAncestor(gui.hwndCaret, GA_ROOT) == window
                && IsWindowVisible(gui.hwndCaret).as_bool()
                && gui.rcCaret.bottom > gui.rcCaret.top
            {
                let mut point = POINT {
                    x: gui.rcCaret.left,
                    y: gui.rcCaret.top,
                };
                if ClientToScreen(gui.hwndCaret, &mut point).as_bool() {
                    let caret = RECT {
                        left: point.x,
                        top: point.y,
                        right: point.x + (gui.rcCaret.right - gui.rcCaret.left).max(1),
                        bottom: point.y + gui.rcCaret.bottom - gui.rcCaret.top,
                    };
                    if intersection(caret, visible).is_some() {
                        return Ok((caret, true));
                    }
                }
            }
            let mut focus = RECT::default();
            if gui.hwndFocus != window
                && GetWindowRect(gui.hwndFocus, &mut focus).is_ok()
                && input_rect(focus, client)
            {
                if let Some(rect) = intersection(focus, visible) {
                    return Ok((rect, true));
                }
            }
        }
        let inset = (12.0 * scale).round() as i32;
        Ok((
            RECT {
                left: (visible.left + inset).min(visible.right),
                bottom: (visible.bottom - inset).max(visible.top),
                ..visible
            },
            false,
        ))
    }

    /// 布局上限扣除原生非客户区；跨屏时以目标窗口 DPI 换算，再由布局身份拒绝旧测量。
    unsafe fn layout_limits(
        target: HWND,
        window: HWND,
    ) -> crate::model::Result<(f64, f64, String, i32, i32)> {
        let mut info = MONITORINFO {
            cbSize: size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        if !GetMonitorInfoW(
            MonitorFromWindow(target, MONITOR_DEFAULTTONEAREST),
            &mut info,
        )
        .as_bool()
        {
            return Err("无法读取候选屏幕工作区。".into());
        }
        let dpi = GetDpiForWindow(target).max(96);
        let scale = dpi as f64 / 96.0;
        let mut outer = RECT::default();
        let mut client = RECT::default();
        GetWindowRect(window, &mut outer).map_err(|_| "无法读取候选窗口范围。")?;
        GetClientRect(window, &mut client).map_err(|_| "无法读取候选内容范围。")?;
        let ratio = dpi as f64 / GetDpiForWindow(window).max(96) as f64;
        let ix = ((outer.right - outer.left - client.right + client.left).max(0) as f64 * ratio)
            .round() as i32;
        let iy = ((outer.bottom - outer.top - client.bottom + client.top).max(0) as f64 * ratio)
            .round() as i32;
        let work = info.rcWork;
        let width = (360.0_f64.min((work.right - work.left) as f64 / scale * 0.7)
            - ix as f64 / scale)
            .max(1.0);
        let height = (420.0_f64.min((work.bottom - work.top) as f64 / scale * 0.5)
            - iy as f64 / scale)
            .max(1.0);
        Ok((
            width,
            height,
            format!(
                "{}:{}:{}:{}:{dpi}",
                work.left, work.top, work.right, work.bottom
            ),
            ix,
            iy,
        ))
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
                return;
            }
            if let (Some(payload), Some(window)) = (&mut state.payload, self.candidate_window.get())
            {
                if let Ok((width, height, key, _, _)) =
                    unsafe { layout_limits(hwnd(payload.target.hwnd), hwnd(*window)) }
                {
                    if payload.layout_key != key {
                        payload.layout_key = key;
                        payload.max_width = width;
                        payload.max_height = height;
                        state.measured_size = None;
                        drop(state);
                        self.publish_candidates();
                        return;
                    }
                }
            }
            if let (Some(payload), Some(window), Some((width, height))) = (
                &state.payload,
                self.candidate_window.get(),
                state.measured_size,
            ) {
                // 沿用已有焦点巡检；同屏移动只更新原生坐标，不发布新快照或触发正文测量。
                unsafe {
                    let _ = position_candidates(payload, hwnd(*window), width, height);
                }
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
        pub(crate) unsafe fn fire_shortcut(
            &self,
            shortcut: &str,
            snapshot: &crate::native::ShortcutBindings,
        ) {
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
            // 话术与账号来自同一绑定快照，不能在读完旧话术后补读新账号。
            let (phrases, member_epoch) = {
                let phrases = snapshot
                    .phrases
                    .iter()
                    .filter(|p| {
                        crate::native::parse_shortcut(&p.shortcut)
                            .is_ok_and(|s| s.canonical() == shortcut)
                    })
                    .cloned()
                    .collect::<Vec<_>>();
                (phrases, snapshot.member_epoch)
            };
            if phrases.is_empty() {
                return;
            }
            self.hide_preview(None);
            let Some(window) = self.candidate_window.get() else {
                return;
            };
            let (max_width, max_height, layout_key, _, _) =
                match layout_limits(hwnd(target.hwnd), hwnd(*window)) {
                    Ok(limits) => limits,
                    Err(error) => {
                        self.notify_error(error);
                        return;
                    }
                };
            let payload = CandidatePayload {
                session_id: crate::model::id(),
                layout_key,
                max_width,
                max_height,
                member_epoch,
                shortcut: shortcut.into(),
                phrases,
                selected_index: 0,
                target,
            };
            self.candidates.lock().unwrap().open(payload, context);
            self.publish_candidates();
        }
        pub fn resize_candidates(
            &self,
            session_id: &str,
            layout_key: &str,
            width: f64,
            height: f64,
        ) -> crate::model::Result<()> {
            self.check_candidates();
            let mut state = self.candidates.lock().unwrap();
            let Some(payload) = &state.payload else {
                return Ok(());
            };
            if payload.session_id != session_id || payload.layout_key != layout_key {
                return Ok(());
            }
            if !width.is_finite() || !height.is_finite() || width <= 0.0 || height <= 0.0 {
                return Err("候选窗口尺寸无效。".into());
            }
            let window = hwnd(*self.candidate_window.get().ok_or("候选窗口尚未创建。")?);
            // 持有候选锁完成显示，关闭或替换后的迟到测量不能重新打开旧窗口。
            unsafe {
                position_candidates(payload, window, width, height)?;
            }
            state.measured_size = Some((width, height));
            Ok(())
        }
    }

    /// 测量与移动共用同一套边界换算；矩形未变化时不调用系统定位，避免选中切换或轮询造成闪动。
    unsafe fn position_candidates(
        payload: &CandidatePayload,
        window: HWND,
        width: f64,
        height: f64,
    ) -> crate::model::Result<()> {
        let target = hwnd(payload.target.hwnd);
        let (max_width, max_height, current_key, ix, iy) = layout_limits(target, window)?;
        if current_key != payload.layout_key {
            return Ok(());
        }
        let mut info = MONITORINFO {
            cbSize: size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        if !GetMonitorInfoW(
            MonitorFromWindow(target, MONITOR_DEFAULTTONEAREST),
            &mut info,
        )
        .as_bool()
        {
            return Err("无法读取候选所在的屏幕工作区。".into());
        }
        let scale = GetDpiForWindow(target).max(96) as f64 / 96.0;
        let gap = (6.0 * scale).round() as i32;
        let work = RECT {
            left: info.rcWork.left + gap,
            top: info.rcWork.top + gap,
            right: info.rcWork.right - gap,
            bottom: info.rcWork.bottom - gap,
        };
        let (anchor, near_input) = input_anchor(&payload.target, work, scale)?;
        let rect = bounds(
            anchor,
            near_input,
            work,
            (width
                .max((240.0 - ix as f64 / scale).min(max_width))
                .min(max_width)
                * scale)
                .round() as i32
                + ix,
            (height.min(max_height) * scale).round() as i32 + iy,
            gap,
        );
        let mut current = RECT::default();
        if GetWindowRect(window, &mut current).is_ok()
            && current == rect
            && IsWindowVisible(window).as_bool()
        {
            return Ok(());
        }
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
        Ok(())
    }
    #[cfg(test)]
    mod tests {
        use super::*;
        fn rect(left: i32, top: i32, right: i32, bottom: i32) -> RECT {
            RECT {
                left,
                top,
                right,
                bottom,
            }
        }

        #[test]
        fn candidates_choose_free_side_then_clamp_and_fallback_inside_window() {
            let work = rect(6, 6, 994, 794);
            assert_eq!(
                bounds(rect(100, 400, 101, 420), true, work, 240, 100, 6).top,
                294
            );
            assert_eq!(
                bounds(rect(100, 20, 101, 40), true, work, 240, 100, 6).top,
                46
            );
            // 两侧都不足时，优先较大一侧；最终夹边可覆盖部分输入区域，但不会出屏。
            assert_eq!(
                bounds(rect(100, 500, 101, 520), true, work, 360, 600, 6).top,
                6
            );
            assert_eq!(
                bounds(rect(100, 200, 101, 220), true, work, 360, 600, 6).top,
                194
            );
            assert_eq!(
                bounds(rect(950, 400, 951, 420), true, work, 360, 100, 6).left,
                634
            );
            assert_eq!(
                bounds(rect(112, 100, 700, 688), false, work, 240, 100, 6),
                rect(112, 588, 352, 688)
            );
            assert_eq!(
                bounds(
                    rect(-100, 20, -99, 40),
                    true,
                    rect(-194, 6, -6, 154),
                    360,
                    420,
                    6
                ),
                rect(-194, 6, -6, 154)
            );
        }

        #[test]
        fn input_anchor_rejects_full_client_containers_and_invisible_rectangles() {
            let client = rect(100, 100, 900, 700);
            assert!(input_rect(rect(100, 500, 900, 700), client));
            assert!(!input_rect(client, client));
            assert!(!input_rect(rect(110, 110, 890, 690), client));
            assert!(!input_rect(rect(1000, 500, 1200, 700), client));
            assert!(!input_rect(rect(200, 500, 200, 700), client));
            assert_eq!(
                intersection(rect(-100, 100, 500, 800), client),
                Some(rect(100, 100, 500, 700))
            );
        }
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
                            (360.0 * scale) as i32,
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
                session_id: "test".into(),
                layout_key: "test-layout".into(),
                max_width: 360.0,
                max_height: 420.0,
                member_epoch: None,
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
