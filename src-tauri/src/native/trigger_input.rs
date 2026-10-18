use super::shortcuts::KeyEvent;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc, Mutex,
};
use std::time::Instant;

/// 仅保存在原生层的透传输入凭据。钩子只取系统光标和键盘布局，不等待目标进程。
/// 选择候选后仍共享失效标记，覆盖素材下载、授权核验期间继续输入或切换焦点的情况。
#[derive(Clone)]
pub struct TriggerInput {
    pub(super) events: Vec<KeyEvent>,
    context: (i64, i64),
    anchor: Option<Anchor>,
    valid: Arc<AtomicBool>,
    caret: Arc<Mutex<Option<(i32, i32)>>>,
    tracking: Arc<AtomicBool>,
    pub(super) matched_at: Instant,
    pub(crate) clipboard_sequence: Option<u32>,
}

#[derive(Clone)]
struct Anchor {
    x: i32,
    y: i32,
    layout: isize,
    shift: bool,
    caps: bool,
}

impl TriggerInput {
    pub fn invalidate(&self) {
        self.valid.store(false, Ordering::Release);
    }
    pub fn valid_context(&self, context: Option<(i64, i64)>) -> bool {
        self.valid.load(Ordering::Acquire) && context == Some(self.context)
    }
}

#[cfg(windows)]
mod windows_input {
    use super::*;
    use crate::{model::Result, native::platform::hwnd};
    use std::mem::size_of;
    use std::time::Duration;
    use windows::Win32::{
        Foundation::*,
        UI::{
            Controls::*,
            Input::{Ime::ImmGetDefaultIMEWnd, KeyboardAndMouse::*},
            WindowsAndMessaging::*,
        },
    };

    const UNVERIFIABLE: &str =
        "当前输入区无法安全定位本次快捷键，输入已保留。请改用 Ctrl／Alt 组合快捷键。";
    // 微软拼音结束组字后可自动增加 IME_CMODE_SYMBOL；仍请求完整原值，
    // 核验时忽略这一自动变化，其他位（尤其中文、全半角）必须恢复。
    const IME_MODE_MASK: usize = !0x400;

    impl TriggerInput {
        pub(crate) unsafe fn capture(context: (i64, i64), modifiers: u32) -> Self {
            let thread = GetWindowThreadProcessId(hwnd(context.1), None);
            let mut gui = GUITHREADINFO {
                cbSize: size_of::<GUITHREADINFO>() as u32,
                ..Default::default()
            };
            let anchor = (GetGUIThreadInfo(thread, &mut gui).is_ok()
                && gui.hwndFocus == hwnd(context.1)
                && gui.hwndCaret == hwnd(context.1))
            .then(|| Anchor {
                x: gui.rcCaret.left,
                y: gui.rcCaret.top,
                layout: GetKeyboardLayout(thread).0 as isize,
                shift: modifiers & 4 != 0,
                caps: GetKeyState(VK_CAPITAL.0 as i32) & 1 != 0,
            });
            Self {
                events: vec![],
                context,
                anchor,
                valid: Arc::new(AtomicBool::new(true)),
                caret: Arc::new(Mutex::new(None)),
                tracking: Arc::new(AtomicBool::new(true)),
                matched_at: Instant::now(),
                clipboard_sequence: None,
            }
        }

