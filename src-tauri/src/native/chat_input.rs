use std::{
    path::Path,
    sync::{mpsc, Arc, Mutex},
    time::{Duration, Instant},
};
use windows::{
    core::PWSTR,
    Win32::{
        Foundation::{CloseHandle, HWND, RECT},
        System::{
            Com::{
                CoCreateInstance, CoInitializeEx, CoUninitialize, CLSCTX_INPROC_SERVER,
                COINIT_MULTITHREADED,
            },
            Threading::{
                OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32,
                PROCESS_QUERY_LIMITED_INFORMATION,
            },
            Variant::VARIANT,
        },
        UI::{
            Accessibility::*,
            WindowsAndMessaging::{
                GetWindowRect, GetWindowThreadProcessId, IsIconic, IsWindow, IsWindowVisible,
            },
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

/// 只传递句柄和检测结论，不跨线程传递 COM 对象或聊天内容。
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

/// UI Automation 的跨进程调用可能等待目标应用；独立 MTA 线程检测，
/// 原生跟随与键盘监听只读取短锁内的结论，不能被查询卡住。
/// 通知队列只保留一个唤醒信号，实际请求始终读取最新目标，退出时关闭发送端即可。
pub struct ChatInputProbe {
    observation: Arc<Mutex<Observation>>,
    wake: mpsc::SyncSender<()>,
    foreground: i64,
}
impl Default for ChatInputProbe {
    fn default() -> Self {
        let observation = Arc::new(Mutex::new(Observation::default()));
        let shared = observation.clone();
        let (wake, rx) = mpsc::sync_channel(1);
        std::thread::spawn(move || unsafe {
            if let Err(error) = CoInitializeEx(None, COINIT_MULTITHREADED).ok() {
                log::warn!("无法初始化聊天输入框检测，暂不自动吸附：{error}");
                return;
            }
            // COM 接口必须先释放，再结束当前线程的 COM apartment。
            if let Err(error) = detect_loop(shared, rx) {
                log::warn!("聊天输入框检测不可用，暂不自动吸附：{error}");
            }
            CoUninitialize();
        });
        Self {
            observation,
            wake,
            foreground: 0,
        }
    }
}
impl ChatInputProbe {
    /// 闪语自身窗口和短暂空前台只能延续已确认的聊天关系。
    /// 外部窗口切换即清除结论；同一个 HWND 的页面变化由后台周期复核。
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
    let fixture = cfg!(debug_assertions)
        && std::env::var_os("SHANYU_TEST_DATA_DIR").is_some()
        && name.eq_ignore_ascii_case("ShanyuDockHost.exe");
    (fixture || name.eq_ignore_ascii_case("WXWork.exe")).then_some(Target {
        window: window.0 as i64,
        pid,
        fixture,
    })
}

unsafe fn detect_loop(
    shared: Arc<Mutex<Observation>>,
    rx: mpsc::Receiver<()>,
) -> windows::core::Result<()> {
    let automation: IUIAutomation2 = CoCreateInstance(&CUIAutomation8, None, CLSCTX_INPROC_SERVER)?;
    automation.SetAutoSetFocus(false)?;
    automation.SetConnectionTimeout(200)?;
    automation.SetTransactionTimeout(200)?;
    let edit = automation.CreatePropertyCondition(
        UIA_ControlTypePropertyId,
        &VARIANT::from(UIA_EditControlTypeId.0),
    )?;
    let document = automation.CreatePropertyCondition(
        UIA_ControlTypePropertyId,
        &VARIANT::from(UIA_DocumentControlTypeId.0),
    )?;
    let condition = automation.CreateOrCondition(&edit, &document)?;
    let mut warned_unverified = false;
    let mut failed = false;
    loop {
        if matches!(
            rx.recv_timeout(INTERVAL),
            Err(mpsc::RecvTimeoutError::Disconnected)
        ) {
            return Ok(());
        }
        let (target, revision) = {
            let observation = shared.lock().unwrap();
            (observation.target, observation.revision)
        };
        let Some(target) = target else { continue };
        if !target.fixture && !warned_unverified {
            log::warn!("当前企业微信的消息输入控件尚未完成实机核实，暂不自动吸附；可通过托盘或快捷键独立打开闪语。");
            warned_unverified = true;
        }
        let result = contains_input(&automation, &condition, target);
        if result.is_err() && !failed {
            log::warn!("无法确认当前页面的聊天输入框，已停止自动吸附。");
        }
        failed = result.is_err();
        shared
            .lock()
            .unwrap()
            .publish(revision, result.unwrap_or(false), Instant::now());
    }
}

/// 不读取 Name、Value、Text 或消息列表。控件身份只能来自已实机确认的元数据，
/// 不能把普通 Edit、搜索框、窗口标题或未核实的类名当作聊天消息编辑器。
fn verified_identity(automation_id: &str, fixture: bool) -> bool {
    // 真实企业微信尚无已核实的标识，明确拒绝；待实机对比聊天／搜索／图片页后补充。
    fixture && automation_id == "ShanyuChatInput"
}

fn usable_input(
    visible: bool,
    enabled: bool,
    focusable: bool,
    password: bool,
    read_only: bool,
    bounds: RECT,
    window: RECT,
) -> bool {
    visible
        && enabled
        && focusable
        && !password
        && !read_only
        && bounds.right > bounds.left
        && bounds.bottom > bounds.top
        && bounds.left < window.right
        && bounds.right > window.left
        && bounds.top < window.bottom
        && bounds.bottom > window.top
}

unsafe fn contains_input(
    automation: &IUIAutomation2,
    condition: &IUIAutomationCondition,
    target: Target,
) -> windows::core::Result<bool> {
    let window = HWND(target.window as usize as *mut std::ffi::c_void);
    if !IsWindow(Some(window)).as_bool()
        || !IsWindowVisible(window).as_bool()
        || IsIconic(window).as_bool()
        || process_id(window) != target.pid
    {
        return Ok(false);
    }
    let mut rect = RECT::default();
    GetWindowRect(window, &mut rect)?;
    let root = automation.ElementFromHandle(window)?;
    let elements = root.FindAll(TreeScope_Descendants, condition)?;
    let deadline = Instant::now() + INTERVAL;
    for index in 0..elements.Length()? {
        if Instant::now() >= deadline {
            return Ok(false);
        }
        let element = elements.GetElement(index)?;
        if element.CurrentProcessId()? as u32 != target.pid
            || !verified_identity(&element.CurrentAutomationId()?.to_string(), target.fixture)
        {
            continue;
        }
        let pattern: IUIAutomationValuePattern = element.GetCurrentPatternAs(UIA_ValuePatternId)?;
        if usable_input(
            !element.CurrentIsOffscreen()?.as_bool(),
            element.CurrentIsEnabled()?.as_bool(),
            element.CurrentIsKeyboardFocusable()?.as_bool(),
            element.CurrentIsPassword()?.as_bool(),
            pattern.CurrentIsReadOnly()?.as_bool(),
            element.CurrentBoundingRectangle()?,
            rect,
        ) {
            return Ok(true);
        }
    }
    Ok(false)
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
    fn absent_target_and_unverified_editors_never_qualify() {
        let mut observation = Observation::default();
        observation.publish(0, true, Instant::now());
        assert_eq!(observation.confirmed(Instant::now()), None);
        assert!(!verified_identity("SearchInput", true));
        assert!(!verified_identity("", true));
        assert!(!verified_identity("ShanyuChatInput", false));
        assert!(verified_identity("ShanyuChatInput", true));
    }
    #[test]
    fn hidden_disabled_readonly_password_and_empty_inputs_are_rejected() {
        let bounds = RECT {
            left: 10,
            top: 10,
            right: 100,
            bottom: 50,
        };
        assert!(usable_input(true, true, true, false, false, bounds, bounds));
        for (visible, enabled, focusable, password, read_only) in [
            (false, true, true, false, false),
            (true, false, true, false, false),
            (true, true, false, false, false),
            (true, true, true, true, false),
            (true, true, true, false, true),
        ] {
            assert!(!usable_input(
                visible, enabled, focusable, password, read_only, bounds, bounds
            ));
        }
        assert!(!usable_input(
            true,
            true,
            true,
            false,
            false,
            RECT::default(),
            bounds
        ));
        let outside = RECT {
            left: 200,
            top: 10,
            right: 300,
            bottom: 50,
        };
        assert!(!usable_input(
            true, true, true, false, false, outside, bounds
        ));
    }
}
