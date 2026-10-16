use crate::model::{Library, Phrase, Result, Settings};
use serde::{Deserialize, Serialize};
use std::sync::{
    atomic::{AtomicBool, Ordering},
    mpsc, Arc, Mutex, OnceLock, RwLock,
};
use tauri::{AppHandle, Emitter, Manager};

#[path = "native/shortcuts.rs"]
mod shortcuts;
pub use shortcuts::{parse_shortcut, Shortcut};
#[path = "native/candidates.rs"]
mod candidates;
pub use candidates::CandidatePayload;
#[cfg(windows)]
#[path = "native/dock_target.rs"]
mod dock_target;
#[cfg(windows)]
#[path = "native/docking.rs"]
mod docking;
#[cfg(windows)]
#[path = "native/edge_hide.rs"]
mod edge_hide;
#[cfg(windows)]
#[path = "native/minimized_float.rs"]
mod minimized_float;
#[path = "native/preview.rs"]
mod preview;
pub use preview::{PreviewAnchor, PreviewPayload};
/// 自身回放与发送带独立标记，WebView2 的原生回调也能识别，避免把回放重新当作快捷键。
pub(crate) const INTERNAL_INPUT_MARKER: usize = 0x53594E41;

/// 客户端窗口默认使用直角，后续创建的编辑器和设置窗口也应用同一策略；最小化悬浮窗单独裁剪圆角。
/// Windows 10 本身为直角且不支持该 DWM 属性，忽略其 E_INVALIDARG；其他失败记录中文日志。
#[cfg(windows)]
pub fn square_window_corners(window: &tauri::Window) {
    use windows::Win32::{
        Foundation::E_INVALIDARG,
        Graphics::Dwm::{DwmSetWindowAttribute, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_DONOTROUND},
    };
    let handle = match window.hwnd() {
        Ok(handle) => handle,
        Err(error) => {
            log::warn!("无法获取窗口句柄，直角设置未应用：{error}");
            return;
        }
    };
    let preference = DWMWCP_DONOTROUND;
    let result = unsafe {
        DwmSetWindowAttribute(
            platform::hwnd(handle.0 as i64),
            DWMWA_WINDOW_CORNER_PREFERENCE,
            std::ptr::from_ref(&preference).cast(),
            std::mem::size_of_val(&preference) as u32,
        )
    };
    if let Err(error) = result {
        if error.code() != E_INVALIDARG {
            log::warn!("无法将 {} 窗口设置为直角：{error}", window.label());
        }
    }
}

pub fn startup_error(message: &str) {
    log::error!("{message}");
    #[cfg(windows)]
    unsafe {
        use windows::{
            core::PCWSTR,
            Win32::UI::WindowsAndMessaging::{MessageBoxW, MB_ICONERROR, MB_OK},
        };
        let body = platform::wide(message);
        let title = platform::wide("闪语启动失败");
        MessageBoxW(
            None,
            PCWSTR(body.as_ptr()),
            PCWSTR(title.as_ptr()),
            MB_OK | MB_ICONERROR,
        );
    }
}