        /// 与客服宝相同，先临时关闭输入法，让此前拼音和触发字母上屏，再清理本次输入。
        /// 标准 Edit 核对精确范围；企业微信自绘输入区按实际透传次数退格，包含长按重复。
        /// 中文数字可能选中此前拼音，无法核对范围的自绘输入区必须拒绝，不能猜测删除。
        pub(crate) fn remove(
            &self,
            progress: &mut Option<&'static str>,
            ime: &mut Option<ImeSession>,
            mut check: impl FnMut() -> Result<()>,
        ) -> Result<()> {
            unsafe {
                check()?;
                self.ensure_valid()?;
                let anchor = self.anchor.as_ref().ok_or(UNVERIFIABLE)?;
                let focus = hwnd(self.context.1);
                let thread = GetWindowThreadProcessId(focus, None);
                if GetKeyboardLayout(thread).0 as isize != anchor.layout {
                    return Err("输入法已切换，本次快捷键替换已停止，输入已保留。".into());
                }
                let mut class = [0u16; 256];
                let len = GetClassNameW(focus, &mut class) as usize;
                let class = String::from_utf16_lossy(&class[..len]).to_ascii_lowercase();
                let standard = class == "edit" || class.starts_with("windowsforms10.edit.");
                if standard && GetWindowLongW(focus, GWL_STYLE) & (ES_PASSWORD | ES_READONLY) != 0 {
                    return Err(UNVERIFIABLE.into());
                }
                let mut expected = self.expected_text(anchor)?;
                if !standard
                    && !super::super::dock_target::supports_keyboard_replacement(hwnd(
                        self.context.0,
                    ))
                {
                    return Err(UNVERIFIABLE.into());
                }
                let session = ImeSession::capture(self, anchor)?;
                let native = session.as_ref().is_some_and(|s| s.conversion & 1 != 0);
                if native
                    && (anchor.layout as u16 != 0x0804
                        || (!standard
                            && self
                                .events
                                .iter()
                                .any(|e| e.down && (0x30..=0x39).contains(&e.key))))
                {
                    return Err("当前输入法中的数字或组字无法安全区分触发文字，输入已保留。请改用字母双键或 Ctrl／Alt 组合快捷键。".into());
                }
                if native && session.as_ref().is_some_and(|s| s.conversion & 8 != 0) {
                    // 微软拼音的全角数字已直接上屏，仍必须逐字核对；此前拼音被数字选字时不匹配。
                    for c in &mut expected {
                        if (0x30..=0x39).contains(c) {
                            *c += 0xff00 - 0x20;
                        }
                    }
                }
                *ime = session;
                check()?;
                self.ensure_valid()?;
                // 后续关闭组字、选区、退格和粘贴会主动移动光标，不能把自身操作判成用户移动。
                self.tracking.store(false, Ordering::Release);
                if let Some(session) = ime {
                    check()?;
                    self.ensure_valid()?;
                    *progress = Some("输入法临时关闭操作已开始，此前拼音可能已上屏");
                    session.close(&mut check)?;
                }
                check()?;
                self.ensure_valid()?;
                let range = standard
                    .then(|| -> Result<_> {
                        let before = text(focus)?;
                        let caret = selection(focus)?;
                        let start = verified_start(&before, caret, &expected)?;
                        let position = message(focus, EM_POSFROMCHAR, start, 0)? as u32;
                        if !native
                            && (position == u32::MAX
                                || position as u16 as i16 as i32 != anchor.x
                                || (position >> 16) as u16 as i16 as i32 != anchor.y)
                        {
                            return Err("输入位置已变化，无法确认快捷键范围，输入已保留。".into());
                        }
                        Ok((before, start, caret))
                    })
                    .transpose()?;
                // IME 的延后通知可能覆盖 EM_SETSEL；组字结束后的清理走同一输入队列，
                // 标准 Edit 仍在前后核对完整文字范围，不因使用退格而放宽核验。
                if !standard || native {
                    *progress = Some("本次快捷键的退格操作已开始，请检查输入区");
                    super::super::clipboard::repeated_keys(VK_BACK, false, expected.len(), || {
                        check()?;
                        self.ensure_valid()
                    })?;
                    *progress = Some("本次快捷键的退格已提交");
                    if let Some((before, start, caret)) = range {
                        let mut after = before[..start].to_vec();
                        after.extend_from_slice(&before[caret..]);
                        // 系统输入尚在排队时只等待核验结果，不能因暂未生效补发退格。
                        for _ in 0..25 {
                            check()?;
                            self.ensure_valid()?;
                            let actual = text(focus)?;
                            let current = selection(focus)?;
                            if actual == after && current == start {
                                *progress = Some("本次快捷键已清理");
                                return Ok(());
                            }
                            std::thread::sleep(Duration::from_millis(20));
                        }
                        return Err("无法确认快捷键清理结果，已停止后续粘贴。".into());
                    }
                    // 退格在目标队列中异步执行；先处理用户新输入／新复制的失效通知，
                    // 再提交 Ctrl+V。期间不回放、补退格或重试已提交的操作。
                    for _ in 0..5 {
                        std::thread::sleep(Duration::from_millis(20));
                        check()?;
                        self.ensure_valid()?;
                    }
                    message(focus, WM_NULL, 0, 0)?;
                    return Ok(());
                }
                let (before, start, caret) = range.unwrap();
                check()?;
                self.ensure_valid()?;
                // 选择消息超时也可能已生效，先标记步骤，失败后必须报告中断。
                *progress = Some("本次快捷键的选区操作已开始，请检查输入区");
                message(focus, EM_SETSEL, start, caret as isize)?;
                check()?;
                self.ensure_valid()?;
                let selected = message(focus, EM_GETSEL, 0, 0)? as u32;
                if selected != ((caret as u32) << 16 | start as u32) || text(focus)? != before {
                    return Err("输入内容或选区已变化，已停止清理。".into());
                }
                check()?;
                self.ensure_valid()?;
                *progress = Some("本次快捷键的清理操作已开始，请检查输入区");
                message(focus, WM_CLEAR, 0, 0)?;
                let mut after = before[..start].to_vec();
                after.extend_from_slice(&before[caret..]);
                if text(focus)? != after || selection(focus)? != start {
                    return Err("无法确认快捷键清理结果，已停止后续粘贴。".into());
                }
                *progress = Some("本次快捷键已清理");
                check()?;
                self.ensure_valid()
            }
        }

