use std::{
    path::Path,
    sync::{mpsc, Arc, Mutex},
    time::{Duration, Instant},
};
use windows::{
    core::PWSTR,
    Win32::{
        Foundation::{CloseHandle, HWND, RECT},
        System::Threading::{
            OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32,
            PROCESS_QUERY_LIMITED_INFORMATION,
        },
        UI::WindowsAndMessaging::{
            GetClassNameW, GetWindowRect, GetWindowThreadProcessId, IsIconic, IsWindow,
            IsWindowVisible,
        },
    },
};

const INTERVAL: Duration = Duration::from_millis(250);
const RESULT_LIFETIME: Duration = Duration::from_millis(750);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Target {
    window: i64,
    pid: u32,
    fixture: bool,
}

/// 只传递句柄、进程身份和检测结论，不查询窗口标题或聊天内容。
/// revision 区分 A→B→A 的两次 A，防止慢查询把已作废的结果重新用于新前台。
#[derive(Default)]
struct Observation {
    target: Option<Target>,
    revision: u64,
    confirmed_at: Option<Instant>,
}
impl Observation {
    fn select(&mut self, target: Option<Target>) -> bool {
        if self.target == target {
            return false;
        }
        self.target = target;
        self.revision = self.revision.wrapping_add(1);
        self.confirmed_at = None;
        true
    }
    fn publish(&mut self, revision: u64, confirmed: bool, now: Instant) {
        if self.revision == revision {
            self.confirmed_at = confirmed.then_some(now);
        }
    }
    fn confirmed(&self, now: Instant) -> Option<i64> {
        self.confirmed_at
            .filter(|at| now.saturating_duration_since(*at) < RESULT_LIFETIME)
            .and(self.target.map(|target| target.window))
    }
}

/// 企业微信 5.0.10 的聊天输入框不暴露 UIA 元数据，因此按进程和精确窗口类识别。
/// 主窗口内的通讯录和搜索仍跟随；登录、图片、视频及其他窗口类型不能继承聊天目标。
/// 独立线程周期复核窗口有效性，原生跟随与键盘监听只读取短锁内的结论。
/// 通知队列只保留一个唤醒信号，实际请求始终读取最新目标，退出时关闭发送端即可。
pub struct DockTargetProbe {
    observation: Arc<Mutex<Observation>>,
    wake: mpsc::SyncSender<()>,
    foreground: i64,
}
impl Default for DockTargetProbe {
    fn default() -> Self {
        let observation = Arc::new(Mutex::new(Observation::default()));
        let shared = observation.clone();
        let (wake, rx) = mpsc::sync_channel(1);
        std::thread::spawn(move || unsafe { detect_loop(shared, rx) });
        Self {
            observation,
            wake,
            foreground: 0,
        }
    }
}
impl DockTargetProbe {
    /// 闪语自身窗口和短暂空前台只能延续已确认的聊天关系。
    /// 外部窗口切换即清除结论；同一个 HWND 的可见性及身份由后台周期复核。
    pub unsafe fn observe(&mut self, foreground: HWND, extend: bool) {
        let window = foreground.0 as i64;
        if !extend && (window != self.foreground || window == 0) {
            let target = candidate(foreground);
            if self.observation.lock().unwrap().select(target) {
                let _ = self.wake.try_send(());
            }
        }
        self.foreground = window;
    }
    pub fn confirmed(&self) -> Option<i64> {
        self.observation.lock().unwrap().confirmed(Instant::now())
    }
}

unsafe fn process_id(window: HWND) -> u32 {
    let mut pid = 0;
    GetWindowThreadProcessId(window, Some(&mut pid));
    pid
}
unsafe fn candidate(window: HWND) -> Option<Target> {
    let pid = process_id(window);
    if window.0.is_null() || pid == std::process::id() {
        return None;
    }
    let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
    let mut path = [0u16; 4096];
    let mut len = path.len() as u32;
    let result = QueryFullProcessImageNameW(
        process,
        PROCESS_NAME_WIN32,
        PWSTR(path.as_mut_ptr()),
        &mut len,
    );
    let _ = CloseHandle(process);
    result.ok()?;
    let path = String::from_utf16_lossy(&path[..len as usize]);
    let name = Path::new(&path).file_name()?.to_string_lossy();
    // 验收身份仅允许隔离 Debug 客户端使用，不能推广到正式企业微信。
    let isolated = std::env::var_os("SHANYU_TEST_DATA_DIR").is_some();
    let fixture = name.eq_ignore_ascii_case("ShanyuDockHost.exe");
    (accepted_process(&name, isolated) && accepted_class(&window_class(window)?, fixture))
        .then_some(Target {
            window: window.0 as i64,
            pid,
            fixture,
        })
}

/// 键盘撤回仅放行已识别的企业微信输入宿主；不把全局快捷键扩展到未知自绘控件。
pub(super) unsafe fn supports_keyboard_replacement(window: HWND) -> bool {
    candidate(window).is_some()
}

