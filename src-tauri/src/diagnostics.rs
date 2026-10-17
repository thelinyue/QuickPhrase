//! 诊断只接收枚举和数值，不接收正文、按键码、窗口标题、路径或网络错误原文。
//! 钩子通过有界队列 try_send；落盘、序列化和压缩都在其他线程执行。
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
    fs,
    io::Write,
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, AtomicU64, AtomicU8, Ordering},
        mpsc, Arc, Mutex, OnceLock,
    },
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
use tauri::Emitter;

const BASIC_LIMIT: usize = 256;
const DETAIL_LIMIT: usize = 4096;
const DURATION_MS: u64 = 300_000;
const FILE_LIMIT: u64 = 2 * 1024 * 1024;
static RECORDER: OnceLock<Recorder> = OnceLock::new();

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum Category {
    Input,
    Network,
    Window,
    Performance,
}
impl Category {
    fn bit(self) -> u8 {
        1 << self as u8
    }
}

/// 字段含义保持明确，导出时无需靠正则猜测哪些文本含隐私。
/// IME 组字无法从跨进程钩子可靠获知，因此不伪造组字状态。
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
#[serde(tag = "event", rename_all = "snake_case", deny_unknown_fields)]
pub enum Event {
    Startup {},
    Bindings {
        count: usize,
        member_phrases: usize,
        member: bool,
        generation: u64,
        custom: bool,
        global: bool,
    },
    Authorization {
        signed_in: bool,
        offline: bool,
        teams: usize,
    },
    Hook {
        installed: bool,
    },
    Session {
        mask: u8,
        started: bool,
    },
    Key {
        role: u8,
        down: bool,
        local_blocked: bool,
        chain_blocked: bool,
        context: bool,
    },
    Match {
        fired: bool,
        replay: usize,
        cancelled: bool,
        candidate: bool,
    },
    Context {
        allowed: bool,
        target_matches: bool,
        management: bool,
        busy: bool,
        focus_changed: bool,
    },
    Candidate {
        action: u8,
        count: usize,
    },
    Layout {
        window: u8,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
        dpi: u32,
    },
    Dock {
        enabled: bool,
        target: bool,
        attached: bool,
        target_changed: bool,
    },
    Delivery {
        stage: u8,
        ok: bool,
        blocks: usize,
        segments: usize,
        elapsed_ms: u64,
    },
    Network {
        request: u64,
        operation: NetworkOperation,
        stage: u8,
        status: u16,
        failure: u8,
        elapsed_ms: u64,
    },
    Ipc {
        operation: FrontendOperation,
        ok: bool,
        elapsed_ms: u64,
    },
    Heartbeat {
        elapsed_ms: u64,
    },
}
impl Event {
    fn category(&self) -> Option<Category> {
        match self {
            Self::Startup { .. }
            | Self::Bindings { .. }
            | Self::Authorization { .. }
            | Self::Hook { .. }
            | Self::Session { .. } => None,
            Self::Key { .. }
            | Self::Match { .. }
            | Self::Context { .. }
            | Self::Candidate { .. }
            | Self::Delivery { .. } => Some(Category::Input),
            Self::Layout { .. } | Self::Dock { .. } => Some(Category::Window),
            Self::Network { .. } => Some(Category::Network),
            Self::Ipc { .. } | Self::Heartbeat { .. } => Some(Category::Performance),
        }
    }
}

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum NetworkOperation {
    Login,
    Request,
    Authorization,
    PrivateSync,
    Member,
    Shared,
    Download,
    Upload,
}
impl NetworkOperation {
    pub fn route(path: &str) -> Self {
        if path == "me" {
            Self::Authorization
        } else if path.starts_with("me/private/") {
            Self::PrivateSync
        } else if path.starts_with("me/phrase-") || path.starts_with("phrase-grants/") {
            Self::Member
        } else if path.starts_with("teams/") {
            Self::Shared
        } else {
            Self::Request
        }
    }
}
#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum FrontendOperation {
    GetLibrary,
    SearchContent,
    GetMemberView,
    GetTeamView,
    SaveSettings,
    SavePhrase,
    Heartbeat,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Record {
    time_ms: u64,
    process: u64,
    event: Event,
}
#[derive(Clone, Default, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Buffer {
    basic: VecDeque<Record>,
    detail: VecDeque<Record>,
    overwritten: u64,
    dropped: u64,
}
impl Buffer {
    fn push(&mut self, record: Record) {
        if matches!(
            record.event,
            Event::Authorization { .. } | Event::Bindings { .. }
        ) && self
            .basic
            .iter()
            .rev()
            .find(|previous| {
                previous.process == record.process
                    && std::mem::discriminant(&previous.event)
                        == std::mem::discriminant(&record.event)
            })
            .is_some_and(|previous| previous.event == record.event)
        {
            return;
        }
        let (queue, limit) = if record.event.category().is_none() {
            (&mut self.basic, BASIC_LIMIT)
        } else {
            (&mut self.detail, DETAIL_LIMIT)
        };
        if queue.len() >= limit {
            queue.pop_front();
            self.overwritten += 1;
        }
        queue.push_back(record);
    }
    fn records(&self) -> Vec<Record> {
        let mut records: Vec<_> = self
            .basic
            .iter()
            .chain(self.detail.iter())
            .cloned()
            .collect();
        records.sort_by_key(|r| r.time_ms);
        records
    }
}
enum Message {
    Event(Record),
    Flush(mpsc::SyncSender<()>),
    Clear(mpsc::SyncSender<()>),
}
struct Shared {
    buffer: Mutex<Buffer>,
    disk_failed: AtomicBool,
}
struct Recorder {
    tx: mpsc::SyncSender<Message>,
    shared: Arc<Shared>,
    mask: AtomicU8,
    deadline: AtomicU64,
    dropped: Arc<AtomicU64>,
    start: Instant,
    process: u64,
    directory: PathBuf,
}
fn wall_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}