        fn ensure_valid(&self) -> Result<()> {
            self.observe_caret();
            // 巡检存在间隔；真正变更前重新读取系统焦点，查询失败也不能继续删除。
            if self.valid_context(unsafe { crate::native::candidates::input_context() }) {
                Ok(())
            } else {
                Err("检测到新的输入或焦点变化，候选已失效，替换已停止。".into())
            }
        }

        unsafe fn expected_text(&self, anchor: &Anchor) -> Result<Vec<u16>> {
            let mut state = [0u8; 256];
            state[VK_SHIFT.0 as usize] = if anchor.shift { 0x80 } else { 0 };
            state[VK_CAPITAL.0 as usize] = u8::from(anchor.caps);
            let layout = HKL(anchor.layout as *mut _);
            let mut text = vec![];
            for event in self
                .events
                .iter()
                .filter(|e| e.down && (0x30..=0x5a).contains(&e.key))
            {
                if GetAsyncKeyState(event.key as i32) < 0 {
                    return Err("请松开快捷键后重新选择候选，输入已保留。".into());
                }
                let mut chars = [0u16; 8];
                let count = ToUnicodeEx(event.key, 0, &state, &mut chars, 4, Some(layout));
                if count != 1 {
                    return Err(UNVERIFIABLE.into());
                }
                text.push(chars[0]);
            }
            if text.is_empty() {
                return Err(UNVERIFIABLE.into());
            }
            Ok(text)
        }