/// 专用菜单不应出现在任务栏／Alt+Tab；每次打开时修正 Tauri 可能重建的扩展样式。
pub(crate) fn prepare_common_menu(handle: i64) -> Result<()> {
    unsafe { platform::background_window(platform::hwnd(handle)) }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TargetContext {
    pub hwnd: i64,
    pub focus: i64,
    pub pid: u32,
}
/// 初始模式为自动跟随，随后处理手动启动意图；不能等同于用户主动隐藏。
/// 独立显示由手动启动、托盘或快捷键唤起；重新使用目标后回到跟随，手动隐藏则必须再次唤起。
#[derive(Default, Debug, Clone, Copy, PartialEq, Eq)]
enum DisplayMode {
    #[default]
    Automatic,
    Standalone,
    Hidden,
}
impl DisplayMode {
    fn update(&mut self, dock: bool, available: bool, target_active: bool) -> bool {
        if *self == Self::Hidden {
            return false;
        }
        if dock && target_active {
            *self = Self::Automatic;
        }
        *self == Self::Standalone || (dock && available)
    }
}
/// 焦点记录、全局快捷键和吸附共享一个轻量后台线程；发送另用独立线程，避免分段等待阻塞窗口跟随。
/// 全局鼠标钩子使用独立消息线程，不能等吸附轮询休眠或窗口查询完成后才放行系统鼠标事件。
/// target 只在外部窗口进入闪语时冻结；切到其他软件不会扩大话术快捷键的默认生效范围。
/// 管理窗口可独立开关；不使用单一“最后打开窗口”句柄，避免关掉一个就提前解除暂停。
#[derive(Default)]
pub struct ManagementWindows {
    /// 文件选择可由多个编辑窗口打开，按窗口身份记录，关闭其中一个不能解除其他窗口的暂停。
    pub file_windows: Mutex<std::collections::HashSet<String>>,
    /// 自定义短语菜单需要与系统菜单一样暂停快捷键和贴边收起。
    pub common_menu: std::sync::atomic::AtomicI64,
    pub search_history_confirm: std::sync::atomic::AtomicI64,
    pub settings: std::sync::atomic::AtomicI64,
    pub teams: std::sync::atomic::AtomicI64,
    pub users: std::sync::atomic::AtomicI64,
    pub company: std::sync::atomic::AtomicI64,
}
impl ManagementWindows {
    /// 设置是独立窗口，不阻止主窗口贴边收起；其他管理窗口保持原有保护。
    pub(crate) fn blocks_edge_hide(&self) -> bool {
        !self.file_windows.lock().unwrap().is_empty()
            || self.common_menu.load(Ordering::Acquire) != 0
            || self.search_history_confirm.load(Ordering::Acquire) != 0
            || self.teams.load(Ordering::Acquire) != 0
            || self.users.load(Ordering::Acquire) != 0
            || self.company.load(Ordering::Acquire) != 0
    }
    pub(crate) fn is_open(&self) -> bool {
        self.settings.load(Ordering::Acquire) != 0 || self.blocks_edge_hide()
    }
}

/// 匹配表、候选内容与账号世代一次替换，避免刷新中读取到不同批次。
#[derive(Clone, Default)]
pub struct ShortcutBindings {
    generation: u64,
    keys: Vec<(String, Shortcut)>,
    phrases: Vec<Phrase>,
    member_epoch: Option<u64>,
}
pub struct NativeState {
    pub common_menu: Mutex<crate::common_menu::MenuState>,
    pub search_history_confirm: Mutex<Option<crate::search_history_confirm::Session>>,
    pub common_menu_handle: std::sync::atomic::AtomicI64,
    pub target: Mutex<Option<TargetContext>>,
    pub settings: RwLock<Settings>,
    pub bindings: RwLock<ShortcutBindings>,
    pub candidates: Mutex<candidates::CandidateState>,
    /// 只由主窗口同步绘制结果；文件槽位为空，隐藏窗口继续沿用最后快照。
    pub sequence_map: RwLock<(Vec<Option<String>>, bool)>,
    /// 当前正在编辑或录入快捷键的客户端窗口。输入框与模态框中不触发话术，避免抢走录入按键。
    pub shortcut_input: std::sync::atomic::AtomicI64,
    pub search_input: std::sync::atomic::AtomicI64,
    /// 设置、用户名与共享话术各自记录句柄，最后一个管理窗口关闭后才恢复业务快捷键。
    pub management_windows: ManagementWindows,
    /// 总高度与向上展开高度必须来自同一次布局，避免跟随线程读到半次更新而移动输入行。
    pub common_geometry: Mutex<(i32, i32)>,
    pub error: Mutex<Option<String>>,
    display_mode: Mutex<DisplayMode>,
    /// 只有主窗口主动最小化才能置位，自动隐藏和贴边收起不创建恢复小条。
    minimized: AtomicBool,
    /// 仅抑制当前一次最小化的小条，重新打开主窗口再最小化时清除。
    float_dismissed: AtomicBool,
    #[cfg(windows)]
    float_window: OnceLock<i64>,
    #[cfg(windows)]
    float_position: Mutex<Option<minimized_float::Position>>,
    pub running: AtomicBool,
    pub window_size: Mutex<crate::window_size::WindowSize>,
    pub busy: Arc<AtomicBool>,
    pub cancelled: Arc<AtomicBool>,
    /// 常驻窗口的句柄在启动时读取。后台不调用 Tauri 的同步句柄查询，
    /// 避免主线程等待设置回执、后台线程等待主线程处理句柄查询而互相阻塞。
    #[cfg(windows)]
    windows: OnceLock<(i64, i64, i64)>,
    #[cfg(windows)]
    candidate_window: OnceLock<i64>,
    #[cfg(windows)]
    pub preview: Mutex<preview::PreviewState>,
    app: OnceLock<AppHandle>,
    control: Mutex<Option<mpsc::Sender<Control>>>,
}
enum Control {
    Settings(Box<Settings>, mpsc::SyncSender<Result<()>>),
    Show(String),
    Hide,
    Minimize,
    HideFloat,
}
impl NativeState {
    pub fn new(settings: Settings, busy: Arc<AtomicBool>, cancelled: Arc<AtomicBool>) -> Self {
        Self {
            target: Mutex::new(None),
            settings: RwLock::new(settings),
            bindings: RwLock::new(ShortcutBindings::default()),
            candidates: Mutex::new(candidates::CandidateState::default()),
            sequence_map: RwLock::new((vec![], true)),
            common_menu: Mutex::new(crate::common_menu::MenuState::default()),
            search_history_confirm: Mutex::new(None),
            common_menu_handle: std::sync::atomic::AtomicI64::new(0),
            shortcut_input: std::sync::atomic::AtomicI64::new(0),
            search_input: std::sync::atomic::AtomicI64::new(0),
            management_windows: ManagementWindows::default(),
            common_geometry: Mutex::new((34, 0)),
            error: Mutex::new(None),
            display_mode: Mutex::new(DisplayMode::default()),
            minimized: AtomicBool::new(false),
            float_dismissed: AtomicBool::new(false),
            #[cfg(windows)]
            float_window: OnceLock::new(),
            #[cfg(windows)]
            float_position: Mutex::new(None),
            running: AtomicBool::new(true),
            window_size: Mutex::new(crate::window_size::WindowSize::default()),
            busy,
            cancelled,
            #[cfg(windows)]
            windows: OnceLock::new(),
            #[cfg(windows)]
            candidate_window: OnceLock::new(),
            #[cfg(windows)]
            preview: Mutex::new(preview::PreviewState::default()),
            app: OnceLock::new(),
            control: Mutex::new(None),
        }
    }
    pub fn start(self: &Arc<Self>, app: &AppHandle) -> Result<()> {
        let _ = self.app.set(app.clone());
        #[cfg(windows)]
        {
            let handle = |label: &str| -> Result<i64> {
                app.get_webview_window(label)
                    .ok_or("闪语窗口尚未创建。")?
                    .hwnd()
                    .map(|handle| handle.0 as i64)
                    .map_err(|e| format!("无法获取闪语窗口句柄：{e}"))
            };
            let windows = (
                handle("main")?,
                handle("common-dock")?,
                handle("phrase-preview")?,
            );
            for window in [windows.0, windows.1, windows.2] {
                unsafe {
                    platform::background_window(platform::hwnd(window))?;
                }
            }
            self.windows
                .set(windows)
                .map_err(|_| "Windows服务已经启动。".to_string())?;
            let candidate = handle("shortcut-candidates")?;
            unsafe {
                platform::background_window(platform::hwnd(candidate))?;
            }
            let _ = self.candidate_window.set(candidate);
            let floating = handle("minimized-float")?;
            unsafe {
                platform::background_window(platform::hwnd(floating))?;
            }
            let _ = self.float_window.set(floating);
            match minimized_float::read_position(
                &app.state::<crate::AppState>().device_store.lock().unwrap(),
            ) {
                Ok(position) => *self.float_position.lock().unwrap() = position,
                Err(error) => log::warn!("{error}"),
            }
            *self.target.lock().unwrap() = unsafe { platform::capture() };
        }
        let (tx, rx) = mpsc::channel();
        *self.control.lock().unwrap() = Some(tx);
        let this = self.clone();
        std::thread::spawn(move || platform::monitor(this, rx));
        Ok(())
    }
    /// 只保存正常主窗口的客户区尺寸；贴边裁剪只改变区域，不会把细条宽度记入下次启动。
    pub fn remember_main_size(&self, flush: bool) {
        #[cfg(windows)]
        unsafe {
            use windows::Win32::{
                Foundation::RECT,
                UI::{HiDpi::GetDpiForWindow, WindowsAndMessaging::*},
            };
            let Some(windows) = self.windows.get() else {
                return;
            };
            let main = platform::hwnd(windows.0);
            let mut size = self.window_size.lock().unwrap();
            if IsWindowVisible(main).as_bool()
                && !IsIconic(main).as_bool()
                && !IsZoomed(main).as_bool()
                && !self.minimized.load(Ordering::Acquire)
            {
                let mut client = RECT::default();
                if GetClientRect(main, &mut client).is_ok() {
                    if size.restore_pending {
                        if let Some(saved) = size.current {
                            // 无边框 WebView 首次显示前可能仍带30DIP系统标题栏测量值。
                            // 显示后按实际客户区补差，不能将初始化产生的高度反写成用户尺寸。
                            let scale = f64::from(GetDpiForWindow(main).max(96)) / 96.0;
                            let width = (saved.width * scale).round() as i32;
                            let height = (saved.height * scale).round() as i32;
                            if client.right != width || client.bottom != height {
                                let mut outer = RECT::default();
                                if GetWindowRect(main, &mut outer).is_ok() {
                                    let _ = SetWindowPos(
                                        main,
                                        None,
                                        0,
                                        0,
                                        outer.right - outer.left + width - client.right,
                                        outer.bottom - outer.top + height - client.bottom,
                                        SWP_NOMOVE
                                            | SWP_NOZORDER
                                            | SWP_NOACTIVATE
                                            | SWP_ASYNCWINDOWPOS,
                                    );
                                }
                                return;
                            }
                        }
                        size.restore_pending = false;
                    }
                    if let Some(current) = crate::window_size::Size::from_physical(
                        client.right - client.left,
                        client.bottom - client.top,
                        GetDpiForWindow(main),
                    ) {
                        if size.observe(current, std::time::Instant::now()) {
                            if let Err(error) = size.persist() {
                                log::warn!("{error}");
                            }
                        }
                    }
                }
            }
            if flush {
                if let Err(error) = size.persist() {
                    log::warn!("{error}");
                }
            }
        }
    }
    pub fn apply_settings(&self, settings: &Settings) -> Result<()> {
        settings.validate()?;
        let previous = self.settings.read().unwrap().clone();
        self.configure_background(settings)?;
        if let Some(window) = self
            .app
            .get()
            .and_then(|app| app.get_webview_window("main"))
        {
            if let Err(error) = if previous.pinned != settings.pinned {
                window.set_always_on_top(settings.pinned)
            } else {
                Ok(())
            } {
                let _ = self.configure_background(&previous);
                return Err(format!("置顶设置失败：{error}"));
            }
            if let Err(error) = if previous.free_resize != settings.free_resize {
                window.set_resizable(settings.free_resize)
            } else {
                Ok(())
            } {
                let _ = window.set_always_on_top(previous.pinned);
                let _ = self.configure_background(&previous);
                return Err(format!("窗口拉宽设置失败：{error}"));
            }
        }
        Ok(())
    }
    fn configure_background(&self, settings: &Settings) -> Result<()> {
        let (tx, rx) = mpsc::sync_channel(1);
        self.control
            .lock()
            .unwrap()
            .as_ref()
            .ok_or("Windows服务尚未启动。")?
            .send(Control::Settings(Box::new(settings.clone()), tx))
            .map_err(|_| "Windows服务已停止。")?;
        rx.recv_timeout(std::time::Duration::from_secs(3))
            .map_err(|_| "Windows设置更新超时，请稍后重试。".to_string())?
    }
    pub fn update_bindings(&self, library: &Library, member_epoch: Option<u64>) -> Result<()> {
        let settings = self.settings.read().unwrap();
        let mut phrases = vec![];
        for root in library
            .categories
            .iter()
            .filter(|c| c.kind == "phrase" && c.parent_id.is_none())
        {
            for group in std::iter::once(None).chain(
                library
                    .categories
                    .iter()
                    .filter(|c| c.parent_id.as_deref() == Some(&root.id))
                    .map(|c| Some(c.id.as_str())),
            ) {
                phrases.extend(
                    library
                        .phrases
                        .iter()
                        .filter(|p| p.root_id == root.id && p.group_id.as_deref() == group)
                        .cloned(),
                );
            }
        }
        let mut bindings = vec![];
        for p in phrases.iter().filter(|p| !p.shortcut.is_empty()) {
            let shortcut = parse_shortcut(&p.shortcut)?;
            if settings.shortcut_reserved(&shortcut) {
                return Err("话术快捷键与导航或序号快捷键冲突。".into());
            }
            let canonical = shortcut.canonical();
            if !bindings.iter().any(|(key, _)| key == &canonical) {
                bindings.push((canonical, shortcut));
            }
        }
        let mut current = self.bindings.write().unwrap();
        if current.keys != bindings
            || current.phrases != phrases
            || current.member_epoch != member_epoch
        {
            *current = ShortcutBindings {
                generation: current.generation.wrapping_add(1),
                keys: bindings,
                phrases,
                member_epoch,
            };
        }
        Ok(())
    }
    /// 搜索仅放行序号组合键；编辑和模态输入由上下文校验整体暂停。
    fn active_bindings(
        &self,
        search: bool,
        snapshot: &ShortcutBindings,
    ) -> Vec<(String, Shortcut)> {
        let settings = self.settings.read().unwrap();
        let mut bindings = if settings.custom_hotkeys && !search {
            snapshot.keys.clone()
        } else {
            vec![]
        };
        if settings.sequence_hotkeys {
            bindings.extend(
                settings.sequence_bindings.iter().filter_map(|value| {
                    parse_shortcut(value).ok().map(|key| (key.canonical(), key))
                }),
            );
        }
        bindings
    }
    pub fn show(&self, action: &str) {
        self.send_control(Control::Show(action.into()));
    }
    pub fn hide(&self) {
        self.send_control(Control::Hide);
    }
    pub fn minimize(&self) {
        self.send_control(Control::Minimize);
    }
    pub fn hide_float(&self) {
        self.send_control(Control::HideFloat);
    }
    /// 正常退出时补存最后坐标，不能让工作池中的位置写入被进程退出截断。
    pub fn persist_float_position(&self) {
        #[cfg(windows)]
        if let Some(app) = self.app.get() {
            if let Err(error) = minimized_float::persist_position(app) {
                log::warn!("悬浮窗位置未能保存：{error}");
            }
        }
    }
    /// 窗口动作与设置按同一队列顺序处理，先定位再显示，避免主线程唤起与后台隐藏相互覆盖。
    fn send_control(&self, action: Control) {
        let sent = self
            .control
            .lock()
            .unwrap()
            .as_ref()
            .is_some_and(|tx| tx.send(action).is_ok());
        if !sent {
            self.notify_error("窗口操作无法完成，Windows服务尚未启动或已经停止。".into());
        }
    }
    pub fn toggle(&self) {
        #[cfg(windows)]
        let visible = self.windows.get().is_some_and(|windows| unsafe {
            use windows::Win32::UI::WindowsAndMessaging::{IsIconic, IsWindowVisible};
            let main = platform::hwnd(windows.0);
            IsWindowVisible(main).as_bool() && !IsIconic(main).as_bool()
        });
        #[cfg(not(windows))]
        let visible = self
            .app
            .get()
            .and_then(|a| a.get_webview_window("main"))
            .and_then(|w| w.is_visible().ok())
            .unwrap_or(false);
        if visible {
            self.hide();
        } else {
            self.show("show");
        }
    }
    fn notify_error(&self, message: String) {
        let mut previous = self.error.lock().unwrap();
        if previous.as_ref() != Some(&message) {
            log::error!("{message}");
            if let Some(app) = self.app.get() {
                let _ = app.emit("native-error", &message);
            }
            *previous = Some(message);
        }
    }
}

#[cfg(windows)]
#[path = "native/clipboard.rs"]
mod clipboard;
#[cfg(windows)]
pub use clipboard::{copy_files, copy_text, WindowsDriver};
#[cfg(windows)]
pub use platform::{consume_delivery_key, file_icon, install_webview_shortcuts, open_file};
#[cfg(windows)]
mod platform {
    use super::shortcuts::{KeyAction, KeyEvent, ShortcutMatcher};
    use super::*;
    use std::{
        mem::size_of,
        path::Path,
        time::{Duration, Instant},
    };
    use windows::{
        core::PCWSTR,
        Win32::{
            Foundation::*,
            Graphics::{
                Dwm::{DwmGetWindowAttribute, DWMWA_EXTENDED_FRAME_BOUNDS},
                Gdi::*,
            },
            System::{LibraryLoader::GetModuleHandleW, Registry::*},
            UI::{
                HiDpi::{GetDpiForMonitor, GetDpiForWindow, MDT_EFFECTIVE_DPI},
                Input::{
                    GetCurrentInputMessageSource, KeyboardAndMouse::*, IMO_INJECTED,
                    INPUT_MESSAGE_SOURCE,
                },
                WindowsAndMessaging::*,
            },
        },
    };
    static HOOK_STATE: OnceLock<Arc<NativeState>> = OnceLock::new();
    static MATCHER: OnceLock<Mutex<ShortcutMatcher>> = OnceLock::new();
    // 仅表示主窗口已经进入目标吸附；开关开启但独立显示时仍可自由拖动。
    // 子类回调只读原子状态，不等待跟随线程或设置锁，避免系统移动消息相互阻塞。
    static MAIN_DOCKED: AtomicBool = AtomicBool::new(false);
    pub fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(Some(0)).collect()
    }
    pub fn hwnd(value: i64) -> HWND {
        HWND(value as usize as *mut std::ffi::c_void)
    }
    /// Tauri 的跳过任务栏还会调用 Shell 接口；跟随线程直接显示原生窗口时也必须保留此样式。
    /// 工具窗口不加入任务栏或 Alt+Tab，但仍允许用户通过托盘／快捷键主动激活。
    pub(super) unsafe fn background_window(window: HWND) -> Result<()> {
        let style = GetWindowLongPtrW(window, GWL_EXSTYLE);
        if style & WS_EX_APPWINDOW.0 as isize == 0 && style & WS_EX_TOOLWINDOW.0 as isize != 0 {
            return Ok(());
        }
        SetLastError(WIN32_ERROR(0));
        if SetWindowLongPtrW(
            window,
            GWL_EXSTYLE,
            (style & !(WS_EX_APPWINDOW.0 as isize)) | WS_EX_TOOLWINDOW.0 as isize,
        ) == 0
            && GetLastError().0 != 0
        {
            return Err(format!(
                "无法设置后台窗口的任务栏行为：{}",
                GetLastError().0
            ));
        }
        SetWindowPos(
            window,
            None,
            0,
            0,
            0,
            0,
            SWP_FRAMECHANGED
                | SWP_NOMOVE
                | SWP_NOSIZE
                | SWP_NOZORDER
                | SWP_NOACTIVATE
                | SWP_ASYNCWINDOWPOS,
        )
        .map_err(|error| format!("无法应用后台窗口的任务栏行为：{error}"))
    }
    pub unsafe fn pid(hwnd: HWND) -> u32 {
        let mut pid = 0;
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
        pid
    }
    pub(super) unsafe fn capture() -> Option<TargetContext> {
        let window = GetForegroundWindow();
        if window.0.is_null() || pid(window) == std::process::id() {
            return None;
        }
        let mut name = [0u16; 128];
        let len = GetClassNameW(window, &mut name);
        let name = String::from_utf16_lossy(&name[..len.max(0) as usize]);
        if [
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "Progman",
            "WorkerW",
        ]
        .contains(&name.as_str())
        {
            return None;
        }
        let thread = GetWindowThreadProcessId(window, None);
        let mut gui = GUITHREADINFO {
            cbSize: size_of::<GUITHREADINFO>() as u32,
            ..Default::default()
        };
        let focus = if GetGUIThreadInfo(thread, &mut gui).is_ok() && !gui.hwndFocus.0.is_null() {
            gui.hwndFocus
        } else {
            window
        };
        Some(TargetContext {
            hwnd: window.0 as i64,
            focus: focus.0 as i64,
            pid: pid(window),
        })
    }
    unsafe fn register(settings: &Settings, keep_available: bool) -> Result<()> {
        let mut failure = None;
        for (i, key) in [
            &settings.search_hotkey,
            &settings.window_hotkey,
            &settings.common_hotkey,
        ]
        .iter()
        .enumerate()
        {
            if let Err(e) = RegisterHotKey(
                None,
                i as i32 + 1,
                MOD_ALT | MOD_NOREPEAT,
                key.as_bytes()[0].to_ascii_uppercase() as u32,
            ) {
                let message = format!("Alt+{key}快捷键被占用，无法注册：{e}");
                if !keep_available {
                    for id in 1..=3 {
                        let _ = UnregisterHotKey(None, id);
                    }
                    return Err(message);
                }
                failure.get_or_insert(message);
            }
        }
        failure.map_or(Ok(()), Err)
    }
    unsafe fn shortcut_context(state: &NativeState) -> Option<(i64, i64)> {
        let foreground = GetForegroundWindow();
        let window = foreground.0 as i64;
        let settings = state.settings.read().unwrap();
        let owned = pid(foreground) == std::process::id();
        let active = if owned {
            state
                .windows
                .get()
                .is_some_and(|w| window == w.0 || window == w.1)
                && (state.shortcut_input.load(Ordering::Acquire) != window
                    || state.search_input.load(Ordering::Acquire) == window)
        } else {
            settings.global_phrase_hotkeys
                || state
                    .target
                    .lock()
                    .unwrap()
                    .as_ref()
                    .is_some_and(|t| t.hwnd == window)
        };
        if (!settings.custom_hotkeys && !settings.sequence_hotkeys)
            || !active
            || state.busy.load(Ordering::Acquire)
            || state.management_windows.is_open()
        {
            return None;
        }
        let mut gui = GUITHREADINFO {
            cbSize: size_of::<GUITHREADINFO>() as u32,
            ..Default::default()
        };
        if GetGUIThreadInfo(GetWindowThreadProcessId(foreground, None), &mut gui).is_err()
            || gui.hwndFocus.0.is_null()
        {
            return None;
        }
        Some((window, gui.hwndFocus.0 as i64))
    }
    unsafe fn modifiers() -> u32 {
        [
            (VK_CONTROL, 1),
            (VK_MENU, 2),
            (VK_SHIFT, 4),
            (VK_LWIN, 8),
            (VK_RWIN, 8),
        ]
        .iter()
        .fold(0, |mods, (key, bit)| {
            mods | if GetAsyncKeyState(key.0 as i32) < 0 {
                *bit
            } else {
                0
            }
        })
    }
    fn modifier_key(key: u32) -> bool {
        [
            0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5,
        ]
        .contains(&key)
    }
    unsafe fn apply_key_action(state: &NativeState, action: KeyAction) {
        if action.discarded {
            log::info!("快捷键序列已取消：窗口、输入位置或录入状态发生变化，未向新焦点写入按键。");
        }
        if !action.replay.is_empty() {
            let input = |event: &KeyEvent| INPUT {
                r#type: INPUT_KEYBOARD,
                Anonymous: INPUT_0 {
                    ki: KEYBDINPUT {
                        wVk: VIRTUAL_KEY(event.key as u16),
                        dwFlags: (if event.down {
                            KEYBD_EVENT_FLAGS(0)
                        } else {
                            KEYEVENTF_KEYUP
                        }) | if event.extended {
                            KEYEVENTF_EXTENDEDKEY
                        } else {
                            KEYBD_EVENT_FLAGS(0)
                        },
                        dwExtraInfo: INTERNAL_INPUT_MARKER,
                        ..Default::default()
                    },
                },
            };
            let inputs = action.replay.iter().map(input).collect::<Vec<_>>();
            let submitted = SendInput(&inputs, size_of::<INPUT>() as i32) as usize;
            if submitted != inputs.len() {
                let release = action
                    .replay
                    .iter()
                    .take(submitted)
                    .filter(|e| e.down)
                    .map(|e| input(&KeyEvent { down: false, ..*e }))
                    .collect::<Vec<_>>();
                if !release.is_empty() {
                    let _ = SendInput(&release, size_of::<INPUT>() as i32);
                }
                state.notify_error(
                    "快捷键未完成，但系统无法完整还原普通按键，请检查目标权限。".into(),
                );
            }
        }
    }
    /// 界面 Enter 提交会将焦点移回目标；松键前仍消费原提交键，避免长按继续发送草稿。
    pub fn consume_delivery_key(key: Option<u32>) {
        unsafe {
            if key == Some(0x0D) && GetAsyncKeyState(0x0D) < 0 {
                MATCHER
                    .get_or_init(|| Mutex::new(ShortcutMatcher::default()))
                    .lock()
                    .unwrap()
                    .consume(0x0D);
            }
        }
    }
    /// Alt 导航键由 RegisterHotKey 消费，窗口仍可能收到 Alt 松键并进入隐式系统菜单状态。
    /// 主窗口实际吸附时禁止系统移动和纵向调整；角落按对应左右边处理，仍可调整宽度。
    /// 独立模式及其他窗口沿用原系统操作；Alt+空格、Alt+F4等显式菜单操作继续放行。
    /// 此子类只安装在主窗口和短语横条的所属 UI 线程，窗口销毁时移除，不修改全局键盘状态。
    unsafe extern "system" fn navigation_system_menu(
        window: HWND,
        message: u32,
        w: WPARAM,
        l: LPARAM,
        id: usize,
        main_window: usize,
    ) -> LRESULT {
        use windows::Win32::UI::Shell::{DefSubclassProc, RemoveWindowSubclass};
        if message == WM_SYSCOMMAND && w.0 & 0xFFF0 == SC_KEYMENU as usize && l.0 == 0 {
            return LRESULT(0);
        }
        let docked = main_window != 0 && MAIN_DOCKED.load(Ordering::Acquire);
        if docked && message == WM_SYSCOMMAND {
            let command = w.0 & 0xFFF0;
            if command == SC_MOVE as usize
                || (command == SC_SIZE as usize && !matches!(w.0 & 0xF, 1 | 2))
            {
                return LRESULT(0);
            }
        }
        if message == WM_NCDESTROY {
            let _ = RemoveWindowSubclass(window, Some(navigation_system_menu), id);
        }
        let result = DefSubclassProc(window, message, w, l);
        if docked && message == WM_NCHITTEST {
            return LRESULT(match result.0 as u32 {
                HTTOP | HTBOTTOM => HTCLIENT,
                HTTOPLEFT | HTBOTTOMLEFT => HTLEFT,
                HTTOPRIGHT | HTBOTTOMRIGHT => HTRIGHT,
                _ => return result,
            } as isize);
        }
        result
    }

    /// WebView2 的组合键回调也走原生匹配器，搜索输入不依赖全局钩子是否转发客户端按键。
    /// 已被全局钩子消费的键不会进入 WebView；普通键的重复通知仍由匹配器的按住状态处理。
    pub fn install_webview_shortcuts(
        window: &tauri::WebviewWindow,
        state: Arc<NativeState>,
    ) -> Result<()> {
        use webview2_com::{AcceleratorKeyPressedEventHandler, Microsoft::Web::WebView2::Win32::*};
        let handle = window
            .hwnd()
            .map_err(|e| format!("无法获取导航窗口句柄：{e}"))?
            .0 as i64;
        let main_window = usize::from(window.label() == "main");
        window
            .with_webview(move |webview| unsafe {
                if !windows::Win32::UI::Shell::SetWindowSubclass(
                    hwnd(handle),
                    Some(navigation_system_menu),
                    0,
                    main_window,
                )
                .as_bool()
                {
                    log::error!("无法接入窗口导航保护，Alt键可能唤起系统菜单。");
                }
                let mut token = 0;
                let result = webview.controller().add_AcceleratorKeyPressed(
                    &AcceleratorKeyPressedEventHandler::create(Box::new(move |_, args| {
                        let Some(args) = args else {
                            return Ok(());
                        };
                        let mut source = INPUT_MESSAGE_SOURCE::default();
                        let _ = GetCurrentInputMessageSource(&mut source);
                        let extra = GetMessageExtraInfo().0 as usize;
                        if extra == INTERNAL_INPUT_MARKER {
                            return Ok(());
                        }
                        let test_key = cfg!(debug_assertions)
                            && std::env::var_os("SHANYU_TEST_DATA_DIR").is_some()
                            && extra == 0x53595141;
                        if source.originId == IMO_INJECTED && !test_key {
                            return Ok(());
                        }
                        let mut kind = COREWEBVIEW2_KEY_EVENT_KIND::default();
                        let mut key = 0;
                        let mut status = COREWEBVIEW2_PHYSICAL_KEY_STATUS::default();
                        args.KeyEventKind(&mut kind)?;
                        args.VirtualKey(&mut key)?;
                        args.PhysicalKeyStatus(&mut status)?;
                        let event = KeyEvent {
                            key,
                            down: kind == COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN
                                || kind == COREWEBVIEW2_KEY_EVENT_KIND_SYSTEM_KEY_DOWN,
                            extended: status.IsExtendedKey.as_bool(),
                        };
                        if shortcut_event(&state, event) {
                            args.SetHandled(true)?;
                        }
                        Ok(())
                    })),
                    &mut token,
                );
                if let Err(error) = result {
                    log::error!("无法接入窗口快捷键监听：{error}");
                }
            })
            .map_err(|e| format!("无法接入窗口快捷键监听：{e}"))
    }
    unsafe fn shortcut_event(state: &NativeState, event: KeyEvent) -> bool {
        let context = shortcut_context(state);
        let current_modifiers = modifiers();
        let (candidate_active, candidate_action) = {
            let mut candidates = state.candidates.lock().unwrap();
            let active = candidates.payload.is_some();
            (active, candidates.event(event, context, current_modifiers))
        };
        if candidate_action.changed {
            state.publish_candidates();
        }
        if let Some((index, mode)) = candidate_action.submit {
            if let Some(app) = state.app.get() {
                crate::queue_candidate_delivery(app.clone(), index, mode);
            }
        }
        let search = context.is_some_and(|c| state.search_input.load(Ordering::Acquire) == c.0);
        let snapshot = state.bindings.read().unwrap().clone();
        let mut matcher = MATCHER
            .get_or_init(|| Mutex::new(ShortcutMatcher::default()))
            .lock()
            .unwrap();
        let previous = matcher.rebind(snapshot.generation, context);
        let mut action = matcher.event(
            event,
            if candidate_active || candidate_action.swallowed {
                None
            } else {
                context
            },
            current_modifiers,
            &state.active_bindings(search, &snapshot),
            Instant::now(),
        );
        drop(matcher);
        if !previous.replay.is_empty() {
            let mut events = previous.replay;
            events.append(&mut action.replay);
            action.replay = events;
        }
        // SendInput 会排队；同时回放当前事件并吞掉原事件，保持“首键、后续普通键”的顺序。
        if !action.replay.is_empty() && !action.swallowed {
            action.replay.push(event);
            action.swallowed = true;
        }
        if event.down
            && !action.swallowed
            && !candidate_action.swallowed
            && state.busy.load(Ordering::Acquire)
            && !modifier_key(event.key)
        {
            state.cancelled.store(true, Ordering::Release);
        }
        if let Some(id) = action.fired.take() {
            state.fire_shortcut(&id, &snapshot);
        }
        let swallowed = action.swallowed || candidate_action.swallowed;
        apply_key_action(state, action);
        swallowed
    }
    unsafe extern "system" fn keyboard_hook(code: i32, w: WPARAM, l: LPARAM) -> LRESULT {
        if code >= 0 {
            if let Some(state) = HOOK_STATE.get() {
                let key = &*(l.0 as *const KBDLLHOOKSTRUCT);
                let message = w.0 as u32;
                // 正式输入与自身回放仍忽略合成事件；隔离 Debug 验收仅接受带专用标记的宿主按键。
                let test_key = cfg!(debug_assertions)
                    && std::env::var_os("SHANYU_TEST_DATA_DIR").is_some()
                    && key.dwExtraInfo == 0x53595141;
                if (!key.flags.contains(LLKHF_INJECTED) || test_key)
                    && [WM_KEYDOWN, WM_SYSKEYDOWN, WM_KEYUP, WM_SYSKEYUP].contains(&message)
                {
                    let event = KeyEvent {
                        key: key.vkCode,
                        down: message == WM_KEYDOWN || message == WM_SYSKEYDOWN,
                        extended: key.flags.contains(LLKHF_EXTENDED),
                    };
                    let swallowed = shortcut_event(state, event);
                    if swallowed {
                        return LRESULT(1);
                    }
                }
            }
        }
        CallNextHookEx(None, code, w, l)
    }

    unsafe extern "system" fn mouse_hook(code: i32, w: WPARAM, l: LPARAM) -> LRESULT {
        if code >= 0
            && [
                WM_LBUTTONDOWN,
                WM_RBUTTONDOWN,
                WM_MBUTTONDOWN,
                WM_XBUTTONDOWN,
            ]
            .contains(&(w.0 as u32))
        {
            if let Some(state) = HOOK_STATE.get() {
                if state.candidates.lock().unwrap().payload.is_some() {
                    let pointer = (*(l.0 as *const MSLLHOOKSTRUCT)).pt;
                    let mut rect = RECT::default();
                    let inside = state.candidate_window.get().is_some_and(|window| {
                        GetWindowRect(hwnd(*window), &mut rect).is_ok()
                            && pointer.x >= rect.left
                            && pointer.x < rect.right
                            && pointer.y >= rect.top
                            && pointer.y < rect.bottom
                    });
                    if !inside {
                        state.cancel_candidates();
                    }
                }
            }
        }
        CallNextHookEx(None, code, w, l)
    }
    /// 低级鼠标钩子会同步调用安装它的线程，即使移动事件直接放行也必须及时泵消息。
    /// 单独等待输入消息，消息到达即唤醒；50ms超时只用于退出检查，不是鼠标处理间隔。
    /// 点击取消候选仍走原有检查，不能为了响应速度去掉外部交互的发送保护。
    fn listen_mouse(state: Arc<NativeState>) {
        unsafe {
            let module = GetModuleHandleW(None).ok().map(|m| HINSTANCE(m.0));
            let Ok(mouse) = SetWindowsHookExW(WH_MOUSE_LL, Some(mouse_hook), module, 0) else {
                state.notify_error("无法安装候选外部点击监听，请检查系统权限。".into());
                return;
            };
            let mut msg = MSG::default();
            while state.running.load(Ordering::Acquire) {
                while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
                    let _ = TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
                MsgWaitForMultipleObjectsEx(None, 50, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            }
            let _ = UnhookWindowsHookEx(mouse);
        }
    }
    pub fn monitor(state: Arc<NativeState>, rx: mpsc::Receiver<Control>) {
        unsafe {
            let _ = HOOK_STATE.set(state.clone());
            let mut last_external = state.target.lock().unwrap().clone().or_else(|| capture());
            let mut was_owned = false;
            let mut msg = MSG::default();
            let _ = PeekMessageW(&mut msg, None, 0, 0, PM_NOREMOVE);
            // 启动时保留未冲突的快捷键，并将持久化的启动设置应用到当前程序路径，
            // 保证重装或更换安装目录后，勾选“开机启动”仍与实际注册一致。
            if let Err(e) = set_startup(state.settings.read().unwrap().startup) {
                state.notify_error(e);
            }
            if let Err(e) = register(&state.settings.read().unwrap(), true) {
                state.notify_error(e);
            }
            let module = GetModuleHandleW(None).ok().map(|m| HINSTANCE(m.0));
            let hook = SetWindowsHookExW(WH_KEYBOARD_LL, Some(keyboard_hook), module, 0).ok();
            let mouse_state = state.clone();
            let mouse = std::thread::spawn(move || listen_mouse(mouse_state));
            if hook.is_none() {
                state.notify_error("无法安装话术快捷键监听，请检查系统权限。".into());
            }
            let mut dock = Dock::default();
            while state.running.load(Ordering::Acquire) {
                while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
                    if msg.message == WM_HOTKEY {
                        if state.management_windows.common_menu.load(Ordering::Acquire) != 0
                            || state
                                .management_windows
                                .search_history_confirm
                                .load(Ordering::Acquire)
                                != 0
                        {
                            continue;
                        }
                        if let Some(external) = capture() {
                            *state.target.lock().unwrap() = Some(external.clone());
                            last_external = Some(external);
                        }
                        match msg.wParam.0 {
                            1 => state.show("search"),
                            2 => state.toggle(),
                            3 => state.show("common"),
                            _ => {}
                        }
                    } else {
                        let _ = TranslateMessage(&msg);
                        DispatchMessageW(&msg);
                    }
                }
                while let Ok(control) = rx.try_recv() {
                    let (next, ack) = match control {
                        Control::Settings(next, ack) => (*next, ack),
                        Control::Show(action) => {
                            if action == "settings" {
                                if let Some(app) = state.app.get() {
                                    let _ = app.emit("ui-action", &action);
                                }
                            } else {
                                if let Some(external) = capture() {
                                    *state.target.lock().unwrap() = Some(external.clone());
                                    last_external = Some(external);
                                }
                                dock.refresh_target(GetForegroundWindow());
                                // 等待已排队的贴边／最小化动作结束，再真正显示并激活。
                                if action != "common" {
                                    state.minimized.store(false, Ordering::Release);
                                    dock.minimize = None;
                                }
                                dock.show_action = Some(action);
                            }
                            continue;
                        }
                        Control::Hide => {
                            state.minimized.store(false, Ordering::Release);
                            dock.minimize = None;
                            dock.show_action = None;
                            *state.display_mode.lock().unwrap() = DisplayMode::Hidden;
                            state.hide_preview(None);
                            state.cancel_candidates();
                            continue;
                        }
                        Control::HideFloat => {
                            state.float_dismissed.store(true, Ordering::Release);
                            continue;
                        }
                        Control::Minimize => {
                            let tray = state.settings.read().unwrap().tray;
                            if !state.minimized.swap(true, Ordering::AcqRel) {
                                state.float_dismissed.store(false, Ordering::Release);
                            }
                            dock.minimize = Some(tray);
                            dock.show_action = None;
                            if tray {
                                *state.display_mode.lock().unwrap() = DisplayMode::Hidden;
                            }
                            state.hide_preview(None);
                            state.cancel_candidates();
                            continue;
                        }
                    };
                    let previous = state.settings.read().unwrap().clone();
                    // 置顶、吸附和面板开关不重注册快捷键；启动时部分按键被占用，
                    // 也不能阻止用户切换这些无关功能。仅修改按键时严格注册并回滚。
                    let hotkeys_changed = next.search_hotkey != previous.search_hotkey
                        || next.window_hotkey != previous.window_hotkey
                        || next.common_hotkey != previous.common_hotkey;
                    if hotkeys_changed {
                        for id in 1..=3 {
                            let _ = UnregisterHotKey(None, id);
                        }
                    }
                    let result: Result<()> = (|| {
                        if hotkeys_changed {
                            register(&next, false)?;
                        }
                        if next.startup != previous.startup {
                            set_startup(next.startup)?;
                        }
                        Ok(())
                    })();
                    match result {
                        Ok(()) => {
                            *state.settings.write().unwrap() = next;
                            if hotkeys_changed {
                                *state.error.lock().unwrap() = None;
                            }
                            let _ = ack.send(Ok(()));
                        }
                        Err(e) => {
                            if hotkeys_changed {
                                for id in 1..=3 {
                                    let _ = UnregisterHotKey(None, id);
                                }
                                let _ = register(&previous, true);
                            }
                            if next.startup != previous.startup {
                                let _ = set_startup(previous.startup);
                            }
                            let _ = ack.send(Err(e));
                        }
                    }
                }
                // 被钩子消费的键不会可靠更新异步键态；必须等实际 KeyUp 才能解除消费。
                let current_context = shortcut_context(&state);
                let submit_down = GetAsyncKeyState(VK_RETURN.0 as i32) < 0;
                let timed = {
                    let mut matcher = MATCHER
                        .get_or_init(|| Mutex::new(ShortcutMatcher::default()))
                        .lock()
                        .unwrap();
                    matcher.release_submit_key(|_| submit_down);
                    let previous =
                        matcher.rebind(state.bindings.read().unwrap().generation, current_context);
                    let mut timed = matcher.tick(current_context, Instant::now());
                    if !previous.replay.is_empty() {
                        timed.replay = previous.replay;
                    }
                    timed
                };
                apply_key_action(&state, timed);
                state.check_candidates();
                let foreground = GetForegroundWindow();
                let owned = pid(foreground) == std::process::id();
                if let Some(app) = state.app.get() {
                    crate::common_menu::poll(app, &state, foreground);
                    crate::search_history_confirm::poll(app, &state);
                }
                if owned && !was_owned {
                    *state.target.lock().unwrap() = last_external.clone();
                }
                if let Some(external) = capture() {
                    last_external = Some(external);
                }
                was_owned = owned;
                if let Some(windows) = state.windows.get() {
                    state
                        .preview
                        .lock()
                        .unwrap()
                        .update(hwnd(windows.2), foreground);
                }
                dock.refresh_target(foreground);
                if dock.edge.ready() && dock.floating.ready() {
                    if let Some(action) = dock.show_action.take() {
                        dock.show(&state, &action);
                    }
                }
                dock.update(&state, foreground);
                if dock.original_rect.is_some() {
                    // 启动即吸附时高度由目标决定；已恢复的逻辑宽度仍沿用，不能和跟随定位争抢高度。
                    state.window_size.lock().unwrap().restore_pending = false;
                }
                state.remember_main_size(false);
                if let Some(windows) = state.windows.get() {
                    dock.floating.update(&state, hwnd(windows.0));
                }
                dock.update_common(&state, foreground);
                std::thread::sleep(Duration::from_millis(30));
            }
            if let Some(hook) = hook {
                let _ = UnhookWindowsHookEx(hook);
            }
            let _ = mouse.join();
            for id in 1..=3 {
                let _ = UnregisterHotKey(None, id);
            }
        }
    }

    /// 企业微信识别仅出现在吸附模块。位置使用物理像素，内容尺寸按各面板自身 DPI 换算。
    /// 目标与面板可分处不同显示器；始终固定贴在外侧，不按工作区回挪或裁小。
    #[derive(Default)]
    struct Dock {
        target: Option<HWND>,
        probe: super::dock_target::DockTargetProbe,
        /// 闪语窗口只能延续刚才正在使用的目标，不能让独立窗口吸附到后台的旧目标。
        target_context: bool,
        original_rect: Option<RECT>,
        width: super::docking::DockWidth,
        foreground: i64,
        /// 底栏独立记录前台切换，在目标所在层内维持目标、侧栏、底栏的顺序。
        common_foreground: i64,
        /// 只为本次进程首次独立显示定位；随后唤起保留用户移动的位置。
        standalone_positioned: Arc<AtomicBool>,
        /// 首次定位与显示在同一个界面线程动作中完成；完成前轮询不得提前显示旧位置。
        standalone_pending: Arc<AtomicBool>,
        /// 独立贴边状态不能复用目标吸附的宽度、原始矩形或手动隐藏模式。
        edge: super::edge_hide::EdgeHide,
        /// 托盘选择在主动最小化时固定；小条开关可在最小化期间独立更新。
        minimize: Option<bool>,
        show_action: Option<String>,
        floating: super::minimized_float::FloatingWindow,
    }
    impl Dock {
        /// 两处面板共用进程及窗口类型结论；同进程图片／视频窗口不能借用后台聊天关系。
        unsafe fn refresh_target(&mut self, foreground: HWND) {
            let extend = self.target_context
                && (foreground.0.is_null() || pid(foreground) == std::process::id());
            self.probe.observe(foreground, extend);
            self.target = self.probe.confirmed().map(hwnd);
        }
        /// 仅跟随可见、未最小化且当前正在使用的目标。编辑闪语时保持目标关系，切换其他程序隐藏。
        unsafe fn visible_target(&self, foreground: HWND) -> Option<HWND> {
            self.target.filter(|target| {
                IsWindow(Some(*target)).as_bool()
                    && IsWindowVisible(*target).as_bool()
                    && !IsIconic(*target).as_bool()
                    // 激活切换期间前台句柄可短暂为空；保留已建立的关系，避免点击侧栏时反向隐藏窗口。
                    && (foreground == *target
                        || (self.target_context
                            && (foreground.0.is_null()
                                || pid(foreground) == std::process::id())))
            })
        }
        /// 手动启动、托盘及快捷键请求允许激活主窗口。先恢复显示模式与几何，再交出焦点。
        unsafe fn show(&mut self, state: &Arc<NativeState>, action: &str) {
            let Some(windows) = state.windows.get() else {
                return;
            };
            let main = hwnd(windows.0);
            let foreground = GetForegroundWindow();
            if action == "common"
                && foreground != main
                && state.settings.read().unwrap().dock_common
                && IsWindowVisible(hwnd(windows.1)).as_bool()
            {
                let _ = SetForegroundWindow(hwnd(windows.1));
                if let Some(app) = state.app.get() {
                    let _ = app.emit_to("common-dock", "ui-action", "common");
                }
                return;
            }
            state.minimized.store(false, Ordering::Release);
            self.minimize = None;
            self.edge.reveal();
            *state.display_mode.lock().unwrap() = if state.settings.read().unwrap().dock
                && self.visible_target(foreground).is_some()
            {
                DisplayMode::Automatic
            } else {
                DisplayMode::Standalone
            };
            if IsIconic(main).as_bool() {
                let _ = ShowWindow(main, SW_RESTORE);
            }
            self.update(state, foreground);
            // 无激活 WebView 的点击应由窗口所属线程完成激活；后台跟随和拖动不进入此路径。
            let native = state.clone();
            let handle = main.0 as i64;
            if let Some(app) = state.app.get() {
                if let Err(error) = app.run_on_main_thread(move || {
                    if native.minimized.load(Ordering::Acquire)
                        || *native.display_mode.lock().unwrap() == DisplayMode::Hidden
                    {
                        return;
                    }
                    let main = hwnd(handle);
                    if !IsWindowVisible(main).as_bool() || IsIconic(main).as_bool() {
                        return;
                    }
                    let _ = ShowWindow(main, SW_RESTORE);
                    if !SetForegroundWindow(main).as_bool() {
                        log::warn!("闪语已显示，但系统暂未允许切换输入焦点。");
                    }
                }) {
                    state.notify_error(format!("闪语已显示，但暂时无法激活窗口：{error}"));
                }
            }
            if let Some(app) = state.app.get() {
                app.state::<Arc<crate::startup::LaunchIntent>>()
                    .mark_shown();
                let _ = app.emit("ui-action", action);
            }
        }
        /// 排除 Windows 阴影与不可见的调整大小边框，以屏幕上实际可见的边缘贴齐。
        unsafe fn frame(target: HWND) -> windows::core::Result<RECT> {
            let mut rect = RECT::default();
            if DwmGetWindowAttribute(
                target,
                DWMWA_EXTENDED_FRAME_BOUNDS,
                std::ptr::from_mut(&mut rect).cast(),
                size_of::<RECT>() as u32,
            )
            .is_err()
            {
                GetWindowRect(target, &mut rect)?;
            }
            Ok(rect)
        }
        /// 独立横条不改变聊天窗口的焦点；即使主窗口没有左右吸附，也可跟随底边。
        /// 输入行固定贴在目标下方；搜索结果和历史向上覆盖目标，其他底部内容不改变锚点。
        unsafe fn update_common(&mut self, state: &NativeState, foreground: HWND) {
            let foreground_changed = self.common_foreground != foreground.0 as i64;
            self.common_foreground = foreground.0 as i64;
            let Some(windows) = state.windows.get() else {
                return;
            };
            let bar = hwnd(windows.1);
            let target = self.visible_target(foreground).filter(|_| {
                let settings = state.settings.read().unwrap();
                // 后台启动仍可自动跟随，只有用户主动隐藏才同时收起底部面板。
                (settings.dock_common || settings.dock_search)
                    && *state.display_mode.lock().unwrap() != DisplayMode::Hidden
            });
            let Some(target) = target else {
                if IsWindowVisible(bar).as_bool() {
                    let _ = ShowWindow(bar, SW_HIDE);
                }
                return;
            };
            let Ok(target_rect) = Self::frame(target) else {
                return;
            };
            let mut current = RECT::default();
            if GetWindowRect(bar, &mut current).is_err() {
                return;
            }
            // 内容高度来自底栏 WebView 的 DIP，必须使用底栏自身 DPI，避免目标 DPI 模式不同而裁短内容。
            // 跨屏后下一轮重新读取 DPI 和边框，沿用异步定位，不重复处理系统的 DPI 通知。
            let (height, above_height) = *state.common_geometry.lock().unwrap();
            let mut desired = super::docking::bottom_bounds(
                &target_rect,
                height,
                above_height,
                GetDpiForWindow(bar),
            );
            let Ok(frame) = Self::frame(bar) else {
                return;
            };
            // 可见原生边框也占高度；前端上报的是客户区内容，不能让底部控件被这一圈边框裁掉。
            let mut client = RECT::default();
            if GetClientRect(bar, &mut client).is_ok() {
                desired.bottom +=
                    ((frame.bottom - frame.top) - (client.bottom - client.top)).max(0);
            }
            let bounds = super::docking::window_bounds_for_frame(&desired, &current, &frame);
            let (x, y, width, height) = (
                bounds.left,
                bounds.top,
                bounds.right - bounds.left,
                bounds.bottom - bounds.top,
            );
            let topmost = GetWindowLongPtrW(target, GWL_EXSTYLE) & WS_EX_TOPMOST.0 as isize != 0;
            let is_topmost = GetWindowLongPtrW(bar, GWL_EXSTYLE) & WS_EX_TOPMOST.0 as isize != 0;
            let main = hwnd(windows.0);
            let expanded = above_height > 0;
            let anchor = if expanded {
                // 插到目标之前而不是全局置顶；跳过底栏自身，避免把自身作为插入锚点。
                let previous = GetWindow(target, GW_HWNDPREV).unwrap_or(HWND_TOP);
                let previous = if previous == bar {
                    GetWindow(bar, GW_HWNDPREV).unwrap_or(HWND_TOP)
                } else {
                    previous
                };
                if !topmost
                    && GetWindowLongPtrW(previous, GWL_EXSTYLE) & WS_EX_TOPMOST.0 as isize != 0
                {
                    HWND_TOP
                } else {
                    previous
                }
            } else if self.original_rect.is_some()
                && IsWindowVisible(main).as_bool()
                && !IsIconic(main).as_bool()
            {
                main
            } else {
                target
            };
            if !IsWindowVisible(bar).as_bool()
                || foreground_changed
                || is_topmost != topmost
                || if expanded {
                    GetWindow(target, GW_HWNDPREV).ok() != Some(bar)
                } else {
                    GetWindow(anchor, GW_HWNDNEXT).ok() != Some(bar)
                }
                || current.left != x
                || current.top != y
                || current.right - current.left != width
                || current.bottom - current.top != height
            {
                // 展开时底栏在目标前，收起后回到原有层级；跟随目标的置顶状态，不抢焦点。
                // 不移动共同所有者，否则 Windows 会把侧栏一起带到目标前方，收起后也无法恢复顺序。
                // 跨线程定位异步提交，避免界面等待设置响应时与后台同步窗口消息互相等待。
                if expanded && is_topmost != topmost {
                    let _ = SetWindowPos(
                        bar,
                        Some(if topmost {
                            HWND_TOPMOST
                        } else {
                            HWND_NOTOPMOST
                        }),
                        0,
                        0,
                        0,
                        0,
                        SWP_NOMOVE
                            | SWP_NOSIZE
                            | SWP_NOACTIVATE
                            | SWP_NOOWNERZORDER
                            | SWP_ASYNCWINDOWPOS,
                    );
                }
                let _ = SetWindowPos(
                    bar,
                    Some(anchor),
                    x,
                    y,
                    width,
                    height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS,
                );
            }
        }
        /// 返回异步恢复使用的矩形及 DPI，首次独立定位不再读回尚未处理的旧侧栏位置。
        unsafe fn restore(&mut self, main: HWND, pinned: bool) -> Result<Option<(RECT, u32)>> {
            if let Some(original) = self.original_rect {
                let mut current = RECT::default();
                GetWindowRect(main, &mut current)
                    .map_err(|e| format!("无法读取吸附窗口的当前尺寸：{e}"))?;
                let dpi = GetDpiForWindow(main).max(96);
                // 跟随目标跨屏后，旧独立坐标和旧 DPI 已不再适用；以当前侧栏所在屏幕为准。
                let monitor = MonitorFromWindow(main, MONITOR_DEFAULTTONEAREST);
                let mut info = MONITORINFO {
                    cbSize: size_of::<MONITORINFO>() as u32,
                    ..Default::default()
                };
                if !GetMonitorInfoW(monitor, &mut info).as_bool() {
                    return Err("无法读取吸附窗口当前屏幕的工作区。".into());
                }
                let same_monitor = MonitorFromRect(&original, MONITOR_DEFAULTTONEAREST) == monitor;
                let rect = super::docking::undocked_bounds(
                    same_monitor.then_some(&original),
                    &current,
                    &info.rcWork,
                );
                SetWindowPos(
                    main,
                    Some(if pinned { HWND_TOPMOST } else { HWND_NOTOPMOST }),
                    rect.left,
                    rect.top,
                    rect.right - rect.left,
                    rect.bottom - rect.top,
                    SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS,
                )
                .map_err(|e| format!("无法恢复闪语独立窗口的位置和尺寸：{e}"))?;
                self.original_rect = None;
                self.width = Default::default();
                return Ok(Some((rect, dpi)));
            }
            Ok(None)
        }
        /// 独立窗口先在前台所在屏幕定位再显示；尺寸随目标 DPI 换算，工作区排除任务栏。
        unsafe fn position_standalone(
            &mut self,
            state: &Arc<NativeState>,
            main: HWND,
            foreground: HWND,
            restored: Option<(RECT, u32)>,
            preserve_size: bool,
        ) -> Result<()> {
            if self.standalone_positioned.load(Ordering::Acquire) {
                return Ok(());
            }
            let monitor = if !foreground.0.is_null() && IsWindow(Some(foreground)).as_bool() {
                MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST)
            } else {
                let mut point = POINT::default();
                GetCursorPos(&mut point).map_err(|e| format!("无法确定当前屏幕：{e}"))?;
                MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST)
            };
            let mut info = MONITORINFO {
                cbSize: size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            if !GetMonitorInfoW(monitor, &mut info).as_bool() {
                return Err("无法读取当前屏幕工作区。".into());
            }
            let mut rect = RECT::default();
            GetWindowRect(main, &mut rect).map_err(|e| format!("无法读取主窗口尺寸：{e}"))?;
            // 恢复定位是异步消息；使用刚提交的最新宽高及其 DPI，避免读回旧矩形后二次缩放。
            let current_dpi = if let Some((bounds, dpi)) = restored {
                rect = bounds;
                dpi
            } else {
                GetDpiForWindow(main).max(96)
            };
            let (mut dpi, mut dpi_y) = (current_dpi, current_dpi);
            let _ = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut dpi, &mut dpi_y);
            let width =
                ((rect.right - rect.left) as f64 * dpi as f64 / current_dpi as f64).round() as i32;
            let height =
                ((rect.bottom - rect.top) as f64 * dpi as f64 / current_dpi as f64).round() as i32;
            let mut bounds = super::docking::standalone_bounds(&info.rcWork, width, height, dpi);
            if restored.is_some() || preserve_size {
                // 首次显示前已经吸附过时，也保留最新尺寸；超出工作区不擅自压缩。
                bounds.right = bounds.left + width;
                bounds.bottom = bounds.top + height;
            }
            let app = state.app.get().ok_or("Windows服务尚未启动。")?;
            let handle = main.0 as i64;
            let pending = self.standalone_pending.clone();
            let positioned = self.standalone_positioned.clone();
            let native = state.clone();
            pending.store(true, Ordering::Release);
            if let Err(error) = app.run_on_main_thread(move || {
                if !native.minimized.load(Ordering::Acquire)
                    && *native.display_mode.lock().unwrap() != DisplayMode::Hidden
                {
                    let main = hwnd(handle);
                    let saved = {
                        let size = native.window_size.lock().unwrap();
                        (size.restore_pending && restored.is_none())
                            .then_some(size.current)
                            .flatten()
                    };
                    let mut bounds = bounds;
                    if let Some(size) = saved {
                        // 在首次显示前刷新隐藏窗口的非客户区，按真实边框恢复尺寸并重新居中。
                        // 否则先用含系统标题栏的高度定位，显示后补差会再跳动30DIP。
                        let _ = SetWindowPos(
                            main,
                            None,
                            0,
                            0,
                            0,
                            0,
                            SWP_FRAMECHANGED
                                | SWP_NOMOVE
                                | SWP_NOSIZE
                                | SWP_NOZORDER
                                | SWP_NOACTIVATE,
                        );
                        let mut outer = RECT::default();
                        let mut client = RECT::default();
                        if GetWindowRect(main, &mut outer).is_ok()
                            && GetClientRect(main, &mut client).is_ok()
                        {
                            let scale = f64::from(dpi) / f64::from(GetDpiForWindow(main).max(96));
                            let width = (size.width * f64::from(dpi) / 96.0
                                + f64::from(outer.right - outer.left - client.right) * scale)
                                .round() as i32;
                            let height = (size.height * f64::from(dpi) / 96.0
                                + f64::from(outer.bottom - outer.top - client.bottom) * scale)
                                .round() as i32;
                            bounds =
                                super::docking::standalone_bounds(&info.rcWork, width, height, dpi);
                            bounds.right = bounds.left + width;
                            bounds.bottom = bounds.top + height;
                        }
                    }
                    // 不在后台先排异步定位再同步显示，否则第一帧可能暴露左侧默认坐标。
                    if let Err(error) = SetWindowPos(
                        main,
                        None,
                        bounds.left,
                        bounds.top,
                        bounds.right - bounds.left,
                        bounds.bottom - bounds.top,
                        SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW,
                    ) {
                        native.notify_error(format!("无法将主窗口定位到屏幕右侧：{error}"));
                    } else {
                        positioned.store(true, Ordering::Release);
                    }
                }
                pending.store(false, Ordering::Release);
            }) {
                self.standalone_pending.store(false, Ordering::Release);
                return Err(format!("无法安排主窗口首次显示：{error}"));
            }
            Ok(())
        }
        unsafe fn update(&mut self, state: &Arc<NativeState>, foreground: HWND) {
            let Some(windows) = state.windows.get() else {
                return;
            };
            let main = hwnd(windows.0);
            if !self.edge.ready()
                || !self.floating.ready()
                || self.standalone_pending.load(Ordering::Acquire)
            {
                return;
            }
            // 最小化必须在裁剪和贴边定位结束后执行，否则待处理的定位会覆盖系统最小化。
            if state.minimized.load(Ordering::Acquire) {
                if self.edge.clear(state, main) {
                    if let Some(tray) = self.minimize.take() {
                        self.floating.minimize(state, main, tray);
                    }
                }
                return;
            }
            let settings = state.settings.read().unwrap().clone();
            // Tauri 修改可调整大小／置顶属性时可能重建扩展样式，在任何再次显示前恢复后台语义。
            if let Err(error) = background_window(main) {
                state.notify_error(error);
                return;
            }
            let target = self.visible_target(foreground);
            let visible = IsWindowVisible(main).as_bool();
            let (show, mode) = {
                let mut mode = state.display_mode.lock().unwrap();
                if !settings.dock && self.original_rect.is_some() && *mode != DisplayMode::Hidden {
                    *mode = DisplayMode::Standalone;
                }
                let target_active = target.is_some_and(|target| foreground == target);
                let show = mode.update(settings.dock, target.is_some(), target_active);
                (show, *mode)
            };
            self.target_context = target.is_some() && mode != DisplayMode::Hidden;
            if !settings.dock || mode == DisplayMode::Standalone {
                MAIN_DOCKED.store(false, Ordering::Release);
                if !show && !self.edge.clear(state, main) {
                    return;
                }
                let restored = match self.restore(main, settings.pinned) {
                    Ok(restored) => restored,
                    Err(error) => {
                        state.notify_error(error);
                        return;
                    }
                };
                if !settings.dock && restored.is_some() {
                    // 主动解除吸附已经选定当前位置；首次独立显示也不能再按其他前台窗口跨屏定位。
                    self.standalone_positioned.store(true, Ordering::Release);
                }
                if show {
                    let preserve_size = state.window_size.lock().unwrap().current.is_some();
                    if let Err(error) =
                        self.position_standalone(state, main, foreground, restored, preserve_size)
                    {
                        state.notify_error(error);
                        return;
                    }
                    if self.standalone_pending.load(Ordering::Acquire) {
                        return;
                    }
                }
                if show && !visible {
                    let _ = ShowWindow(main, SW_SHOWNOACTIVATE);
                } else if !show && visible {
                    let _ = ShowWindow(main, SW_HIDE);
                }
                if show {
                    self.edge.update(state, main, foreground);
                }
                return;
            }
            if !self.edge.clear(state, main) {
                return;
            }
            if !show {
                if visible {
                    let _ = ShowWindow(main, SW_HIDE);
                }
                if self
                    .target
                    .is_some_and(|target| !IsWindow(Some(target)).as_bool())
                {
                    self.target = None;
                }
                return;
            }
            let Some(target) = target else {
                return;
            };
            let Ok(r) = Self::frame(target) else {
                return;
            };
            let mut m = RECT::default();
            if GetWindowRect(main, &mut m).is_err() {
                return;
            }
            // 非托盘最小化保持最小化状态，自动跟随不擅自还原；明确唤起时由 show 恢复。
            if IsIconic(main).as_bool() {
                return;
            }
            // 主窗口若被系统或外部操作最大化，先异步还原且不激活，下一轮再读取正常尺寸。
            // 不能用 SetWindowPos 直接缩小最大化窗口，否则 WebView 仍保留全屏尺寸而显示空白。
            if IsZoomed(main).as_bool() {
                let _ = ShowWindowAsync(main, SW_SHOWNOACTIVATE);
                return;
            }
            if self.original_rect.is_none() {
                self.original_rect = Some(m);
            }
            MAIN_DOCKED.store(true, Ordering::Release);
            let Ok(frame) = Self::frame(main) else {
                return;
            };
            let mut gui = GUITHREADINFO {
                cbSize: size_of::<GUITHREADINFO>() as u32,
                ..Default::default()
            };
            let resizing = GetGUIThreadInfo(GetWindowThreadProcessId(main, None), &mut gui).is_ok()
                && gui.flags.0 & GUI_INMOVESIZE.0 != 0;
            let dpi = GetDpiForWindow(main).max(96);
            self.width
                .observe(m.right - m.left, dpi, settings.free_resize && resizing);
            // 逻辑外框宽度与侧栏自身的界面缩放一致；目标窗口 DPI 可能固定，或目标与侧栏分处不同屏幕。
            // DPI 通知完成后下一轮自动校正，边框补偿和跟随定位不反写用户宽度。
            let width = (self.width.physical(dpi)
                - ((m.right - m.left) - (frame.right - frame.left)))
                .max(1);
            let bounds = super::docking::sidebar_bounds(&r, width, settings.dock_side == "left");
            // 吸附期间层级由目标决定；独立窗口的置顶选择留到 restore 时恢复。
            let topmost = GetWindowLongPtrW(target, GWL_EXSTYLE) & WS_EX_TOPMOST.0 as isize != 0;
            let is_topmost = GetWindowLongPtrW(main, GWL_EXSTYLE) & WS_EX_TOPMOST.0 as isize != 0;
            let foreground_changed = self.foreground != foreground.0 as i64;
            self.foreground = foreground.0 as i64;
            let bounds = super::docking::window_bounds_for_frame(&bounds, &m, &frame);
            let (x, y, width, height) = (
                bounds.left,
                bounds.top,
                bounds.right - bounds.left,
                bounds.bottom - bounds.top,
            );
            if !visible
                || m.left != x
                || m.top != y
                || m.right - m.left != width
                || m.bottom - m.top != height
                || foreground_changed
                || GetWindow(target, GW_HWNDNEXT).ok() != Some(main)
                || is_topmost != topmost
            {
                // 侧栏紧邻目标后方，目标普通／置顶时都跟随同层；不引用任务栏等其他层的窗口。
                // 快速移动目标时，即使几何尚未刷新，目标内容也先于侧栏显示。
                // 不同步等待界面线程处理定位消息，设置命令才能继续在后台队列中响应。
                if let Err(error) = SetWindowPos(
                    main,
                    Some(target),
                    x,
                    y,
                    width,
                    height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS,
                ) {
                    state.notify_error(format!("闪语跟随目标窗口失败：{error}"));
                }
            }
        }
    }

    fn set_startup(enabled: bool) -> Result<()> {
        // 自动验收使用隔离数据目录，同时避免修改用户真实的开机启动项。
        #[cfg(debug_assertions)]
        if std::env::var_os("SHANYU_TEST_DATA_DIR").is_some() {
            return if enabled {
                Err("隔离验收环境不修改系统开机启动。".into())
            } else {
                Ok(())
            };
        }
        unsafe {
            let mut key = HKEY::default();
            let sub = wide("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
            let name = wide("QuickPhrase");
            let legacy_name = wide("Shanyu");
            RegCreateKeyExW(
                HKEY_CURRENT_USER,
                PCWSTR(sub.as_ptr()),
                None,
                PCWSTR::null(),
                REG_OPTION_NON_VOLATILE,
                KEY_SET_VALUE,
                None,
                &mut key,
                None,
            )
            .ok()
            .map_err(|e| format!("开机启动设置无法写入：{e}"))?;
            let result = if enabled {
                let exe = std::env::current_exe().map_err(|e| format!("程序路径无法读取：{e}"))?;
                let text = wide(&format!("\"{}\" --autostart", exe.display()));
                let bytes = std::slice::from_raw_parts(text.as_ptr() as *const u8, text.len() * 2);
                RegSetValueExW(key, PCWSTR(name.as_ptr()), None, REG_SZ, Some(bytes))
                    .ok()
                    .map_err(|e| format!("开机启动设置失败：{e}"))
            } else {
                let code = RegDeleteValueW(key, PCWSTR(name.as_ptr()));
                if code == ERROR_FILE_NOT_FOUND {
                    Ok(())
                } else {
                    code.ok().map_err(|e| format!("开机启动设置无法删除：{e}"))
                }
            };
            // 旧版本使用拼音启动项；只有新入口写入成功后才移除，避免重复开机启动。
            let result = result.and_then(|_| {
                let code = RegDeleteValueW(key, PCWSTR(legacy_name.as_ptr()));
                if code == ERROR_FILE_NOT_FOUND {
                    Ok(())
                } else {
                    code.ok().map_err(|e| format!("旧开机启动项无法删除：{e}"))
                }
            });
            let _ = RegCloseKey(key);
            result
        }
    }

    /// 使用 Windows 文件关联打开文件，定位时交给资源管理器；不经过命令解释器。
    pub fn open_file(path: &str, reveal: bool) -> Result<()> {
        use windows::Win32::UI::Shell::ShellExecuteW;
        let source = Path::new(path);
        let parent = source.parent().ok_or("文件路径无效。")?;
        if (!reveal && !source.is_file()) || (reveal && !parent.is_dir()) {
            return Err("关联文件或所在文件夹已失效，请重新关联。".into());
        }
        let verb = wide("open");
        let program = wide(if reveal { "explorer.exe" } else { path });
        let args = wide(&if source.is_file() {
            format!("/select,\"{path}\"")
        } else {
            format!("\"{}\"", parent.display())
        });
        let result = unsafe {
            ShellExecuteW(
                None,
                PCWSTR(verb.as_ptr()),
                PCWSTR(program.as_ptr()),
                if reveal {
                    PCWSTR(args.as_ptr())
                } else {
                    PCWSTR::null()
                },
                PCWSTR::null(),
                SW_SHOWNORMAL,
            )
        };
        if result.0 as isize <= 32 {
            return Err(format!(
                "文件无法打开，Windows错误码：{}。",
                result.0 as isize
            ));
        }
        Ok(())
    }
    pub fn file_icon(path: &str, dir: &Path) -> Result<String> {
        unsafe {
            use windows::Win32::{Storage::FileSystem::FILE_ATTRIBUTE_NORMAL, UI::Shell::*};
            let extension = Path::new(path)
                .extension()
                .and_then(|s| s.to_str())
                .unwrap_or("file")
                .to_lowercase();
            let safe = extension
                .chars()
                .filter(|c| c.is_ascii_alphanumeric())
                .take(20)
                .collect::<String>();
            let output = dir.join("thumbnails").join(format!("file-{safe}.png"));
            if output.is_file() {
                return Ok(output.to_string_lossy().into());
            }
            // 按文件类型查询关联图标，失效路径也有图标；避开 Shell 不支持的扩展路径前缀。
            let name = wide(&format!("file.{extension}"));
            let mut info = SHFILEINFOW::default();
            if SHGetFileInfoW(
                PCWSTR(name.as_ptr()),
                FILE_ATTRIBUTE_NORMAL,
                Some(&mut info),
                size_of::<SHFILEINFOW>() as u32,
                SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES,
            ) == 0
            {
                return Err("无法读取文件系统图标。".into());
            }
            let dc = CreateCompatibleDC(None);
            let mut bits = std::ptr::null_mut();
            let bitmap_info = BITMAPINFO {
                bmiHeader: BITMAPINFOHEADER {
                    biSize: size_of::<BITMAPINFOHEADER>() as u32,
                    biWidth: 16,
                    biHeight: -16,
                    biPlanes: 1,
                    biBitCount: 32,
                    biCompression: BI_RGB.0,
                    ..Default::default()
                },
                ..Default::default()
            };
            let bitmap =
                CreateDIBSection(Some(dc), &bitmap_info, DIB_RGB_COLORS, &mut bits, None, 0)
                    .map_err(|e| format!("文件图标无法创建：{e}"))?;
            let previous = SelectObject(dc, HGDIOBJ(bitmap.0));
            std::ptr::write_bytes(bits, 0, 16 * 16 * 4);
            let drawn = DrawIconEx(dc, 0, 0, info.hIcon, 16, 16, 0, None, DI_NORMAL).is_ok();
            let mut bytes = std::slice::from_raw_parts(bits as *const u8, 16 * 16 * 4).to_vec();
            for p in bytes.chunks_mut(4) {
                p.swap(0, 2);
                if p[3] == 0 && p[..3].iter().any(|c| *c != 0) {
                    p[3] = 255;
                }
            }
            SelectObject(dc, previous);
            let _ = DeleteObject(HGDIOBJ(bitmap.0));
            let _ = DeleteDC(dc);
            let _ = DestroyIcon(info.hIcon);
            if !drawn {
                return Err("文件图标无法绘制。".into());
            }
            image::RgbaImage::from_raw(16, 16, bytes)
                .ok_or("图标数据无效。")?
                .save(&output)
                .map_err(|e| format!("文件图标缓存无法写入：{e}"))?;
            Ok(output.to_string_lossy().into())
        }
    }
}

