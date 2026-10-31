use super::platform::{hwnd, pid, wide};
use crate::{
    delivery::DeliveryDriver,
    model::{Block, Result},
    native::{TargetContext, TriggerInput},
};
use std::{
    mem::size_of,
    path::Path,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::{Duration, Instant},
};
use windows::{
    core::PWSTR,
    Win32::{
        Foundation::*,
        Graphics::Gdi::*,
        System::{DataExchange::*, Memory::*, Threading::*},
        UI::{Input::KeyboardAndMouse::*, WindowsAndMessaging::*},
    },
};

fn error(e: impl std::fmt::Display) -> String {
    format!("Windows输入操作失败：{e}")
}
struct ClipboardLock;
impl ClipboardLock {
    fn open(owner: HWND) -> Result<Self> {
        for _ in 0..12 {
            if unsafe { OpenClipboard(Some(owner)) }.is_ok() {
                return Ok(Self);
            }
            std::thread::sleep(Duration::from_millis(25));
        }
        Err("剪贴板正在被其他程序占用，请稍后重试。".into())
    }

    fn open_for_delivery(
        owner: HWND,
        mut check: impl FnMut() -> Result<()>,
        mut on_wait: impl FnMut(),
    ) -> Result<Self> {
        let started = Instant::now();
        let deadline = started + Duration::from_secs(3);
        let mut announced = false;
        loop {
            check()?;
            match unsafe { OpenClipboard(Some(owner)) } {
                Ok(()) => {
                    let lock = Self;
                    // 等待时用户可能接管了输入，取得锁后、清空剪贴板前再核对一次。
                    check()?;
                    return Ok(lock);
                }
                Err(e) if Instant::now() >= deadline => return Err(clipboard_open_error(e)),
                Err(_) => {
                    if !announced && started.elapsed() >= Duration::from_millis(500) {
                        announced = true;
                        on_wait();
                    }
                    std::thread::sleep(
                        Duration::from_millis(25)
                            .min(deadline.saturating_duration_since(Instant::now())),
                    );
                }
            }
        }
    }
}
impl Drop for ClipboardLock {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseClipboard();
        }
    }
}

fn clipboard_open_error(error: windows::core::Error) -> String {
    // 内容所有者不一定是当前打开剪贴板的程序；只报告检测时仍匹配的窗口和进程。
    let occupant = identify_clipboard_opener(
        || unsafe {
            let window = GetOpenClipboardWindow().ok()?;
            Some((window, pid(window)))
        },
        |process_id| unsafe {
            OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process_id)
                .ok()
                .and_then(|process| {
                    let mut path = [0u16; 4096];
                    let mut length = path.len() as u32;
                    let result = QueryFullProcessImageNameW(
                        process,
                        PROCESS_NAME_WIN32,
                        PWSTR(path.as_mut_ptr()),
                        &mut length,
                    );
                    let _ = CloseHandle(process);
                    result.ok()?;
                    let path = String::from_utf16_lossy(&path[..length as usize]);
                    Some(Path::new(&path).file_name()?.to_string_lossy().into_owned())
                })
        },
    );
    format_clipboard_open_error(error, occupant)
}

fn identify_clipboard_opener(
    mut current: impl FnMut() -> Option<(HWND, u32)>,
    process_name: impl FnOnce(u32) -> Option<String>,
) -> Option<(u32, Option<String>)> {
    let (window, process_id) = current()?;
    if process_id == 0 {
        return None;
    }
    let name = process_name(process_id);
    (current() == Some((window, process_id))).then_some((process_id, name))
}

fn format_clipboard_open_error(
    error: windows::core::Error,
    occupant: Option<(u32, Option<String>)>,
) -> String {
    let message = match occupant {
        Some((process_id, Some(name))) => {
            format!("{name}（PID {process_id}）在检测时可能占用剪贴板，请检查该程序。")
        }
        Some((process_id, None)) => {
            format!("进程 PID {process_id} 在检测时可能占用剪贴板，但无法读取程序名。")
        }
        None => "无法打开剪贴板，可能被其他程序占用，但无法识别占用程序。".into(),
    };
    format!("{message}（系统错误码：0x{:08X}）", error.code().0 as u32)
}