        /// 透传输入进入目标队列后记录停稳的系统光标；继续输入、鼠标和方向键另由钩子失效。
        /// 共享记录也覆盖候选选定后等待下载期间的程序化光标移动，不读取聊天正文。
        pub(crate) fn observe_caret(&self) {
            if !self.tracking.load(Ordering::Acquire)
                || self.matched_at.elapsed() < Duration::from_millis(50)
            {
                return;
            }
            unsafe {
                let mut held = std::collections::HashSet::new();
                for event in &self.events {
                    if event.down {
                        held.insert(event.key);
                    } else {
                        held.remove(&event.key);
                    }
                }
                if !held.is_empty() {
                    return;
                }
                let mut gui = GUITHREADINFO {
                    cbSize: size_of::<GUITHREADINFO>() as u32,
                    ..Default::default()
                };
                if GetGUIThreadInfo(
                    GetWindowThreadProcessId(hwnd(self.context.1), None),
                    &mut gui,
                )
                .is_err()
                    || gui.hwndCaret != hwnd(self.context.1)
                {
                    self.invalidate();
                    return;
                }
                let position = (gui.rcCaret.left, gui.rcCaret.top);
                let mut previous = self.caret.lock().unwrap();
                if previous.is_some_and(|p| p != position) {
                    self.invalidate();
                } else {
                    *previous = Some(position);
                }
            }
        }
    }

    /// 临时英文状态必须覆盖退格和 Ctrl+V，否则输入法会把粘贴解释为组字操作。
    /// 只在原目标、原布局且用户未接管时恢复开关及转换模式，不操作新的焦点。
    pub(crate) struct ImeSession {
        input: TriggerInput,
        window: HWND,
        layout: isize,
        conversion: usize,
        closed_conversion: Option<usize>,
    }
    impl ImeSession {
        unsafe fn capture(input: &TriggerInput, anchor: &Anchor) -> Result<Option<Self>> {
            if ![0x04, 0x11, 0x12].contains(&(anchor.layout as u16 & 0x3ff)) {
                return Ok(None);
            }
            let window = ImmGetDefaultIMEWnd(hwnd(input.context.1));
            if window.0.is_null() {
                return Err("无法确认目标输入法状态，输入已保留。".into());
            }
            if message(window, WM_IME_CONTROL, 5, 0)? == 0 {
                return Ok(None);
            }
            Ok(Some(Self {
                input: input.clone(),
                window,
                layout: anchor.layout,
                conversion: message(window, WM_IME_CONTROL, 1, 0)?,
                closed_conversion: None,
            }))
        }
        unsafe fn close(&mut self, mut check: impl FnMut() -> Result<()>) -> Result<()> {
            // 即使关闭消息超时，也交给调用者尝试恢复；超时不能当作没有发生变更。
            self.closed_conversion = Some(self.conversion);
            message(self.window, WM_IME_CONTROL, 6, 0)?;
            if message(self.window, WM_IME_CONTROL, 5, 0)? != 0 {
                return Err("无法临时关闭输入法，已停止快捷键替换。".into());
            }
            // 部分 TSF 宿主会在下一次按键时重新打开 IME，但保持英文模式。
            // 明确切为英文，使这个异步过渡仍能处理退格和粘贴，并可识别自身模式变更。
            message(self.window, WM_IME_CONTROL, 2, 0)?;
            self.closed_conversion = Some(0);
            // 转换模式由 TSF 异步应用；这里只等结果，不重复提交切换命令。
            for _ in 0..25 {
                check()?;
                self.input.ensure_valid()?;
                if message(self.window, WM_IME_CONTROL, 1, 0)? & IME_MODE_MASK == 0 {
                    return Ok(());
                }
                std::thread::sleep(Duration::from_millis(20));
            }
            Err("无法进入临时英文输入状态，已停止快捷键替换。".into())
        }
        pub(crate) fn restore(self, mut check: impl FnMut() -> Result<()>) -> Result<()> {
            if self.closed_conversion.is_none() {
                return Ok(());
            }
            // SendInput 只是排队；让目标处理退格和粘贴后再恢复中文，期间继续核验目标。
            for _ in 0..25 {
                std::thread::sleep(Duration::from_millis(20));
                check()
                    .and_then(|_| self.input.ensure_valid())
                    .map_err(|_| "输入已中断，未恢复输入法，请检查目标输入区及中英文状态。")?;
            }
            unsafe {
                if GetKeyboardLayout(GetWindowThreadProcessId(hwnd(self.input.context.1), None)).0
                    as isize
                    != self.layout
                    || Some(message(self.window, WM_IME_CONTROL, 1, 0)? & IME_MODE_MASK)
                        != self.closed_conversion.map(|mode| mode & IME_MODE_MASK)
                {
                    return Err("输入法状态已变化，未覆盖当前状态，请检查中英文模式。".into());
                }
                message(self.window, WM_IME_CONTROL, 6, 1)?;
                message(self.window, WM_IME_CONTROL, 2, self.conversion as isize)?;
                for _ in 0..25 {
                    check()?;
                    self.input.ensure_valid()?;
                    if message(self.window, WM_IME_CONTROL, 5, 0)? != 0
                        && message(self.window, WM_IME_CONTROL, 1, 0)? & IME_MODE_MASK
                            == self.conversion & IME_MODE_MASK
                    {
                        return Ok(());
                    }
                    std::thread::sleep(Duration::from_millis(20));
                }
            }
            Err("输入法模式未完整恢复，请手动检查中英文状态。".into())
        }
    }

