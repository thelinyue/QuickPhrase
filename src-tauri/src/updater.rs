//! 更新任务归属于应用而非设置窗口。设备配置不参与个人同步，关闭窗口不会取消任务。
use crate::{
    model::Result,
    private_local,
    store::{db_error, Store},
    update_activity,
    update_protocol::{self as protocol, Config, Manifest, Release},
    AppState,
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::Read,
    sync::Mutex,
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use tauri::{Emitter, Manager, State};
use tokio_util::sync::CancellationToken;

const META: &str = "githubUpdater";
const DAY: u64 = 24 * 60 * 60;
#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct Preferences {
    config: Config,
    last_check: u64,
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    config: Config,
    phase: String,
    release: Option<Manifest>,
    downloaded: u64,
    total: Option<u64>,
    message: String,
    last_check: u64,
    revision: u64,
}
struct Inner {
    preferences: Preferences,
    status: Status,
    release: Option<Release>,
    ready: Option<tempfile::TempDir>,
    cancel: CancellationToken,
}
/// 每次状态变化在同一把锁内递增序号，前端丢弃迟到的快照，避免进度倒退。
pub struct Updater(Mutex<Inner>);
impl Updater {
    pub fn new(store: &Store) -> Self {
        let loaded = private_local::meta(store, META)
            .and_then(|value| {
                if value.is_empty() {
                    Ok(Preferences::default())
                } else {
                    serde_json::from_str::<Preferences>(&value)
                        .map_err(|_| "更新设置无法读取。".into())
                }
            })
            .and_then(|preferences| {
                Ok(Preferences {
                    config: preferences.config.normalize()?,
                    ..preferences
                })
            });
        let message = if loaded.is_err() {
            log::warn!("更新设置无法读取，已暂停自动检查，请在关于页重新保存设置。");
            "更新设置无法读取，已暂停自动检查，请重新保存设置。".to_string()
        } else {
            String::new()
        };
        let preferences = loaded.unwrap_or_else(|_| Preferences {
            config: Config {
                automatic: false,
                ..Config::default()
            },
            ..Preferences::default()
        });
        Self(Mutex::new(Inner {
            status: Status {
                config: preferences.config.clone(),
                phase: "idle".into(),
                release: None,
                downloaded: 0,
                total: None,
                message,
                last_check: preferences.last_check,
                revision: 0,
            },
            preferences,
            release: None,
            ready: None,
            cancel: CancellationToken::new(),
        }))
    }
}
impl Inner {
    fn busy(&self) -> bool {
        ["checking", "downloading", "installing"].contains(&self.status.phase.as_str())
    }
    fn publish(&mut self, app: &tauri::AppHandle) -> Status {
        self.status.revision += 1;
        let _ = app.emit("update-state", &self.status);
        self.status.clone()
    }
    fn persist(&self, store: &Store) -> Result<()> {
        let tx = store.conn.unchecked_transaction().map_err(db_error)?;
        let json = serde_json::to_string(&self.preferences).map_err(db_error)?;
        tx.execute("INSERT INTO sync_meta VALUES(?1,?2) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [META, &json]).map_err(db_error)?;
        tx.commit().map_err(db_error)
    }
}
fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}
fn due(last: u64, time: u64) -> bool {
    last == 0 || time < last || time - last >= DAY
}

#[tauri::command]
pub fn get_update_status(state: State<AppState>) -> Status {
    state.updater.0.lock().unwrap().status.clone()
}

#[tauri::command]
pub fn save_update_config(
    app: tauri::AppHandle,
    state: State<AppState>,
    config: Config,
) -> Result<Status> {
    let config = config.normalize()?;
    let mut inner = state.updater.0.lock().unwrap();
    if inner.busy() {
        return Err("请等待检查完成，或取消下载后再修改线路。".into());
    }
    let previous = inner.preferences.clone();
    inner.preferences.config = config.clone();
    if let Err(e) = inner.persist(&state.device_store.lock().unwrap()) {
        inner.preferences = previous;
        return Err(e);
    }
    inner.status.config = config;
    // 新线路必须重新核对固定版本，不保留旧线路的待安装文件。
    inner.release = None;
    inner.ready = None;
    inner.status.release = None;
    inner.status.phase = "idle".into();
    inner.status.message.clear();
    Ok(inner.publish(&app))
}