#[cfg(not(windows))]
mod platform {
    use super::*;
    pub fn monitor(state: Arc<NativeState>, rx: mpsc::Receiver<Control>) {
        state.notify_error("闪语客户端目前只支持 Windows 10／11 x64。".into());
        while let Ok(control) = rx.recv() {
            match control {
                Control::Settings(_, ack) => {
                    let _ = ack.send(Err("此功能需要 Windows。".into()));
                }
                Control::Show(_) | Control::Hide | Control::Minimize | Control::HideFloat => {}
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn management_windows_suspend_shortcuts_until_the_last_window_closes() {
        let windows = ManagementWindows::default();
        assert!(!windows.is_open());
        for handle in [
            &windows.settings,
            &windows.teams,
            &windows.users,
            &windows.company,
            &windows.common_menu,
            &windows.search_history_confirm,
        ] {
            handle.store(1, Ordering::Release);
            assert!(windows.is_open());
            handle.store(0, Ordering::Release);
        }
        windows.teams.store(1, Ordering::Release);
        windows.users.store(2, Ordering::Release);
        windows.teams.store(0, Ordering::Release);
        assert!(windows.is_open());
        windows.users.store(0, Ordering::Release);
        assert!(!windows.is_open());
    }
    #[test]
    fn settings_allows_edge_hide_but_keeps_shortcuts_suspended() {
        let windows = ManagementWindows::default();
        windows.settings.store(1, Ordering::Release);
        assert!(windows.is_open());
        assert!(!windows.blocks_edge_hide());
        for handle in [
            &windows.teams,
            &windows.users,
            &windows.company,
            &windows.common_menu,
            &windows.search_history_confirm,
        ] {
            handle.store(2, Ordering::Release);
            assert!(windows.blocks_edge_hide());
            handle.store(0, Ordering::Release);
        }
    }
    #[test]
    fn background_start_and_target_lifecycle_follow_without_manual_hide() {
        let mut mode = DisplayMode::default();
        assert!(!mode.update(false, false, false)); // 未开启吸附也不能前台启动。
        assert!(!mode.update(true, false, false)); // 暂无目标。
        assert!(mode.update(true, true, true)); // 目标激活，无需手动唤起。
        assert!(mode.update(true, true, false)); // 正在使用闪语及其编辑窗口。
        assert!(!mode.update(true, false, false)); // 最小化、关闭或切到其他程序。
        assert!(mode.update(true, true, true)); // 恢复目标。
    }
    #[test]
    fn independent_show_waits_for_target_activation_and_manual_hide_is_sticky() {
        let mut mode = DisplayMode::Standalone;
        assert!(mode.update(true, false, false));
        assert!(mode.update(true, true, false)); // 可见但未激活的旧目标不打断独立显示。
        assert_eq!(mode, DisplayMode::Standalone);
        assert!(mode.update(true, true, true));
        assert_eq!(mode, DisplayMode::Automatic);
        assert!(!mode.update(true, false, false));
        mode = DisplayMode::Hidden;
        for dock in [true, false] {
            assert!(!mode.update(dock, true, true));
            assert_eq!(mode, DisplayMode::Hidden);
        }
        mode = DisplayMode::Standalone; // 明确唤起后恢复。
        assert!(mode.update(false, false, false));
    }
    #[test]
    fn shortcuts_require_modifiers_and_normalize_aliases() {
        let s = parse_shortcut("Control+Alt+1").unwrap();
        assert_eq!(s.modifiers, 3);
        assert_eq!(s.key, '1' as u32);
        assert!(parse_shortcut("A").is_err());
        assert!(parse_shortcut("Alt+Alt+1").is_err());
        assert!(parse_shortcut("Ctrl+F25").is_err());
    }
}