fn put(format: u32, bytes: &[u8]) -> Result<()> {
    unsafe {
        let memory = GlobalAlloc(GMEM_MOVEABLE, bytes.len()).map_err(error)?;
        let ptr = GlobalLock(memory);
        if ptr.is_null() {
            let _ = GlobalFree(Some(memory));
            return Err("剪贴板内存无法锁定。".into());
        }
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), ptr as *mut u8, bytes.len());
        let _ = GlobalUnlock(memory);
        if let Err(e) = SetClipboardData(format, Some(HANDLE(memory.0))) {
            let _ = GlobalFree(Some(memory));
            return Err(error(e));
        }
        Ok(())
    }
}
fn text_bytes(text: &str) -> Vec<u8> {
    wide(&text.replace("\r\n", "\n").replace('\n', "\r\n"))
        .into_iter()
        .flat_map(u16::to_le_bytes)
        .collect()
}
pub fn copy_text(owner: i64, text: &str) -> Result<()> {
    let _lock = ClipboardLock::open(hwnd(owner))?;
    unsafe {
        EmptyClipboard().map_err(error)?;
    }
    put(13, &text_bytes(text))
}

fn exact_text_bytes(text: &str) -> Result<Vec<u8>> {
    if text.contains('\0') {
        return Err("命令正文包含空字符，不能原样复制，请联系维护者修正。".into());
    }
    Ok(wide(text).into_iter().flat_map(u16::to_le_bytes).collect())
}
/// 命令不沿用话术投递的 CRLF 规范化，保留原始换行和末尾空白，不追加回车。
pub fn copy_text_exact(owner: i64, text: &str) -> Result<()> {
    let bytes = exact_text_bytes(text)?;
    let _lock = ClipboardLock::open(hwnd(owner))?;
    unsafe {
        EmptyClipboard().map_err(error)?;
    }
    put(13, &bytes)
}

/// 显式“复制文件”写入标准 CF_HDROP，可在资源管理器或支持文件粘贴的应用使用。
/// 先验证整批路径并构造双零结尾的 Unicode 文件列表，失效记录不会清空剪贴板。
fn file_bytes(paths: &[String]) -> Result<Vec<u8>> {
    if paths.is_empty() {
        return Err("请选择要复制的文件。".into());
    }
    let mut bytes = vec![0u8; 20];
    bytes[0..4].copy_from_slice(&20u32.to_le_bytes());
    bytes[16..20].copy_from_slice(&1u32.to_le_bytes());
    for path in paths {
        if !Path::new(path).is_file() {
            return Err(format!("关联文件已失效：{path}。请重新关联。"));
        }
        for ch in wide(&crate::model::external_path(Path::new(path))) {
            bytes.extend_from_slice(&ch.to_le_bytes());
        }
    }
    bytes.extend_from_slice(&0u16.to_le_bytes());
    Ok(bytes)
}
pub fn copy_files(owner: i64, paths: &[String]) -> Result<()> {
    let bytes = file_bytes(paths)?;
    let _lock = ClipboardLock::open(hwnd(owner))?;
    unsafe {
        EmptyClipboard().map_err(error)?;
    }
    put(15, &bytes)
}