unsafe fn detect_loop(shared: Arc<Mutex<Observation>>, rx: mpsc::Receiver<()>) {
    let mut failed = false;
    loop {
        if matches!(
            rx.recv_timeout(INTERVAL),
            Err(mpsc::RecvTimeoutError::Disconnected)
        ) {
            return;
        }
        let (target, revision) = {
            let observation = shared.lock().unwrap();
            (observation.target, observation.revision)
        };
        let Some(target) = target else { continue };
        let result = valid_target(target);
        if result.is_err() && !failed {
            log::warn!("无法确认企业微信窗口的位置，已停止自动吸附。");
        }
        failed = result.is_err();
        shared
            .lock()
            .unwrap()
            .publish(revision, result.unwrap_or(false), Instant::now());
    }
}

fn accepted_process(name: &str, isolated: bool) -> bool {
    name.eq_ignore_ascii_case("WXWork.exe")
        || (cfg!(debug_assertions) && isolated && name.eq_ignore_ascii_case("ShanyuDockHost.exe"))
}

/// 主窗口与客户独立聊天类已实机核实，普通独立聊天类来自客服宝的识别表。
/// 不以标题或同进程关系放行其他窗口。
/// WinForms 会给注册类套上 WindowsForms10 和 app 后缀；只对已隔离的专用宿主去掉包装，
/// 随后仍使用同一份精确名单。正式企业微信不能借用宿主包装类名。
fn accepted_class(name: &str, fixture: bool) -> bool {
    let name = if fixture {
        name.strip_prefix("WindowsForms10.")
            .and_then(|name| name.split_once(".app.").map(|parts| parts.0))
            .unwrap_or(name)
    } else {
        name
    };
    matches!(
        name,
        "WeWorkWindow" | "WwStandaloneConversationWnd" | "CRMCustomerStandaloneWindow"
    )
}

unsafe fn window_class(window: HWND) -> Option<String> {
    let mut name = [0u16; 256];
    let len = GetClassNameW(window, &mut name);
    (len > 0).then(|| String::from_utf16_lossy(&name[..len as usize]))
}

unsafe fn valid_target(target: Target) -> windows::core::Result<bool> {
    let window = HWND(target.window as usize as *mut std::ffi::c_void);
    if !IsWindow(Some(window)).as_bool()
        || !IsWindowVisible(window).as_bool()
        || IsIconic(window).as_bool()
        || process_id(window) != target.pid
        || !window_class(window).is_some_and(|name| accepted_class(&name, target.fixture))
    {
        return Ok(false);
    }
    let mut rect = RECT::default();
    GetWindowRect(window, &mut rect)?;
    Ok(rect.right > rect.left && rect.bottom > rect.top)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn target(window: i64) -> Option<Target> {
        Some(Target {
            window,
            pid: 42,
            fixture: true,
        })
    }
    #[test]
    fn returning_to_same_window_rejects_old_async_result() {
        let mut observation = Observation::default();
        let now = Instant::now();
        observation.select(target(1));
        let revision = observation.revision;
        observation.publish(revision, true, now);
        assert_eq!(observation.confirmed(now), Some(1));
        observation.select(target(2));
        assert_eq!(observation.confirmed(now), None);
        observation.select(target(1));
        observation.publish(revision, true, now);
        assert_eq!(observation.confirmed(now), None);
        observation.publish(observation.revision, true, now);
        assert_eq!(observation.confirmed(now), Some(1));
        assert_eq!(observation.confirmed(now + RESULT_LIFETIME), None);
        observation.publish(observation.revision, false, now);
        assert_eq!(observation.confirmed(now), None);
    }
    #[test]
    fn absent_target_never_qualifies() {
        let mut observation = Observation::default();
        observation.publish(0, true, Instant::now());
        assert_eq!(observation.confirmed(Instant::now()), None);
    }
    #[test]
    fn only_main_and_standalone_wecom_classes_qualify() {
        for name in [
            "WeWorkWindow",
            "WwStandaloneConversationWnd",
            "CRMCustomerStandaloneWindow",
        ] {
            assert!(accepted_class(name, false));
            assert!(accepted_class(
                &format!("WindowsForms10.{name}.app.0.fixture"),
                true
            ));
            assert!(!accepted_class(
                &format!("WindowsForms10.{name}.app.0.fixture"),
                false
            ));
        }
        for name in [
            "",
            "WeChatLogin",
            "SearchResultWindow2",
            "PerryShadowWnd",
            "TitleBarWindow",
            "ImagePreview",
            "ImagePreviewWnd",
            "VideoPlayerWnd",
            "WeWorkWindowExtra",
            "CRMCustomerStandaloneWindowExtra",
            "weworkwindow",
            "WindowsForms10.Window.8.app.0",
        ] {
            assert!(!accepted_class(name, false), "{name}");
            assert!(!accepted_class(name, true), "{name}");
            assert!(
                !accepted_class(&format!("WindowsForms10.{name}.app.0.fixture"), true),
                "{name}"
            );
        }
    }
    #[test]
    fn process_identity_is_required_and_fixture_is_isolated_debug_only() {
        for name in ["WXWork.exe", "wxwork.EXE"] {
            assert!(accepted_process(name, false));
        }
        assert!(!accepted_process("ShanyuDockHost.exe", false));
        assert_eq!(
            accepted_process("ShanyuDockHost.exe", true),
            cfg!(debug_assertions)
        );
        for name in [
            "Other.exe",
            "WXWorkWeb.exe",
            "WXWork.exe.bak",
            "ShanyuIndependentHost.exe",
        ] {
            assert!(!accepted_process(name, true), "{name}");
        }
    }
}