/// 只读取系统版本的数值白名单；不运行系统信息命令，也不收集机器名和用户目录。
#[cfg(windows)]
fn windows_build() -> Option<u32> {
    use windows::{
        core::w,
        Win32::System::Registry::{RegGetValueW, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ},
    };
    let mut text = [0u16; 32];
    let mut bytes = std::mem::size_of_val(&text) as u32;
    unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            w!("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"),
            w!("CurrentBuildNumber"),
            RRF_RT_REG_SZ,
            None,
            Some(text.as_mut_ptr().cast()),
            Some(&mut bytes),
        )
        .ok()
        .ok()?;
    }
    let length = text.iter().position(|c| *c == 0).unwrap_or(text.len());
    String::from_utf16(&text[..length]).ok()?.parse().ok()
}
#[cfg(not(windows))]
fn windows_build() -> Option<u32> {
    None
}
impl Recorder {
    fn remaining(&self) -> u64 {
        self.deadline
            .load(Ordering::Acquire)
            .saturating_sub(self.start.elapsed().as_millis() as u64)
    }
    fn accepts(&self, event: &Event) -> bool {
        event.category().is_none_or(|c| {
            self.remaining() > 0 && self.mask.load(Ordering::Acquire) & c.bit() != 0
        })
    }
    fn record(&self, event: Event) {
        if !self.accepts(&event) {
            return;
        }
        if self
            .tx
            .try_send(Message::Event(Record {
                time_ms: wall_ms(),
                process: self.process,
                event,
            }))
            .is_err()
        {
            self.dropped.fetch_add(1, Ordering::Relaxed);
        }
    }
    fn barrier(&self, clear: bool) -> Result<(), String> {
        let (tx, rx) = mpsc::sync_channel(1);
        self.tx
            .send(if clear {
                Message::Clear(tx)
            } else {
                Message::Flush(tx)
            })
            .map_err(|_| "诊断记录服务已停止。")?;
        rx.recv_timeout(Duration::from_secs(5))
            .map_err(|_| "诊断记录整理超时，请稍后重试。".to_owned())
    }
}
pub fn record(event: Event) {
    if let Some(r) = RECORDER.get() {
        r.record(event);
    }
}
pub fn enabled(category: Category) -> bool {
    RECORDER
        .get()
        .is_some_and(|r| r.remaining() > 0 && r.mask.load(Ordering::Acquire) & category.bit() != 0)
}

