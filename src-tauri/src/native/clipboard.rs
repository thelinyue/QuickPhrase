use super::platform::{hwnd, pid, wide};
use crate::{
    delivery::DeliveryDriver,
    model::{Block, Result},
    native::TargetContext,
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
use windows::Win32::{
    Foundation::*,
    Graphics::Gdi::*,
    System::{DataExchange::*, Memory::*, Threading::*},
    UI::{Input::KeyboardAndMouse::*, WindowsAndMessaging::*},
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
}
impl Drop for ClipboardLock {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseClipboard();
        }
    }
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
}
impl WindowsDriver {
    pub fn new(owner: i64, target: TargetContext, cancelled: Arc<AtomicBool>) -> Self {
        Self {
            owner: hwnd(owner),
            target,
            cancelled,
            initial_seq: 0,
            owned_seq: None,
        }
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
        let lock = ClipboardLock::open(self.owner)?;
        self.check_clipboard()?;
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
impl DeliveryDriver for WindowsDriver {
    fn prepare(&mut self) -> Result<()> {
        self.valid()?;
        unsafe {
            // 只记录变化标识，不读取原格式；即使原载荷无法读取也能开始投递。
            self.initial_seq = GetClipboardSequenceNumber();
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
        keys(VK_V, true, || self.ensure_target())
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
    fn finish(&mut self) -> Result<()> {
        // 产品不恢复旧内容，也不清空最后写入或用户后来复制的内容。
        Ok(())
    }
}
fn keys(key: VIRTUAL_KEY, ctrl: bool, mut check: impl FnMut() -> Result<()>) -> Result<()> {
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
        inputs.push(input(key, false));
        inputs.push(input(key, true));
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
