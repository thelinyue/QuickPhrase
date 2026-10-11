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
#[path = "native/docking.rs"]
mod docking;
#[path = "native/preview.rs"]
mod preview;
pub use preview::{PreviewAnchor, PreviewPayload};
/// 自身回放与发送带独立标记，WebView2 的原生回调也能识别，避免把回放重新当作快捷键。
pub(crate) const INTERNAL_INPUT_MARKER: usize = 0x53594E41;

/// 所有客户端窗口统一使用直角，后续创建的编辑器和设置窗口也应用同一策略。
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

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TargetContext {
    pub hwnd: i64,
    pub focus: i64,
    pub pid: u32,
}
/// 启动只进入自动跟随，不能等同于用户主动隐藏。
/// 独立显示只由托盘／快捷键唤起；重新使用目标后回到跟随，手动隐藏则必须再次唤起。
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
/// target 只在外部窗口进入闪语时冻结；切到其他软件不会扩大话术快捷键的默认生效范围。
pub struct NativeState {
    pub target: Mutex<Option<TargetContext>>,
    pub settings: RwLock<Settings>,
    pub bindings: RwLock<Vec<(String, Shortcut)>>,
    /// 按分类、分组与话术顺序缓存活动库，同键只监听一次，匹配后展开全部候选。
    pub shortcut_phrases: RwLock<Vec<Phrase>>,
    pub candidates: Mutex<candidates::CandidateState>,
    /// 只由主窗口同步绘制结果；文件槽位为空，隐藏窗口继续沿用最后快照。
    pub sequence_map: RwLock<(Vec<Option<String>>, bool)>,
    /// 当前正在编辑或录入快捷键的客户端窗口。输入框与模态框中不触发话术，避免抢走录入按键。
    pub shortcut_input: std::sync::atomic::AtomicI64,
    pub search_input: std::sync::atomic::AtomicI64,
    /// 设置窗口存在期间暂停业务快捷键，关闭后使用已成功保存的新配置。
    pub settings_window: std::sync::atomic::AtomicI64,
    /// 横条按内容换行后的逻辑高度，由前端测量；跟随线程转换为目标显示器的物理像素。
    pub common_height: std::sync::atomic::AtomicI32,
    pub error: Mutex<Option<String>>,
    display_mode: Mutex<DisplayMode>,
    pub running: AtomicBool,
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
}
impl NativeState {
    pub fn new(settings: Settings, busy: Arc<AtomicBool>, cancelled: Arc<AtomicBool>) -> Self {
        Self {
            target: Mutex::new(None),
            settings: RwLock::new(settings),
            bindings: RwLock::new(vec![]),
            shortcut_phrases: RwLock::new(vec![]),
            candidates: Mutex::new(candidates::CandidateState::default()),
            sequence_map: RwLock::new((vec![], true)),
            shortcut_input: std::sync::atomic::AtomicI64::new(0),
            search_input: std::sync::atomic::AtomicI64::new(0),
            settings_window: std::sync::atomic::AtomicI64::new(0),
            common_height: std::sync::atomic::AtomicI32::new(34),
            error: Mutex::new(None),
            display_mode: Mutex::new(DisplayMode::default()),
            running: AtomicBool::new(true),
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
            *self.target.lock().unwrap() = unsafe { platform::capture() };
        }
        let (tx, rx) = mpsc::channel();
        *self.control.lock().unwrap() = Some(tx);
        let this = self.clone();
        std::thread::spawn(move || platform::monitor(this, rx));
        Ok(())
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
            if let Err(error) = window.set_always_on_top(settings.pinned) {
                let _ = self.configure_background(&previous);
                return Err(format!("置顶设置失败：{error}"));
            }
            if let Err(error) = window.set_resizable(settings.free_resize) {
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
    pub fn update_bindings(&self, library: &Library) -> Result<()> {
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
                return Err(format!(
                    "话术「{}」的快捷键与导航或序号快捷键冲突。",
                    p.label()
                ));
            }
            let canonical = shortcut.canonical();
            if !bindings.iter().any(|(key, _)| key == &canonical) {
                bindings.push((canonical, shortcut));
            }
        }
        *self.shortcut_phrases.write().unwrap() = phrases;
        *self.bindings.write().unwrap() = bindings;
        Ok(())
    }
    /// 搜索仅放行序号组合键；编辑和模态输入由上下文校验整体暂停。
    fn active_bindings(&self, search: bool) -> Vec<(String, Shortcut)> {
        let settings = self.settings.read().unwrap();
        let mut bindings = if settings.custom_hotkeys && !search {
            self.bindings.read().unwrap().clone()
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
        core::{PCWSTR, PWSTR},
        Win32::{
            Foundation::*,
            Graphics::{
                Dwm::{DwmGetWindowAttribute, DWMWA_EXTENDED_FRAME_BOUNDS},
                Gdi::*,
            },
            System::{LibraryLoader::GetModuleHandleW, Registry::*, Threading::*},
            UI::{
                HiDpi::GetDpiForWindow,
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
    unsafe fn is_enterprise_wechat(window: HWND) -> bool {
        let Ok(process) = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid(window)) else {
            return false;
        };
        let mut text = [0u16; 4096];
        let mut len = text.len() as u32;
        let ok = QueryFullProcessImageNameW(
            process,
            PROCESS_NAME_WIN32,
            PWSTR(text.as_mut_ptr()),
            &mut len,
        )
        .is_ok();
        let _ = CloseHandle(process);
        if !ok {
            return false;
        }
        let path = String::from_utf16_lossy(&text[..len as usize]);
        let Some(name) = Path::new(&path).file_name() else {
            return false;
        };
        let name = name.to_string_lossy();
        // 专用宿主只对隔离 Debug 客户端生效，避免正式客户端把验收窗口当作企业微信一起跟随。
        #[cfg(debug_assertions)]
        if std::env::var_os("SHANYU_TEST_DATA_DIR").is_some()
            && name.eq_ignore_ascii_case("ShanyuDockHost.exe")
        {
            return true;
        }
        name.eq_ignore_ascii_case("WXWork.exe")
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
            || state.settings_window.load(Ordering::Acquire) != 0
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
        if let Some(id) = action.fired {
            state.fire_shortcut(&id);
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
    /// WebView2 的组合键回调也走原生匹配器，搜索输入不依赖全局钩子是否转发客户端按键。
    /// 已被全局钩子消费的键不会进入 WebView；普通键的重复通知仍由匹配器的按住状态处理。
    pub fn install_webview_shortcuts(
        window: &tauri::WebviewWindow,
        state: Arc<NativeState>,
    ) -> Result<()> {
        use webview2_com::{AcceleratorKeyPressedEventHandler, Microsoft::Web::WebView2::Win32::*};
        window
            .with_webview(move |webview| unsafe {
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
        let mut action = MATCHER
            .get_or_init(|| Mutex::new(ShortcutMatcher::default()))
            .lock()
            .unwrap()
            .event(
                event,
                if candidate_active || candidate_action.swallowed {
                    None
                } else {
                    context
                },
                current_modifiers,
                &state.active_bindings(search),
                Instant::now(),
            );
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
            let mouse = SetWindowsHookExW(WH_MOUSE_LL, Some(mouse_hook), module, 0).ok();
            if hook.is_none() {
                state.notify_error("无法安装话术快捷键监听，请检查系统权限。".into());
            }
            if mouse.is_none() {
                state.notify_error("无法安装候选外部点击监听，请检查系统权限。".into());
            }
            let mut dock = Dock::default();
            if let Some(target) = &last_external {
                if is_enterprise_wechat(hwnd(target.hwnd)) {
                    dock.target = Some(hwnd(target.hwnd));
                }
            }
            let mut cached_external = 0i64;
            while state.running.load(Ordering::Acquire) {
                while PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE).as_bool() {
                    if msg.message == WM_HOTKEY {
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
                                    if is_enterprise_wechat(hwnd(external.hwnd)) {
                                        dock.target = Some(hwnd(external.hwnd));
                                    }
                                    *state.target.lock().unwrap() = Some(external.clone());
                                    last_external = Some(external);
                                }
                                dock.show(&state, &action);
                            }
                            continue;
                        }
                        Control::Hide => {
                            *state.display_mode.lock().unwrap() = DisplayMode::Hidden;
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
                let current_context = shortcut_context(&state);
                let submit_down = GetAsyncKeyState(VK_RETURN.0 as i32) < 0;
                let timed = {
                    let mut matcher = MATCHER
                        .get_or_init(|| Mutex::new(ShortcutMatcher::default()))
                        .lock()
                        .unwrap();
                    matcher.release_submit_key(|_| submit_down);
                    matcher.tick(current_context, Instant::now())
                };
                apply_key_action(&state, timed);
                state.check_candidates();
                let foreground = GetForegroundWindow();
                let owned = pid(foreground) == std::process::id();
                if owned && !was_owned {
                    *state.target.lock().unwrap() = last_external.clone();
                }
                if let Some(external) = capture() {
                    if cached_external != external.hwnd {
                        cached_external = external.hwnd;
                        if is_enterprise_wechat(hwnd(external.hwnd)) {
                            dock.target = Some(hwnd(external.hwnd));
                        }
                    }
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
                dock.update(&state, foreground);
                dock.update_common(&state, foreground);
                std::thread::sleep(Duration::from_millis(30));
            }
            if let Some(hook) = hook {
                let _ = UnhookWindowsHookEx(hook);
            }
            if let Some(mouse) = mouse {
                let _ = UnhookWindowsHookEx(mouse);
            }
            for id in 1..=3 {
                let _ = UnregisterHotKey(None, id);
            }
        }
    }

    /// 企业微信识别仅出现在吸附模块。使用物理像素和目标 DPI 固定贴在外侧，不按工作区回挪或裁小。
    #[derive(Default)]
    struct Dock {
        target: Option<HWND>,
        /// 闪语窗口只能延续刚才正在使用的目标，不能让独立窗口吸附到后台的旧目标。
        target_context: bool,
        original_rect: Option<RECT>,
        width: super::docking::DockWidth,
        foreground: i64,
        /// 底栏独立记录前台切换，在目标所在层内维持目标、侧栏、底栏的顺序。
        common_foreground: i64,
    }
    impl Dock {
        /// 仅跟随可见、未最小化且当前正在使用的目标。编辑闪语时保持目标关系，切换其他程序隐藏。
        unsafe fn visible_target(&self, foreground: HWND) -> Option<HWND> {
            self.target.filter(|target| {
                IsWindow(Some(*target)).as_bool()
                    && IsWindowVisible(*target).as_bool()
                    && !IsIconic(*target).as_bool()
                    // 激活切换期间前台句柄可短暂为空；保留已建立的关系，避免点击侧栏时反向隐藏窗口。
                    && (pid(foreground) == pid(*target)
                        || (self.target_context
                            && (foreground.0.is_null()
                                || pid(foreground) == std::process::id())))
            })
        }
        /// 只有明确的托盘／快捷键请求允许激活主窗口。先选择显示模式并恢复几何，再交出焦点。
        unsafe fn show(&mut self, state: &NativeState, action: &str) {
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
            if !SetForegroundWindow(main).as_bool() {
                log::warn!("闪语已显示，但系统暂未允许切换输入焦点。");
            }
            if let Some(app) = state.app.get() {
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
        /// 始终贴在目标下方且与目标等宽，超出屏幕时不回挪到聊天区域，也不压缩内容。
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
            let height =
                (state.common_height.load(Ordering::Acquire) * GetDpiForWindow(target) as i32 / 96)
                    .max(1);
            let y = target_rect.bottom;
            let Ok(frame) = Self::frame(bar) else {
                return;
            };
            let bounds = super::docking::window_bounds_for_frame(
                &RECT {
                    left: target_rect.left,
                    top: y,
                    right: target_rect.right,
                    bottom: y + height,
                },
                &current,
                &frame,
            );
            let (x, y, width, height) = (
                bounds.left,
                bounds.top,
                bounds.right - bounds.left,
                bounds.bottom - bounds.top,
            );
            let topmost = GetWindowLongPtrW(target, GWL_EXSTYLE) & WS_EX_TOPMOST.0 as isize != 0;
            let is_topmost = GetWindowLongPtrW(bar, GWL_EXSTYLE) & WS_EX_TOPMOST.0 as isize != 0;
            let main = hwnd(windows.0);
            let anchor = if self.original_rect.is_some()
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
                || GetWindow(anchor, GW_HWNDNEXT).ok() != Some(bar)
                || current.left != x
                || current.top != y
                || current.right - current.left != width
                || current.bottom - current.top != height
            {
                // 点击也会改变层内顺序。底栏紧邻侧栏（或仅显示底栏时的目标）后方，
                // 目标先显示，跟随刷新短暂滞后时也不会遮住聊天内容；任务栏仍可正常遮挡。
                // 跨线程定位异步提交，避免界面等待设置响应时与后台同步窗口消息互相等待。
                let _ = SetWindowPos(
                    bar,
                    Some(anchor),
                    x,
                    y,
                    width,
                    height,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS,
                );
            }
        }
        unsafe fn restore(&mut self, state: &NativeState, main: HWND, pinned: bool) -> bool {
            if let Some(rect) = self.original_rect {
                if let Err(error) = SetWindowPos(
                    main,
                    Some(if pinned { HWND_TOPMOST } else { HWND_NOTOPMOST }),
                    rect.left,
                    rect.top,
                    rect.right - rect.left,
                    rect.bottom - rect.top,
                    SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS,
                ) {
                    state.notify_error(format!("无法恢复闪语独立窗口的位置和尺寸：{error}"));
                    return false;
                }
                self.original_rect = None;
                self.width = Default::default();
            }
            true
        }
        unsafe fn update(&mut self, state: &NativeState, foreground: HWND) {
            let Some(windows) = state.windows.get() else {
                return;
            };
            let main = hwnd(windows.0);
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
                let target_active = target.is_some_and(|target| pid(foreground) == pid(target));
                let show = mode.update(settings.dock, target.is_some(), target_active);
                (show, *mode)
            };
            self.target_context = target.is_some() && mode != DisplayMode::Hidden;
            if !settings.dock || mode == DisplayMode::Standalone {
                if !self.restore(state, main, settings.pinned) {
                    return;
                }
                if show && !visible {
                    let _ = ShowWindow(main, SW_SHOWNOACTIVATE);
                } else if !show && visible {
                    let _ = ShowWindow(main, SW_HIDE);
                }
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
            if self.original_rect.is_none() {
                self.original_rect = Some(m);
            }
            let Ok(frame) = Self::frame(main) else {
                return;
            };
            let mut gui = GUITHREADINFO {
                cbSize: size_of::<GUITHREADINFO>() as u32,
                ..Default::default()
            };
            let resizing = GetGUIThreadInfo(GetWindowThreadProcessId(main, None), &mut gui).is_ok()
                && gui.flags.0 & GUI_INMOVESIZE.0 != 0;
            self.width.observe(
                m.right - m.left,
                GetDpiForWindow(main),
                settings.free_resize && resizing,
            );
            // 逻辑外框宽度按目标 DPI 换算；边框补偿不反写宽度，连续移动不会越变越窄或越变越宽。
            let width = (self.width.physical(GetDpiForWindow(target))
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
                    SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_ASYNCWINDOWPOS,
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
            let name = wide("Shanyu");
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
                Control::Show(_) | Control::Hide => {}
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
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