fn persist(path: &Path, buffer: &Buffer) -> Result<(), String> {
    let mut temp = tempfile::NamedTempFile::new_in(path.parent().ok_or("诊断目录无效。")?)
        .map_err(|_| "无法创建诊断文件。")?;
    serde_json::to_writer(temp.as_file_mut(), buffer).map_err(|_| "诊断记录无法写入。")?;
    temp.as_file_mut()
        .flush()
        .map_err(|_| "诊断记录无法写入。")?;
    temp.persist(path).map_err(|_| "诊断记录无法保存。")?;
    Ok(())
}
fn read_buffer(path: &Path) -> Buffer {
    if fs::metadata(path).is_ok_and(|m| m.len() <= FILE_LIMIT) {
        if let Ok(buffer) = fs::read(path)
            .ok()
            .and_then(|bytes| serde_json::from_slice::<Buffer>(&bytes).ok())
            .ok_or(())
        {
            if buffer.basic.len() <= BASIC_LIMIT && buffer.detail.len() <= DETAIL_LIMIT {
                return buffer;
            }
        }
    }
    Buffer::default()
}

/// 日常摘要与临时详细记录分别限额，详细按键不会挤掉启动恢复证据。
/// 每两秒保存安全快照；异常退出最多丢失最后一小段，重启不自动恢复详细采集。
pub fn initialize(root: &Path) -> Result<(), String> {
    let directory = root.join("diagnostics");
    fs::create_dir_all(&directory).map_err(|_| "无法创建本地诊断目录。")?;
    let path = directory.join("records.json");
    let shared = Arc::new(Shared {
        buffer: Mutex::new(read_buffer(&path)),
        disk_failed: AtomicBool::new(false),
    });
    let dropped = Arc::new(AtomicU64::new(shared.buffer.lock().unwrap().dropped));
    let (tx, rx) = mpsc::sync_channel(512);
    let r = Recorder {
        tx,
        shared: shared.clone(),
        mask: AtomicU8::new(0),
        deadline: AtomicU64::new(0),
        dropped: dropped.clone(),
        start: Instant::now(),
        process: wall_ms(),
        directory: directory.clone(),
    };
    std::thread::Builder::new()
        .name("diagnostics".into())
        .spawn(move || {
            let mut dirty = false;
            let mut last = Instant::now();
            loop {
                let message = rx.recv_timeout(Duration::from_millis(250));
                let mut ack = None;
                match message {
                    Ok(Message::Event(record)) => {
                        shared.buffer.lock().unwrap().push(record);
                        dirty = true;
                    }
                    Ok(Message::Clear(tx)) => {
                        *shared.buffer.lock().unwrap() = Buffer::default();
                        dropped.store(0, Ordering::Relaxed);
                        dirty = true;
                        ack = Some(tx);
                    }
                    Ok(Message::Flush(tx)) => ack = Some(tx),
                    Err(mpsc::RecvTimeoutError::Disconnected) => break,
                    Err(mpsc::RecvTimeoutError::Timeout) => {}
                }
                if let Some(r) = RECORDER.get() {
                    let deadline = r.deadline.load(Ordering::Acquire);
                    if deadline > 0
                        && r.remaining() == 0
                        && r.deadline
                            .compare_exchange(deadline, 0, Ordering::AcqRel, Ordering::Acquire)
                            .is_ok()
                    {
                        shared.buffer.lock().unwrap().push(Record {
                            time_ms: wall_ms(),
                            process: r.process,
                            event: Event::Session {
                                mask: r.mask.load(Ordering::Acquire),
                                started: false,
                            },
                        });
                        dirty = true;
                    }
                }
                {
                    let mut buffer = shared.buffer.lock().unwrap();
                    let count = dropped.load(Ordering::Relaxed);
                    if buffer.dropped != count {
                        buffer.dropped = count;
                        dirty = true;
                    }
                }
                if dirty && (last.elapsed() >= Duration::from_secs(2) || ack.is_some()) {
                    let snapshot = shared.buffer.lock().unwrap().clone();
                    let failed = persist(&path, &snapshot).is_err();
                    shared.disk_failed.store(failed, Ordering::Release);
                    dirty = failed;
                    last = Instant::now();
                }
                if let Some(tx) = ack {
                    let _ = tx.send(());
                }
            }
        })
        .map_err(|_| "无法启动诊断记录线程。")?;
    RECORDER.set(r).map_err(|_| "诊断记录已启动。")?;
    // 仅保留 Rust panic 时间，不读取 payload、源码路径或内存；原 panic 处理继续执行。
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        let _ = fs::write(directory.join("panic.txt"), wall_ms().to_string());
        previous(info);
    }));
    record(Event::Startup {});
    Ok(())
}