/// 通用 Windows 发送驱动：直接写入待投递内容，不读取、备份或恢复旧剪贴板。
/// 操作开始及每次写入后记录序号，写入和按键提交前核对变化；用户新复制时立即停止。
/// 完成或中断后保留最后写入的内容，新复制也不会被结束清理覆盖。
pub struct WindowsDriver {
    owner: HWND,
    target: TargetContext,
    cancelled: Arc<AtomicBool>,
    initial_seq: u32,
    owned_seq: Option<u32>,
    trigger_input: Option<TriggerInput>,
    trigger_progress: Option<&'static str>,
    wait_feedback: Option<Arc<dyn Fn(bool) + Send + Sync>>,
}
impl WindowsDriver {
    pub fn new(owner: i64, target: TargetContext, cancelled: Arc<AtomicBool>) -> Self {
        Self {
            owner: hwnd(owner),
            target,
            cancelled,
            initial_seq: 0,
            owned_seq: None,
            trigger_input: None,
            trigger_progress: None,
            wait_feedback: None,
        }
    }
    pub(crate) fn set_wait_feedback(&mut self, feedback: Arc<dyn Fn(bool) + Send + Sync>) {
        self.wait_feedback = Some(feedback);
    }
    pub(crate) fn set_trigger_input(&mut self, input: Option<TriggerInput>) {
        self.trigger_input = input;
    }
    fn valid(&self) -> Result<()> {
        unsafe {
            let root = hwnd(self.target.hwnd);
            let focus = hwnd(self.target.focus);
            if !IsWindow(Some(root)).as_bool()
                || pid(root) != self.target.pid
                || self.target.pid == std::process::id()
            {
                return Err("目标窗口已失效，请重新聚焦目标输入区后返回闪语。".into());
            }
            if !IsWindow(Some(focus)).as_bool()
                || (focus != root && GetAncestor(focus, GA_ROOT) != root)
            {
                return Err("目标输入位置已失效，请重新聚焦输入区。".into());
            }
            Ok(())
        }
    }
    fn check_clipboard(&self) -> Result<()> {
        let expected = self.owned_seq.unwrap_or(self.initial_seq);
        if unsafe { GetClipboardSequenceNumber() } != expected {
            return Err("检测到新的复制操作，已停止；新剪贴板内容已保留。".into());
        }
        Ok(())
    }
    fn write_block(&mut self, block: &Block) -> Result<()> {
        // 先生成完整载荷再占用剪贴板；无效图片或文件不会清空用户的剪贴板。
        let formats: Vec<(u32, Vec<u8>)> = match block {
            Block::Text { text } => vec![(13, text_bytes(text))],
            Block::File { path, .. } => vec![(15, file_bytes(std::slice::from_ref(path))?)],
            Block::Image { path, .. } => {
                let image = crate::backup::decode_image(
                    &std::fs::read(path).map_err(|e| format!("图片已失效或无法读取：{e}"))?,
                )?
                .to_rgba8();
                let (width, height) = image.dimensions();
                let header = BITMAPV5HEADER {
                    bV5Size: size_of::<BITMAPV5HEADER>() as u32,
                    bV5Width: width as i32,
                    bV5Height: -(height as i32),
                    bV5Planes: 1,
                    bV5BitCount: 32,
                    bV5Compression: BI_BITFIELDS,
                    bV5RedMask: 0x00ff0000,
                    bV5GreenMask: 0x0000ff00,
                    bV5BlueMask: 0x000000ff,
                    bV5AlphaMask: 0xff000000,
                    bV5CSType: 0x73524742,
                    ..Default::default()
                };
                let legacy = BITMAPINFOHEADER {
                    biSize: size_of::<BITMAPINFOHEADER>() as u32,
                    biWidth: width as i32,
                    biHeight: -(height as i32),
                    biPlanes: 1,
                    biBitCount: 32,
                    biCompression: BI_RGB.0,
                    ..Default::default()
                };
                let mut v5 = unsafe {
                    std::slice::from_raw_parts(
                        &header as *const _ as *const u8,
                        size_of::<BITMAPV5HEADER>(),
                    )
                }
                .to_vec();
                let mut dib = unsafe {
                    std::slice::from_raw_parts(
                        &legacy as *const _ as *const u8,
                        size_of::<BITMAPINFOHEADER>(),
                    )
                }
                .to_vec();
                for p in image.pixels() {
                    v5.extend_from_slice(&[p[2], p[1], p[0], p[3]]);
                    let mix =
                        |c: u8| ((c as u16 * p[3] as u16 + 255 * (255 - p[3]) as u16) / 255) as u8;
                    dib.extend_from_slice(&[mix(p[2]), mix(p[1]), mix(p[0]), 255]);
                }
                vec![(17, v5), (8, dib)]
            }
        };
        // 反馈对象先于剪贴板锁创建，提前返回时也先释放锁，再通知界面等待结束。
        let mut waiting = WaitFeedback::new(self.wait_feedback.clone());
        let lock = ClipboardLock::open_for_delivery(
            self.owner,
            || self.ensure_target(),
            || waiting.begin(),
        )?;
        unsafe {
            EmptyClipboard().map_err(error)?;
        }
        let result = (|| {
            for (format, bytes) in formats {
                put(format, &bytes)?;
            }
            Ok(())
        })();
        // Windows 可能在 CloseClipboard 时补充合成格式并更新序号。释放锁后只认领仍由
        // 本窗口拥有、检查期间未变化的剪贴板；其他程序抢先复制的内容不会被认领或粘贴。
        drop(lock);
        let sequence = unsafe { GetClipboardSequenceNumber() };
        if unsafe { GetClipboardOwner() }.ok() != Some(self.owner)
            || unsafe { GetClipboardSequenceNumber() } != sequence
        {
            return Err("检测到新的复制操作，已停止；新剪贴板内容已保留。".into());
        }
        self.owned_seq = Some(sequence);
        result
    }
}