    unsafe fn message(focus: HWND, kind: u32, w: usize, l: isize) -> Result<usize> {
        let mut result = 0;
        if SendMessageTimeoutW(
            focus,
            kind,
            WPARAM(w),
            LPARAM(l),
            SMTO_ABORTIFHUNG | SMTO_BLOCK,
            200,
            Some(&mut result),
        )
        .0 == 0
        {
            return Err("目标输入区未响应，快捷键替换已停止。".into());
        }
        Ok(result)
    }

    unsafe fn text(focus: HWND) -> Result<Vec<u16>> {
        for _ in 0..10 {
            let len = message(focus, WM_GETTEXTLENGTH, 0, 0)?;
            // EM_GETSEL 的无指针返回值只完整覆盖 16 位位置；超出时拒绝而非截断后删除。
            if len >= u16::MAX as usize {
                return Err(UNVERIFIABLE.into());
            }
            let mut text = vec![0u16; len + 1];
            let copied = message(focus, WM_GETTEXT, text.len(), text.as_mut_ptr() as isize)?;
            // 结束组字的字符可能仍在排队；长度增长时 WM_GETTEXT 也会返回 len（截断），
            // 必须让目标处理队列后再核对长度。这里只重读，不重试任何修改输入的操作。
            std::thread::sleep(Duration::from_millis(20));
            if copied == len && message(focus, WM_GETTEXTLENGTH, 0, 0)? == len {
                text.truncate(len);
                return Ok(text);
            }
        }
        Err("输入内容已变化，快捷键替换已停止。".into())
    }

    unsafe fn selection(focus: HWND) -> Result<usize> {
        let range = message(focus, EM_GETSEL, 0, 0)? as u32;
        if range == u32::MAX || range as u16 != (range >> 16) as u16 {
            return Err("输入选区已变化，快捷键替换已停止。".into());
        }
        Ok(range as u16 as usize)
    }

    fn verified_start(text: &[u16], caret: usize, expected: &[u16]) -> Result<usize> {
        let start = caret
            .checked_sub(expected.len())
            .filter(|_| !expected.is_empty());
        match start {
            Some(start) if text.get(start..caret) == Some(expected) => Ok(start),
            _ => Err("无法核对本次快捷键文字，输入已保留。".into()),
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        fn utf16(text: &str) -> Vec<u16> {
            text.encode_utf16().collect()
        }
        #[test]
        fn only_matches_observed_input_immediately_before_caret() {
            let text = utf16("草稿rzz后文");
            assert_eq!(verified_start(&text, 5, &utf16("rzz")).unwrap(), 2);
            for (caret, expected) in [(2, "rzz"), (4, "rzz"), (5, "RZZ"), (5, "")] {
                assert!(verified_start(&text, caret, &utf16(expected)).is_err());
            }
        }
    }
}

#[cfg(windows)]
pub(crate) use windows_input::ImeSession;