/// 正常退出尽力落盘，但磁盘/队列故障不能无限阻止用户退出程序。
pub fn flush_on_exit() {
    if let Some(r) = RECORDER.get() {
        r.deadline.store(0, Ordering::Release);
        let (tx, rx) = mpsc::sync_channel(1);
        if r.tx.try_send(Message::Flush(tx)).is_ok() {
            let _ = rx.recv_timeout(Duration::from_secs(1));
        }
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    running: bool,
    remaining_seconds: u64,
    categories: Vec<Category>,
    records: usize,
    dropped: u64,
    overwritten: u64,
    disk_failed: bool,
    panic_recorded: bool,
}
fn recorder() -> Result<&'static Recorder, String> {
    RECORDER
        .get()
        .ok_or_else(|| "诊断服务未就绪，请重启闪语后重试。".into())
}
#[tauri::command]
pub fn diagnostics_status() -> Result<Status, String> {
    let r = recorder()?;
    let remaining = r.remaining();
    let buffer = r.shared.buffer.lock().map_err(|_| "诊断状态无法读取。")?;
    Ok(Status {
        running: remaining > 0,
        remaining_seconds: remaining.div_ceil(1000),
        categories: [
            Category::Input,
            Category::Network,
            Category::Window,
            Category::Performance,
        ]
        .into_iter()
        .filter(|c| r.mask.load(Ordering::Acquire) & c.bit() != 0)
        .collect(),
        records: buffer.basic.len() + buffer.detail.len(),
        dropped: r.dropped.load(Ordering::Relaxed),
        overwritten: buffer.overwritten,
        disk_failed: r.shared.disk_failed.load(Ordering::Acquire),
        panic_recorded: r.directory.join("panic.txt").exists(),
    })
}
#[tauri::command]
pub fn diagnostics_start(
    app: tauri::AppHandle,
    categories: Vec<Category>,
) -> Result<Status, String> {
    let r = recorder()?;
    let mask = categories.iter().fold(0, |mask, c| mask | c.bit());
    if mask == 0 {
        return Err("请至少选择一种问题。".into());
    }
    if r.remaining() > 0 {
        return Err("正在记录，请先停止本次记录。".into());
    }
    r.mask.store(mask, Ordering::Release);
    r.deadline.store(
        r.start.elapsed().as_millis() as u64 + DURATION_MS,
        Ordering::Release,
    );
    r.record(Event::Session {
        mask,
        started: true,
    });
    let status = diagnostics_status()?;
    let _ = app.emit("diagnostics-changed", &status);
    Ok(status)
}
#[tauri::command]
pub fn diagnostics_stop(app: tauri::AppHandle) -> Result<Status, String> {
    let r = recorder()?;
    r.deadline.store(0, Ordering::Release);
    r.record(Event::Session {
        mask: r.mask.load(Ordering::Acquire),
        started: false,
    });
    let status = diagnostics_status()?;
    let _ = app.emit("diagnostics-changed", &status);
    Ok(status)
}
#[tauri::command]
pub async fn diagnostics_clear() -> Result<Status, String> {
    tauri::async_runtime::spawn_blocking(|| {
        let r = recorder()?;
        if r.remaining() > 0 {
            return Err("请先停止记录，再清除本地诊断。".into());
        }
        r.barrier(true)?;
        let panic = r.directory.join("panic.txt");
        if panic.exists() {
            fs::remove_file(panic).map_err(|_| "崩溃标记无法清除。")?;
        }
        if r.shared.disk_failed.load(Ordering::Acquire) {
            return Err("本地诊断文件未能清除，请检查目录权限。".into());
        }
        diagnostics_status()
    })
    .await
    .map_err(|_| "清除诊断任务未完成。")?
}