#[tauri::command]
pub fn check_update(app: tauri::AppHandle) -> Result<Status> {
    start_check(&app, false)
}
fn start_check(app: &tauri::AppHandle, automatic: bool) -> Result<Status> {
    let state = app.state::<AppState>();
    let mut inner = state.updater.0.lock().unwrap();
    if inner.busy()
        || (automatic
            && (!inner.preferences.config.automatic
                || !due(inner.preferences.last_check, now())
                || inner.ready.is_some()))
    {
        return Ok(inner.status.clone());
    }
    let previous = inner.preferences.last_check;
    inner.preferences.last_check = now();
    if let Err(e) = inner.persist(&state.device_store.lock().unwrap()) {
        inner.preferences.last_check = previous;
        return Err(e);
    }
    inner.status.last_check = inner.preferences.last_check;
    inner.ready = None;
    inner.release = None;
    inner.status.release = None;
    inner.status.message.clear();
    inner.status.phase = "checking".into();
    inner.status.downloaded = 0;
    inner.status.total = None;
    let config = inner.preferences.config.clone();
    let snapshot = inner.publish(app);
    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        let result = protocol::check(&config, &app.package_info().version.to_string()).await;
        let state = app.state::<AppState>();
        let mut inner = state.updater.0.lock().unwrap();
        match result {
            Ok(Some(release)) => {
                inner.status.release = Some(release.manifest.clone());
                inner.release = Some(release);
                inner.status.phase = "available".into();
                inner.status.message = "发现新版本。".into();
            }
            Ok(None) => {
                inner.status.phase = "current".into();
                inner.status.message = "当前已是最新正式版。".into();
            }
            Err(e) => {
                inner.status.phase = "error".into();
                inner.status.message = e;
            }
        }
        inner.publish(&app);
    });
    Ok(snapshot)
}

pub fn start_background(app: tauri::AppHandle) {
    // 隔离验收默认不访问外网；真实线路探测使用独立的 Rust 验收命令。
    #[cfg(debug_assertions)]
    if std::env::var_os("SHANYU_TEST_DATA_DIR").is_some() {
        return;
    }
    tauri::async_runtime::spawn(async move {
        tokio::time::sleep(Duration::from_secs(30)).await;
        loop {
            if let Err(e) = start_check(&app, true) {
                log::warn!("自动更新检查暂时无法启动：{e}");
            }
            tokio::time::sleep(Duration::from_secs(60)).await;
        }
    });
}

#[tauri::command]
pub fn download_update(app: tauri::AppHandle) -> Result<Status> {
    let state = app.state::<AppState>();
    let mut inner = state.updater.0.lock().unwrap();
    if inner.busy() || inner.ready.is_some() {
        return Ok(inner.status.clone());
    }
    let release = inner.release.clone().ok_or("请先检查更新。")?;
    let config = inner.preferences.config.clone();
    inner.cancel = CancellationToken::new();
    let cancel = inner.cancel.clone();
    inner.status.phase = "downloading".into();
    inner.status.message.clear();
    inner.status.downloaded = 0;
    inner.status.total = None;
    let status = inner.publish(&app);
    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        let download = async {
            let client = protocol::client()?;
            let verified = tokio::select! { biased;
                _ = cancel.cancelled() => return Err("下载已取消。".into()),
                result = protocol::fixed_release(&client, &config, &release.manifest.version) => result?,
            };
            if verified.hash != release.hash {
                return Err("该版本安装器已变化，请重新检查更新。".to_string());
            }
            let dir = tempfile::Builder::new()
                .prefix("QuickPhrase-update-")
                .tempdir()
                .map_err(|_| "无法创建更新临时目录。".to_string())?;
            let mut tick = std::time::Instant::now();
            protocol::download(
                &config,
                &verified,
                &dir.path().join(verified.manifest.installer()),
                &cancel,
                |done, total| {
                    if tick.elapsed() < Duration::from_millis(150) && Some(done) != total {
                        return;
                    }
                    tick = std::time::Instant::now();
                    let state = app.state::<AppState>();
                    let mut inner = state.updater.0.lock().unwrap();
                    inner.status.downloaded = done;
                    inner.status.total = total;
                    inner.publish(&app);
                },
            )
            .await?;
            Ok::<_, String>(dir)
        };
        let result = download.await;
        let state = app.state::<AppState>();
        let mut inner = state.updater.0.lock().unwrap();
        match result {
            Ok(dir) => {
                inner.ready = Some(dir);
                inner.status.phase = "ready".into();
                inner.status.message = "下载及签名验证完成，可以退出并安装。".into();
            }
            Err(e) => {
                inner.status.phase = if cancel.is_cancelled() {
                    "available"
                } else {
                    "error"
                }
                .into();
                inner.status.message = e;
            }
        }
        inner.publish(&app);
    });
    Ok(status)
}
#[tauri::command]
pub fn cancel_update(state: State<AppState>) {
    let inner = state.updater.0.lock().unwrap();
    if inner.status.phase == "downloading" {
        inner.cancel.cancel();
    }
}
#[tauri::command]
pub fn ensure_update_idle() -> Result<()> {
    update_activity::ensure_idle()
}