struct WaitFeedback {
    callback: Option<Arc<dyn Fn(bool) + Send + Sync>>,
    started: bool,
}
impl WaitFeedback {
    fn new(callback: Option<Arc<dyn Fn(bool) + Send + Sync>>) -> Self {
        Self {
            callback,
            started: false,
        }
    }
    fn begin(&mut self) {
        if let Some(callback) = &self.callback {
            self.started = true;
            callback(true);
        }
    }
}
impl Drop for WaitFeedback {
    fn drop(&mut self) {
        if self.started {
            if let Some(callback) = &self.callback {
                callback(false);
            }
        }
    }
}
impl DeliveryDriver for WindowsDriver {
    fn prepare(&mut self) -> Result<()> {
        self.valid()?;
        unsafe {
            // 只记录变化标识，不读取原格式；即使原载荷无法读取也能开始投递。
            self.initial_seq = self
                .trigger_input
                .as_ref()
                .and_then(|input| input.clipboard_sequence)
                .unwrap_or_else(|| GetClipboardSequenceNumber());
            // 替换候选不能先恢复一个已经变化的焦点，再删除那个输入区的文字。
            if self.trigger_input.is_some() {
                return self.ensure_target();
            }
            let window = hwnd(self.target.hwnd);
            if IsIconic(window).as_bool() {
                let _ = ShowWindow(window, SW_RESTORE);
            }
            let thread = GetCurrentThreadId();
            let focus_thread = GetWindowThreadProcessId(hwnd(self.target.focus), None);
            let attached =
                thread != focus_thread && AttachThreadInput(thread, focus_thread, true).as_bool();
            let foreground = SetForegroundWindow(window).as_bool();
            if foreground {
                let _ = SetFocus(Some(hwnd(self.target.focus)));
            }
            if attached {
                let _ = AttachThreadInput(thread, focus_thread, false);
            }
            if !foreground {
                return Err(
                    "无法恢复目标焦点。若目标程序以管理员运行，请让两边使用相同权限。".into(),
                );
            }
        }
        self.ensure_target()
    }
    fn ensure_target(&mut self) -> Result<()> {
        if self.cancelled.load(Ordering::Acquire) {
            return Err("操作已取消，已执行的内容不会重发。".into());
        }
        self.valid()?;
        self.check_clipboard()?;
        unsafe {
            if GetForegroundWindow() != hwnd(self.target.hwnd) {
                return Err("目标焦点已变化，操作已停止，已执行内容不会重发。".into());
            }
            let thread = GetWindowThreadProcessId(hwnd(self.target.focus), None);
            let mut gui = GUITHREADINFO {
                cbSize: size_of::<GUITHREADINFO>() as u32,
                ..Default::default()
            };
            if GetGUIThreadInfo(thread, &mut gui).is_ok()
                && !gui.hwndFocus.0.is_null()
                && gui.hwndFocus != hwnd(self.target.focus)
            {
                return Err("目标输入焦点已变化，操作已停止。".into());
            }
        }
        Ok(())
    }
    fn paste(&mut self, block: &Block) -> Result<()> {
        self.write_block(block)?;
        let mut ime: Option<super::trigger_input::ImeSession> = None;
        let pasted = keys(VK_V, true, || {
            self.ensure_target()?;
            if let Some(input) = self.trigger_input.take() {
                let mut progress = None;
                let result = input.remove(&mut progress, &mut ime, || self.ensure_target());
                self.trigger_progress = progress;
                result?;
            }
            self.ensure_target()
        });
        if pasted.is_ok() && self.trigger_progress.is_some() {
            self.trigger_progress = Some("本次快捷键清理与首块粘贴已提交");
        }
        let restored = ime.map_or(Ok(()), |session| session.restore(|| self.ensure_target()));
        match (pasted, restored) {
            (Err(error), Err(restore)) => Err(format!("{error}；{restore}")),
            (result, Ok(())) => result,
            (Ok(()), Err(error)) => Err(error),
        }
    }
    fn send_key(&mut self, ctrl: bool) -> Result<()> {
        keys(VK_RETURN, ctrl, || self.ensure_target())
    }
    fn pause(&mut self, millis: u64, cancel: &AtomicBool) -> Result<()> {
        let end = Instant::now() + Duration::from_millis(millis);
        while Instant::now() < end {
            if cancel.load(Ordering::Acquire) {
                return Err("检测到取消或新的键盘输入，操作已停止。".into());
            }
            self.ensure_target()?;
            std::thread::sleep(
                Duration::from_millis(20).min(end.saturating_duration_since(Instant::now())),
            );
        }
        Ok(())
    }
    fn settle(&mut self, millis: u64) {
        // 不再提交输入或改写剪贴板，只延后释放发送锁，防止下一次投递过早覆盖载荷。
        std::thread::sleep(Duration::from_millis(millis));
    }
    fn finish(&mut self) -> Result<()> {
        // 产品不恢复旧内容，也不清空最后写入或用户后来复制的内容。
        Ok(())
    }
    fn input_change(&self) -> Option<&'static str> {
        self.trigger_progress
    }
}
fn keys(key: VIRTUAL_KEY, ctrl: bool, mut check: impl FnMut() -> Result<()>) -> Result<()> {
    repeated_keys(key, ctrl, 1, &mut check)
}
pub(super) fn repeated_keys(
    key: VIRTUAL_KEY,
    ctrl: bool,
    count: usize,
    mut check: impl FnMut() -> Result<()>,
) -> Result<()> {
    unsafe {
        let until = Instant::now() + Duration::from_millis(1500);
        while [VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN]
            .iter()
            .any(|k| GetAsyncKeyState(k.0 as i32) < 0)
        {
            if Instant::now() > until {
                return Err("请松开 Ctrl、Alt、Shift 或 Win 键后重试。".into());
            }
            std::thread::sleep(Duration::from_millis(20));
        }
        // 等待修饰键松开期间也可能失焦，提交按键前必须再次核对目标与取消状态。
        check()?;
        let input = |key: VIRTUAL_KEY, up: bool| INPUT {
            r#type: INPUT_KEYBOARD,
            Anonymous: INPUT_0 {
                ki: KEYBDINPUT {
                    wVk: key,
                    dwFlags: if up {
                        KEYEVENTF_KEYUP
                    } else {
                        KEYBD_EVENT_FLAGS(0)
                    },
                    dwExtraInfo: super::INTERNAL_INPUT_MARKER,
                    ..Default::default()
                },
            },
        };
        let mut inputs = vec![];
        if ctrl {
            inputs.push(input(VK_CONTROL, false));
        }
        for _ in 0..count {
            inputs.push(input(key, false));
            inputs.push(input(key, true));
        }
        if ctrl {
            inputs.push(input(VK_CONTROL, true));
        }
        if SendInput(&inputs, size_of::<INPUT>() as i32) != inputs.len() as u32 {
            // 释放可能已经按下的键，避免局部提交后留下 Ctrl 按住状态。
            let _ = SendInput(
                &[input(key, true), input(VK_CONTROL, true)],
                size_of::<INPUT>() as i32,
            );
            return Err("系统未完整提交按键，请检查目标权限与输入状态。".into());
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exact_command_copy_keeps_mixed_newlines_quotes_and_trailing_whitespace() {
        let text = "  curl 'https://example.test/a' | sh \\\n\"$HOME\"\r\nlast  ";
        let bytes = exact_text_bytes(text).unwrap();
        let wide: Vec<_> = bytes
            .as_chunks::<2>()
            .0
            .iter()
            .map(|b| u16::from_le_bytes(*b))
            .collect();
        assert_eq!(wide.last(), Some(&0));
        assert_eq!(String::from_utf16(&wide[..wide.len() - 1]).unwrap(), text);
        assert_ne!(bytes, text_bytes(text));
        assert!(exact_text_bytes("echo safe\0echo lost").is_err());
    }

    #[test]
    fn clipboard_error_reports_available_process_or_unknown_without_claiming_certainty() {
        let error = || windows::core::Error::from(windows::core::HRESULT(0x80070005u32 as i32));
        let known = format_clipboard_open_error(error(), Some((1234, Some("example.exe".into()))));
        assert!(known.contains("example.exe（PID 1234）在检测时可能占用"));
        assert!(known.contains("0x80070005"));
        let unnamed = format_clipboard_open_error(error(), Some((1234, None)));
        assert!(unnamed.contains("PID 1234"));
        assert!(unnamed.contains("无法读取程序名"));
        let unknown = format_clipboard_open_error(error(), None);
        assert!(unknown.contains("无法识别占用程序"));
        assert!(!unknown.contains("PID"));
        for message in [known, unnamed, unknown] {
            assert!(!message.contains("重试"), "部分执行时不能暗示立即重试");
        }
    }

    #[test]
    fn clipboard_opener_change_during_detection_is_reported_as_unknown() {
        let mut first_handle = 0u8;
        let mut other_handle = 0u8;
        let first = HWND((&mut first_handle as *mut u8).cast());
        let other = HWND((&mut other_handle as *mut u8).cast());
        assert!(
            identify_clipboard_opener(|| None, |_| panic!("没有占用窗口时不应查询进程")).is_none()
        );
        assert!(identify_clipboard_opener(
            || Some((first, 0)),
            |_| panic!("没有进程ID时不应查询进程")
        )
        .is_none());
        for next in [None, Some((other, 1234)), Some((first, 5678))] {
            let mut observations = [Some((first, 1234)), next].into_iter();
            let occupant = identify_clipboard_opener(
                || observations.next().unwrap(),
                |_| Some("example.exe".into()),
            );
            assert!(occupant.is_none());
            let message = format_clipboard_open_error(
                windows::core::Error::from(windows::core::HRESULT(0x80070005u32 as i32)),
                occupant,
            );
            assert!(message.contains("无法识别占用程序"));
            assert!(!message.contains("example.exe"));
        }
        let mut stable = [Some((first, 1234)), Some((first, 1234))].into_iter();
        assert_eq!(
            identify_clipboard_opener(|| stable.next().unwrap(), |_| Some("example.exe".into())),
            Some((1234, Some("example.exe".into())))
        );
        let mut stable = [Some((first, 1234)), Some((first, 1234))].into_iter();
        assert_eq!(
            identify_clipboard_opener(|| stable.next().unwrap(), |_| None),
            Some((1234, None))
        );
    }

    /// 仅验证内存中的 CF_HDROP 编码，不读写系统剪贴板或发送按键。
    #[test]
    fn unicode_file_list_has_dropfiles_header_and_double_null_terminator() {
        let dir = tempfile::tempdir().unwrap();
        let paths: Vec<_> = ["中文 附件.txt", "第二个📎.txt"]
            .into_iter()
            .map(|name| {
                let path = dir.path().join(name);
                std::fs::write(&path, "源文件").unwrap();
                path.to_string_lossy().into_owned()
            })
            .collect();
        let bytes = file_bytes(&paths).unwrap();
        assert_eq!(&bytes[..4], &20u32.to_le_bytes());
        assert_eq!(&bytes[4..16], &[0; 12]);
        assert_eq!(&bytes[16..20], &1u32.to_le_bytes());
        let units = bytes[20..]
            .as_chunks::<2>()
            .0
            .iter()
            .map(|b| u16::from_le_bytes([b[0], b[1]]))
            .collect::<Vec<_>>();
        assert_eq!(&units[units.len() - 2..], &[0, 0]);
        let decoded: Vec<_> = units[..units.len() - 1]
            .split(|unit| *unit == 0)
            .filter(|path| !path.is_empty())
            .map(|path| String::from_utf16(path).unwrap())
            .collect();
        assert_eq!(decoded, paths);
        assert!(file_bytes(&[]).is_err());
        let invalid = vec![
            paths[0].clone(),
            dir.path().join("失效.txt").to_string_lossy().into_owned(),
        ];
        assert!(file_bytes(&invalid).unwrap_err().contains("关联文件已失效"));
        let single = file_bytes(&paths[..1]).unwrap();
        assert!(single.ends_with(&[0, 0, 0, 0]));
    }
}