const EXPLANATION: &str = "闪语诊断包\n仅包含枚举与数值：启动/绑定数量、授权摘要、所选问题的状态与耗时、窗口位置/DPI。\n不包含原始按键码、快捷键字母、话术/聊天/剪贴板正文、账号、令牌、文件路径、窗口标题、截图或数据库。不自动上传。\n详细记录5分钟自动停止；日常摘要256条、详细记录4096条、队列512条；覆盖/丢弃计数见 manifest。每2秒保存，异常退出可能丢失末尾记录。\nkey.role: 1绑定首键，2绑定后续键，3候选操作键；不保存实际按键。chain_blocked是钩子链返回，不能证明目标程序收到按键。\ncandidate.action: 1打开，2取消，3重新测量。context仅反映焦点与业务保护；输入法组字状态未知，不强制查询/提交组字。\ndelivery.stage: 0开始，1准备，2目标复核，3粘贴提交，4等待，5发送键提交，6清理；ok只表示系统操作结果，不能证明目标应用最终收到/发送。\nnetwork.stage: 0开始，1响应头，2完成，3未完成(失败/取消)；不收集URL和错误正文。\nperformance仅覆盖指定IPC与WebView心跳，不是完整性能分析。\n崩溃仅提供Rust panic时间标记，不覆盖系统异常/强杀，不包含内存转储。\n反馈时请另附：现象、复现时间、操作步骤、预期结果、输入法版本/新旧版。截图请自行确认无敏感信息。\n";
fn export(path: &Path, buffer: &Buffer, manifest: &serde_json::Value) -> Result<(), String> {
    if !path
        .extension()
        .is_some_and(|ext| ext.eq_ignore_ascii_case("zip"))
    {
        return Err("诊断包请保存为 .zip 文件。".into());
    }
    let parent = path
        .parent()
        .filter(|p| p.is_dir())
        .ok_or("请选择有效的诊断包保存位置。")?;
    let mut temp = tempfile::NamedTempFile::new_in(parent).map_err(|_| "无法创建诊断包。")?;
    {
        let mut zip = zip::ZipWriter::new(temp.as_file_mut());
        let options = zip::write::SimpleFileOptions::default()
            .compression_method(zip::CompressionMethod::Deflated);
        let work = (|| -> std::result::Result<(), Box<dyn std::error::Error>> {
            zip.start_file("说明.txt", options)?;
            zip.write_all(EXPLANATION.as_bytes())?;
            zip.start_file("manifest.json", options)?;
            serde_json::to_writer_pretty(&mut zip, manifest)?;
            zip.start_file("events.jsonl", options)?;
            for event in buffer.records() {
                serde_json::to_writer(&mut zip, &event)?;
                zip.write_all(b"\n")?;
            }
            zip.finish()?;
            Ok(())
        })();
        work.map_err(|_| "诊断包写入失败，记录仍保留，请更换保存位置重试。")?;
    }
    temp.persist(path)
        .map_err(|_| "诊断包保存失败，记录仍保留。")?;
    Ok(())
}
#[tauri::command]
pub async fn diagnostics_export(path: String) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || {
        let r = recorder()?;
        if r.remaining() > 0 { return Err("请先停止记录，再导出诊断包。".into()); }
        r.barrier(false)?;
        let panic_time = fs::read_to_string(r.directory.join("panic.txt")).ok().and_then(|s| s.parse::<u64>().ok());
        let status = diagnostics_status()?;
        let manifest = serde_json::json!({ "version": env!("CARGO_PKG_VERSION"), "os": std::env::consts::OS, "windowsBuild": windows_build(), "arch": std::env::consts::ARCH, "exportedAt": wall_ms(), "status": status, "rustPanicAt": panic_time, "imeComposition": "unknown", "targetReception": "unverified" });
        let buffer = r.shared.buffer.lock().map_err(|_| "诊断记录无法读取。")?.clone();
        export(Path::new(&path), &buffer, &manifest)
    }).await.map_err(|_| "导出诊断任务未完成，记录仍保留。")?
}
#[tauri::command]
pub fn diagnostics_frontend(operation: FrontendOperation, ok: bool, elapsed_ms: u64) {
    if elapsed_ms <= 86_400_000 {
        record(Event::Ipc {
            operation,
            ok,
            elapsed_ms,
        });
    }
}