fn check_windows(app: &tauri::AppHandle) -> Result<()> {
    if app.webview_windows().keys().any(|label| {
        [
            "editor",
            "file-editor",
            "common-editor",
            "common-sort",
            "category-sort",
            "file-sort",
            "team-management",
            "user-management",
            "delete-confirm",
        ]
        .contains(&label.as_str())
    }) {
        return Err("请先保存并关闭编辑、排序或管理窗口，再安装更新。".into());
    }
    let state = app.state::<AppState>();
    if state.transfer.is_active() || state.teams.has_active_company_transfers() {
        return Err("请等待导入导出和文件传输完成。".into());
    }
    Ok(())
}

/// 验证时禁止其他进程写入/删除安装器；文件句柄一直保留到安装器进程创建成功。
fn verified_installer(path: &std::path::Path, expected: &str) -> Result<fs::File> {
    let mut options = fs::OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options.share_mode(1);
    }
    let mut file = options
        .open(path)
        .map_err(|_| "更新安装包已丢失或被占用，请重新下载。")?;
    let mut hash = Sha256::new();
    let mut buffer = [0_u8; 65536];
    loop {
        let n = file.read(&mut buffer).map_err(|_| "无法读取更新安装包。")?;
        if n == 0 {
            break;
        }
        hash.update(&buffer[..n]);
    }
    if format!("{:x}", hash.finalize()) != expected {
        return Err("安装包已变化，已拒绝执行，请重新下载。".into());
    }
    Ok(file)
}

/// 隔离验收只从指定测试数据目录读取夹具，仍强制使用产品公钥验证；正式构建不包含此入口。
#[cfg(debug_assertions)]
#[tauri::command]
pub fn qa_load_update(app: tauri::AppHandle, state: State<AppState>) -> Result<Status> {
    let root = std::env::var_os("SHANYU_TEST_DATA_DIR").ok_or("仅允许隔离验收。")?;
    let root = fs::canonicalize(root).map_err(db_error)?;
    if root != fs::canonicalize(&state.device_store.lock().unwrap().dir).map_err(db_error)? {
        return Err("隔离数据目录不匹配。".into());
    }
    let fixture = root.join("update-fixture");
    let manifest = fs::read(fixture.join("release-manifest.json")).map_err(db_error)?;
    let candidate: Manifest = serde_json::from_slice(&manifest).map_err(db_error)?;
    if candidate.version()? <= app.package_info().version {
        return Err("夹具版本必须高于当前版本。".into());
    }
    let release = protocol::verify_metadata(
        protocol::PUBLIC_KEY,
        &fs::read(fixture.join("SHA256SUMS.txt")).map_err(db_error)?,
        &fs::read(fixture.join("SHA256SUMS.txt.minisig")).map_err(db_error)?,
        &manifest,
        &candidate.version,
    )?;
    let mut inner = state.updater.0.lock().unwrap();
    if inner.busy() {
        return Err("请先完成当前更新操作。".into());
    }
    let dir = tempfile::Builder::new()
        .prefix("QuickPhrase-update-qa-")
        .tempdir()
        .map_err(db_error)?;
    let path = dir.path().join(release.manifest.installer());
    fs::copy(fixture.join(release.manifest.installer()), &path).map_err(db_error)?;
    let _file = verified_installer(&path, &release.hash)?;
    inner.status.release = Some(release.manifest.clone());
    inner.release = Some(release);
    inner.ready = Some(dir);
    inner.status.phase = "ready".into();
    inner.status.message = "隔离夹具已通过真实签名和文件校验。".into();
    Ok(inner.publish(&app))
}