/// 请求从进入到退出均可配对；提前返回和 future 被取消也留下结束标记，不记录错误正文。
pub struct NetworkSpan {
    request: u64,
    operation: NetworkOperation,
    start: Instant,
    complete: bool,
    failure: u8,
}
impl NetworkSpan {
    pub fn new(operation: NetworkOperation) -> Self {
        static NEXT: AtomicU64 = AtomicU64::new(1);
        let request = NEXT.fetch_add(1, Ordering::Relaxed);
        record(Event::Network {
            request,
            operation,
            stage: 0,
            status: 0,
            failure: 0,
            elapsed_ms: 0,
        });
        Self {
            request,
            operation,
            start: Instant::now(),
            complete: false,
            failure: 0,
        }
    }
    pub fn response(&self, status: u16) {
        record(Event::Network {
            request: self.request,
            operation: self.operation,
            stage: 1,
            status,
            failure: 0,
            elapsed_ms: self.start.elapsed().as_millis() as u64,
        });
    }
    pub fn complete(&mut self) {
        self.complete = true;
    }
    pub fn transport_error(&mut self, error: &reqwest::Error) {
        self.failure = if error.is_timeout() {
            1
        } else if error.is_connect() {
            2
        } else if error.is_decode() {
            3
        } else {
            4
        };
    }
}
impl Drop for NetworkSpan {
    fn drop(&mut self) {
        record(Event::Network {
            request: self.request,
            operation: self.operation,
            stage: if self.complete { 2 } else { 3 },
            status: 0,
            failure: self.failure,
            elapsed_ms: self.start.elapsed().as_millis() as u64,
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn item(event: Event, time_ms: u64) -> Record {
        Record {
            time_ms,
            process: 1,
            event,
        }
    }
    fn collector() -> (Recorder, mpsc::Receiver<Message>) {
        let (tx, rx) = mpsc::sync_channel(2);
        (
            Recorder {
                tx,
                shared: Arc::new(Shared {
                    buffer: Mutex::new(Buffer::default()),
                    disk_failed: AtomicBool::new(false),
                }),
                mask: AtomicU8::new(0),
                deadline: AtomicU64::new(0),
                dropped: Arc::new(AtomicU64::new(0)),
                start: Instant::now(),
                process: 1,
                directory: PathBuf::new(),
            },
            rx,
        )
    }
    #[test]
    fn default_off_selected_only_and_expiry_without_ui() {
        let (r, rx) = collector();
        let key = Event::Key {
            role: 1,
            down: true,
            local_blocked: false,
            chain_blocked: false,
            context: true,
        };
        r.record(key.clone());
        assert!(rx.try_recv().is_err());
        r.record(Event::Startup {});
        assert!(rx.try_recv().is_ok());
        r.mask.store(Category::Input.bit(), Ordering::Release);
        r.deadline.store(DURATION_MS, Ordering::Release);
        r.record(key.clone());
        assert!(rx.try_recv().is_ok());
        r.record(Event::Heartbeat { elapsed_ms: 2000 });
        assert!(rx.try_recv().is_err());
        // 使用时钟起点模拟经过五分钟，不依赖状态页轮询或真实等待。
        let r = Recorder {
            start: Instant::now() - Duration::from_millis(DURATION_MS + 1),
            ..r
        };
        r.record(key);
        assert!(rx.try_recv().is_err());
        assert_eq!(r.remaining(), 0);
    }
    #[test]
    fn full_queue_never_waits_for_consumer() {
        let (r, _rx) = collector();
        for _ in 0..100 {
            r.record(Event::Startup {});
        }
        assert_eq!(r.dropped.load(Ordering::Relaxed), 98);
    }
    #[test]
    fn detail_cannot_evict_startup_and_authorization_deduplicates() {
        let mut buffer = Buffer::default();
        buffer.push(item(Event::Startup {}, 0));
        for i in 0..DETAIL_LIMIT + 10 {
            buffer.push(item(Event::Heartbeat { elapsed_ms: 2000 }, i as u64 + 1));
        }
        assert_eq!(buffer.detail.len(), DETAIL_LIMIT);
        assert_eq!(buffer.basic.len(), 1);
        assert_eq!(buffer.overwritten, 10);
        for i in 0..300 {
            buffer.push(item(
                Event::Authorization {
                    signed_in: true,
                    offline: false,
                    teams: 2,
                },
                i,
            ));
        }
        assert_eq!(buffer.basic.len(), 2);
        for i in 0..BASIC_LIMIT {
            buffer.push(item(Event::Startup {}, i as u64));
        }
        assert_eq!(buffer.basic.len(), BASIC_LIMIT);
    }
    #[test]
    fn restart_restores_safe_records_and_rejects_unknown_content() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("records.json");
        let mut buffer = Buffer::default();
        buffer.push(item(Event::Startup {}, 1));
        persist(&path, &buffer).unwrap();
        assert_eq!(read_buffer(&path).basic.len(), 1);
        persist(&path, &buffer).unwrap(); // Windows 下替换旧快照仍须成功。
        let mut invalid = serde_json::to_value(&buffer).unwrap();
        invalid["basic"][0]["event"]["clipboard"] = serde_json::json!("不应进入导出包");
        fs::write(&path, invalid.to_string()).unwrap();
        assert!(read_buffer(&path).basic.is_empty());
        assert!(serde_json::from_str::<FrontendOperation>("\"任意输入正文\"").is_err());
    }
    #[test]
    fn export_is_readable_and_failure_preserves_evidence() {
        use std::io::Read;
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("report.zip");
        fs::write(dir.path().join("shanyu.log"), "SECRET_LOG").unwrap();
        fs::write(dir.path().join("shanyu.sqlite3"), "SECRET_DATABASE").unwrap();
        let mut buffer = Buffer::default();
        buffer.push(item(Event::Startup {}, 123));
        let manifest = serde_json::json!({"version":"test"});
        assert!(export(&dir.path().join("missing/report.zip"), &buffer, &manifest).is_err());
        assert_eq!(buffer.basic.len(), 1);
        export(&path, &buffer, &manifest).unwrap();
        let mut zip = zip::ZipArchive::new(fs::File::open(path).unwrap()).unwrap();
        assert_eq!(zip.len(), 3);
        for i in 0..zip.len() {
            let mut file = zip.by_index(i).unwrap();
            let mut content = String::new();
            file.read_to_string(&mut content).unwrap();
            assert!(!content.contains("SECRET_"));
            if file.name().unwrap() == "events.jsonl" {
                assert_eq!(
                    serde_json::from_str::<Record>(content.trim())
                        .unwrap()
                        .time_ms,
                    123
                );
            }
        }
    }
}