#[tauri::command]
pub async fn install_update(app: tauri::AppHandle) -> Result<()> {
    let handoff = update_activity::InstallHandoff::begin()?;
    check_windows(&app)?;
    let (path, hash) = {
        let state = app.state::<AppState>();
        let mut inner = state.updater.0.lock().unwrap();
        let release = inner.release.as_ref().ok_or("请先下载更新。")?;
        let path = inner
            .ready
            .as_ref()
            .ok_or("请先下载更新。")?
            .path()
            .join(release.manifest.installer());
        let hash = release.hash.clone();
        inner.status.phase = "installing".into();
        inner.publish(&app);
        (path, hash)
    };
    let result = tauri::async_runtime::spawn_blocking(move || {
        verified_installer(&path, &hash).map(|file| (path, file))
    })
    .await
    .map_err(|_| "更新安装检查被中断。".to_string())
    .and_then(|v| v);
    let (sender, receiver) = tokio::sync::oneshot::channel();
    let install_app = app.clone();
    // 窗口创建同样在 UI 线程串行执行；最终重查窗口后才启动安装器，不强制丢弃草稿。
    let scheduled = app.run_on_main_thread(move || {
        let result = result.and_then(|(path, _locked_file)| {
            check_windows(&install_app)?;
            std::process::Command::new(path)
                .spawn()
                .map_err(|_| "无法启动安装器，请检查系统权限或安全软件后重试。".to_string())?;
            Ok(())
        });
        let _ = sender.send(result);
    });
    let result = if scheduled.is_err() {
        Err("无法启动安装流程，请重试。".into())
    } else {
        receiver
            .await
            .unwrap_or_else(|_| Err("安装流程已中断。".into()))
    };
    let state = app.state::<AppState>();
    let mut inner = state.updater.0.lock().unwrap();
    match result {
        Ok(()) => {
            // 安装器仍需要源文件，不能随 Rust 状态析构删除；系统临时目录由系统后续回收。
            if let Some(dir) = inner.ready.take() {
                let _ = dir.keep();
            }
            handoff.commit();
            state
                .native
                .running
                .store(false, std::sync::atomic::Ordering::Release);
            app.exit(0);
            Ok(())
        }
        Err(e) => {
            inner.status.phase = "ready".into();
            inner.status.message = e.clone();
            inner.publish(&app);
            Err(e)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn schedule_handles_restart_and_clock_change() {
        assert!(due(0, 10));
        assert!(!due(100, 101));
        assert!(due(100, 100 + DAY));
        assert!(due(100, 50));
    }
    #[test]
    fn preference_roundtrip_is_device_local_and_failed_package_cannot_execute() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        let updater = Updater::new(&store);
        let mut inner = updater.0.lock().unwrap();
        inner.preferences.config.route = "direct".into();
        inner.preferences.config.automatic = false;
        inner.preferences.last_check = 123;
        inner.persist(&store).unwrap();
        drop(inner);
        let reopened = Updater::new(&store);
        let loaded = reopened.0.lock().unwrap();
        assert_eq!(loaded.preferences.last_check, 123);
        assert!(!loaded.preferences.config.automatic);
        assert_eq!(loaded.preferences.config.route, "direct");
        let file = dir.path().join("setup.exe");
        fs::write(&file, b"valid bytes").unwrap();
        let hash = protocol::sha256(b"valid bytes");
        assert!(verified_installer(&file, &hash).is_ok());
        fs::write(&file, b"tampered").unwrap();
        assert!(verified_installer(&file, &hash).is_err());
        store
            .conn
            .execute("UPDATE sync_meta SET value='broken' WHERE key=?1", [META])
            .unwrap();
        let damaged = Updater::new(&store);
        assert!(!damaged.0.lock().unwrap().preferences.config.automatic);
    }
}
