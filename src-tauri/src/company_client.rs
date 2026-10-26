//! 云文件网络任务使用账号世代和固定任务 ID；上传源文件先固化，未知回执只查询原任务。
//! 云文件缓存随账号退出清理，上传待核对任务独立保存，个人副本不放在此缓存目录。
use super::*;
use std::io::{Read, Write};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct CompanyMetadata {
    #[serde(flatten)]
    file: wire::CompanyFile,
    #[serde(default)]
    original_name: Option<String>,
}
impl CompanyMetadata {
    fn upload_matches(&self, id: &str, name: &str, size: u64) -> bool {
        self.file.asset_id == id
            && self.original_name.as_deref().unwrap_or(&self.file.name) == name
            && self.file.size == size
    }
}

#[derive(Default)]
pub(super) struct CompanyNames {
    epoch: u64,
    version: u64,
    names: HashMap<String, String>,
    catalog: Option<wire::CompanyFiles>,
}

#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanySelection {
    pub segment: usize,
    pub block: usize,
    pub asset_id: String,
    pub session_epoch: u64,
}
impl CompanySelection {
    pub fn validate(phrase: &local::Phrase, selections: &[Self]) -> Result<()> {
        if selections.len() > 9 {
            return Err("每条话术最多9个文件。".into());
        }
        let mut positions = vec![];
        for selection in selections {
            let position = (selection.segment, selection.block);
            if positions.contains(&position)
                || !matches!(
                    phrase
                        .segments
                        .get(selection.segment)
                        .and_then(|s| s.blocks.get(selection.block)),
                    Some(local::Block::File { .. })
                )
            {
                return Err("云文件位置无效或重复，请重新选择。".into());
            }
            positions.push(position);
        }
        Ok(())
    }
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanyUpload {
    pub id: String,
    pub name: String,
    pub size: u64,
    server: String,
    user_id: String,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanyEntry {
    #[serde(flatten)]
    pub file: wire::CompanyFile,
    pub cached: bool,
}
#[derive(Serialize)]
pub struct CompanyView {
    pub version: u64,
    pub files: Vec<CompanyEntry>,
    pub uploads: Vec<CompanyUpload>,
    pub offline: bool,
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CompanyTransfer {
    pub session_epoch: u64,
    pub asset_id: String,
    pub name: String,
    pub direction: String,
    pub done: u64,
    pub total: u64,
    pub status: String,
    pub revision: u64,
    /// 逐项错误保存在 Rust 快照中，窗口关闭重开仍可查看；不包含源文件载荷。
    pub message: String,
}

/// 队列归属于账号会话而非 WebView；任务 ID 在入队时固定，准备与重试不能另建云文件。
#[derive(Clone)]
struct UploadJob {
    ctx: AccountContext,
    id: String,
    name: String,
    version: u64,
    cancel: CancellationToken,
}
fn transfer_key(epoch: u64, direction: &str, id: &str) -> String {
    format!(
        "{epoch}:{}:{id}",
        if direction == "upload" {
            "upload"
        } else {
            "company"
        }
    )
}

/// Rust 保存传输真相；窗口重开只订阅和读取快照，不重新启动下载或上传。
fn advance_transfer(
    app: &tauri::AppHandle,
    history: &Mutex<HashMap<String, CompanyTransfer>>,
    key: &str,
    bytes: (u64, u64),
    status: &str,
) {
    let mut records = history.lock().unwrap();
    if let Some(record) = records.get_mut(key) {
        record.done = bytes.0;
        record.total = bytes.1;
        record.status = status.into();
        record.message.clear();
        record.revision += 1;
        let _ = app.emit("company-transfer", record.clone());
    }
}

/// 保留扩展名，处理 Windows 保留名及末尾点空格；目录由素材 ID 隔离同名文件。
pub fn safe_filename(name: &str) -> String {
    let name: String = name
        .chars()
        .map(|c| {
            if c.is_control() || "<>:\"/\\|?*".contains(c) {
                '_'
            } else {
                c
            }
        })
        .collect();
    let name = name.trim_end_matches(['.', ' ']);
    let stem = name.split('.').next().unwrap_or("").to_ascii_uppercase();
    let reserved = [
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8",
        "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];
    let name = if name.is_empty() {
        "文件".into()
    } else if reserved.contains(&stem.as_str()) {
        format!("_{name}")
    } else {
        name.to_string()
    };
    // 按 UTF-16 长度给 Windows 文件名及完成标记留出空间，长名称优先保留最后的扩展名。
    if name.encode_utf16().count() <= 200 {
        return name;
    }
    let (stem, extension) = name
        .rfind('.')
        .filter(|&index| index > 0 && name[index..].encode_utf16().count() < 200)
        .map(|index| (&name[..index], &name[index..]))
        .unwrap_or((&name, ""));
    let mut remaining = 200 - extension.encode_utf16().count();
    let stem: String = stem
        .chars()
        .take_while(|c| {
            if c.len_utf16() > remaining {
                return false;
            }
            remaining -= c.len_utf16();
            true
        })
        .collect();
    format!("{stem}{extension}")
        .trim_end_matches(['.', ' '])
        .to_string()
}
impl TeamClient {
    /// 退出只取消未完成的执行，不清除已固化任务；重启不恢复队列，也不自动重传。
    pub fn stop_company_uploads(&self) {
        for (key, cancel) in self.company_transfers.lock().unwrap().iter() {
            if key.contains(":upload:") {
                cancel.cancel();
            }
        }
    }
    /// 复制期间退出可能留下半份副本。只有固定 ID 目录且从未提交 task.json 才能清理。
    pub(super) fn clean_uncommitted_uploads(&self) -> Result<()> {
        let root = self.dir.join("company-uploads");
        if !root.exists() {
            return Ok(());
        }
        for entry in fs::read_dir(&root).map_err(io_error)? {
            let entry = entry.map_err(io_error)?;
            if entry.file_type().map_err(io_error)?.is_dir()
                && uuid::Uuid::parse_str(&entry.file_name().to_string_lossy()).is_ok()
                && !entry.path().join("task.json").exists()
            {
                fs::remove_dir_all(entry.path()).map_err(io_error)?;
            }
        }
        Ok(())
    }
    pub fn has_active_company_transfers(&self) -> bool {
        !self.company_transfers.lock().unwrap().is_empty()
    }
    pub fn company_context(
        &self,
        epoch: u64,
        operation: wire::CompanyOperation,
    ) -> Result<AccountContext> {
        let active = self.active()?;
        if epoch != active.epoch {
            return Err("账号已变化，请重新打开云文件。".into());
        }
        if !active.authorization.capabilities.company_files {
            return Err("当前闪云版本不支持云文件，请升级服务端。".into());
        }
        if active.offline {
            return Err("请重连后操作。".into());
        }
        if active.authorization.user.must_change_password || active.authorization.user.disabled {
            return Err("请联网并完成初始改密后使用云文件。".into());
        }
        let permissions = if active.authorization.capabilities.company_file_permissions {
            active.authorization.user.company_file_permissions.clone()
        } else {
            wire::CompanyFilePermissions::default()
        };
        if !permissions.allows(active.authorization.user.admin, operation) {
            return Err("没有此项云文件管理权限，请联系服务管理员授权。".into());
        }
        Ok(AccountContext {
            server: active.connection.server,
            user_id: active.authorization.user.id,
            username: active.authorization.user.username,
            epoch: active.epoch,
            cancel: active.cancel,
        })
    }
    /// 本机缓存读取沿用账号、改密、停用和授权边界，但不要求服务端当前在线。
    pub(crate) fn company_cached_context(&self, epoch: u64) -> Result<AccountContext> {
        let active = self.active()?;
        if active.epoch != epoch
            || !active.authorization.capabilities.company_files
            || active.authorization.user.must_change_password
            || active.authorization.user.disabled
            || active.cancel.is_cancelled()
        {
            return Err("云文件账号已变化或不可用。".into());
        }
        let permissions = if active.authorization.capabilities.company_file_permissions {
            active.authorization.user.company_file_permissions.clone()
        } else {
            wire::CompanyFilePermissions::default()
        };
        if !permissions.allows(
            active.authorization.user.admin,
            wire::CompanyOperation::Read,
        ) {
            return Err("没有云文件读取权限，请联系服务管理员授权。".into());
        }
        Ok(AccountContext {
            server: active.connection.server,
            user_id: active.authorization.user.id,
            username: active.authorization.user.username,
            epoch: active.epoch,
            cancel: active.cancel,
        })
    }
    pub(super) fn company_cache_path(&self, active: &Active, id: &str, name: &str) -> PathBuf {
        self.dir
            .join(&active.connection.partition)
            .join("company")
            .join(&active.authorization.user.id)
            .join(id)
            .join(safe_filename(name))
    }
    fn company_catalog_path(&self, active: &Active) -> PathBuf {
        self.dir
            .join(&active.connection.partition)
            .join("company")
            .join(&active.authorization.user.id)
            .join("catalog.json")
    }
    pub(super) fn restore_company_catalog(&self) {
        let Ok(active) = self.active() else { return };
        if !active.authorization.capabilities.company_files {
            return;
        }
        if let Some(catalog) = load_json::<wire::CompanyFiles>(&self.company_catalog_path(&active))
        {
            *self.company_names.lock().unwrap() = CompanyNames {
                epoch: active.epoch,
                version: catalog.version,
                names: catalog
                    .files
                    .iter()
                    .map(|file| (file.asset_id.clone(), file.name.clone()))
                    .collect(),
                catalog: Some(catalog),
            };
        }
    }
    pub(super) fn current_company_name(&self, epoch: u64, id: &str, fallback: &str) -> String {
        let names = self.company_names.lock().unwrap();
        if names.epoch == epoch {
            names
                .names
                .get(id)
                .cloned()
                .unwrap_or_else(|| fallback.into())
        } else {
            fallback.into()
        }
    }
    /// 云端清理不删除本机完整缓存；按账号和不可变 ID 查找，兼容改名前的文件路径。
    pub(super) fn existing_company_cache(
        &self,
        active: &Active,
        id: &str,
        name: &str,
    ) -> Option<PathBuf> {
        if uuid::Uuid::parse_str(id).is_err() {
            return None;
        }
        let preferred = self.company_cache_path(active, id, name);
        if ready_path(&preferred) {
            return Some(preferred);
        }
        fs::read_dir(preferred.parent()?)
            .ok()?
            .flatten()
            .find_map(|entry| {
                let path = entry.path();
                (entry.file_type().ok()?.is_file() && ready_path(&path)).then_some(path)
            })
    }
    pub fn linked_company_existing_cache(
        &self,
        epoch: u64,
        id: &str,
        name: &str,
    ) -> Result<Option<PathBuf>> {
        let ctx = self.company_cached_context(epoch)?;
        let active = self.active()?;
        self.with_account(&ctx, || Ok(self.existing_company_cache(&active, id, name)))
    }
    /// 全局搜索只读取已授权的完整目录快照，不随每个搜索词发起网络请求。
    pub fn cached_company_files(&self, epoch: u64) -> Option<Vec<CompanyEntry>> {
        let active = self.active().ok()?;
        if active.epoch != epoch || !active.authorization.capabilities.company_files {
            return None;
        }
        let names = self.company_names.lock().unwrap();
        if names.epoch != epoch {
            return None;
        }
        Some(
            names
                .catalog
                .as_ref()?
                .files
                .iter()
                .cloned()
                .map(|file| {
                    let cached = self
                        .existing_company_cache(&active, &file.asset_id, &file.name)
                        .is_some();
                    CompanyEntry { file, cached }
                })
                .collect(),
        )
    }
    fn company_progress(
        &self,
        app: &tauri::AppHandle,
        ctx: &AccountContext,
        file: (&str, &str),
        direction: &str,
        bytes: (u64, u64),
        status: &str,
    ) {
        let _ = self.with_account(ctx, || {
            let key = transfer_key(ctx.epoch, direction, file.0);
            self.company_history
                .lock()
                .unwrap()
                .entry(key.clone())
                .or_insert_with(|| CompanyTransfer {
                    session_epoch: ctx.epoch,
                    asset_id: file.0.into(),
                    name: file.1.into(),
                    direction: direction.into(),
                    done: bytes.0,
                    total: bytes.1,
                    status: status.into(),
                    revision: 0,
                    message: String::new(),
                });
            advance_transfer(app, &self.company_history, &key, bytes, status);
            Ok(())
        });
    }
    /// 不可变 ID 允许复用已校验缓存；保留在用路径，改名不会重新下载完整文件。
    fn reuse_company_cache(&self, active: &Active, file: &wire::CompanyFile) -> Result<PathBuf> {
        self.reuse_company_named_cache(active, &file.asset_id, &file.name, Some(file.size))
    }
    pub(super) fn reuse_company_named_cache(
        &self,
        active: &Active,
        id: &str,
        name: &str,
        expected_size: Option<u64>,
    ) -> Result<PathBuf> {
        let path = self.company_cache_path(active, id, name);
        if ready_path(&path) {
            return Ok(path);
        }
        if let Ok(entries) = fs::read_dir(path.parent().ok_or("缓存目录无效。")?) {
            for entry in entries.flatten() {
                let source = entry.path();
                if source != path
                    && !entry.file_type().map_err(io_error)?.is_symlink()
                    && ready_path(&source)
                    && fs::metadata(&source)
                        .is_ok_and(|m| expected_size.is_none_or(|size| m.len() == size))
                {
                    let mut temp = tempfile::NamedTempFile::new_in(path.parent().unwrap())
                        .map_err(io_error)?;
                    let size =
                        std::io::copy(&mut fs::File::open(source).map_err(io_error)?, &mut temp)
                            .map_err(io_error)?;
                    temp.persist(&path).map_err(io_error)?;
                    save_json(&path.with_extension("ready.json"), &size)?;
                    break;
                }
            }
        }
        Ok(path)
    }
    pub fn linked_company_cached(
        &self,
        epoch: u64,
        file: &wire::CompanyFile,
    ) -> Result<Option<PathBuf>> {
        let ctx = self.company_context(epoch, wire::CompanyOperation::Read)?;
        let active = self.active()?;
        self.with_account(&ctx, || {
            let path = self.reuse_company_cache(&active, file)?;
            Ok(ready_path(&path).then_some(path))
        })
    }
    pub async fn rename_company(
        &self,
        app: &tauri::AppHandle,
        epoch: u64,
        id: &str,
        expected: &str,
        name: &str,
    ) -> Result<()> {
        if uuid::Uuid::parse_str(id).is_err() {
            return Err("云文件编号无效。".into());
        }
        let ctx = self.company_context(epoch, wire::CompanyOperation::Read)?;
        let active = self.active()?;
        self.with_account(&ctx, || Ok(()))?;
        self.response(
            &active,
            Method::PATCH,
            &format!("company/files/{id}"),
            Some(serde_json::json!({"name":name,"expectedName":expected})),
        )
        .await
        .map_err(|error| {
            // response 已统一转换非成功状态，需在这里识别旧版闪云不支持的 PATCH。
            if error.starts_with("405:") {
                "当前闪云版本不支持重命名，请升级服务端。".to_owned()
            } else {
                error
            }
        })?;
        self.with_account(&ctx, || Ok(()))?;
        let _ = app.emit("company-files-changed", epoch);
        Ok(())
    }
    fn upload_tasks(&self, ctx: &AccountContext) -> Vec<CompanyUpload> {
        let mut uploads = vec![];
        if let Ok(entries) = fs::read_dir(self.dir.join("company-uploads")) {
            for entry in entries.flatten() {
                if let Some(task) = load_json::<CompanyUpload>(&entry.path().join("task.json")) {
                    if task.server == ctx.server && task.user_id == ctx.user_id {
                        uploads.push(task);
                    }
                }
            }
        }
        uploads
    }
    pub fn company_transfer_view(&self, epoch: u64) -> Result<Vec<CompanyTransfer>> {
        let active = self.active()?;
        if active.epoch != epoch {
            return Err("账号已变化，旧任务视图已失效。".into());
        }
        let ctx = AccountContext {
            server: active.connection.server,
            user_id: active.authorization.user.id,
            username: active.authorization.user.username,
            epoch,
            cancel: active.cancel,
        };
        self.with_account(&ctx, || {
            let mut records: Vec<_> = self
                .company_history
                .lock()
                .unwrap()
                .values()
                .filter(|v| v.session_epoch == epoch)
                .cloned()
                .collect();
            // 未决上传属于当前账号，撤权后仍可查询；不能因窗口清空或客户端重启丢掉原任务。
            for task in self.upload_tasks(&ctx) {
                if !records
                    .iter()
                    .any(|r| r.direction == "upload" && r.asset_id == task.id)
                {
                    records.push(CompanyTransfer {
                        session_epoch: epoch,
                        asset_id: task.id,
                        name: task.name,
                        direction: "upload".into(),
                        done: 0,
                        total: task.size,
                        status: "待核对".into(),
                        revision: 0,
                        message: String::new(),
                    });
                }
            }
            records.sort_by(|a, b| a.name.cmp(&b.name).then(a.asset_id.cmp(&b.asset_id)));
            Ok(records)
        })
    }
    pub fn clear_company_transfers(
        &self,
        epoch: u64,
        direction: &str,
    ) -> Result<Vec<CompanyTransfer>> {
        if direction != "upload" && direction != "download" {
            return Err("传输类型无效。".into());
        }
        // 只移除内存中的已结束记录，不触碰缓存、本体及落盘的未决上传。
        let active = self.active()?;
        if active.epoch != epoch {
            return Err("账号已变化，旧任务视图已失效。".into());
        }
        let ctx = AccountContext {
            server: active.connection.server,
            user_id: active.authorization.user.id,
            username: active.authorization.user.username,
            epoch,
            cancel: active.cancel,
        };
        self.with_account(&ctx, || {
            self.company_history.lock().unwrap().retain(|_, r| {
                r.session_epoch != epoch
                    || r.direction != direction
                    || r.status == "传输中"
                    || (direction == "upload"
                        && !matches!(r.status.as_str(), "已完成" | "准备失败" | "已停止"))
            });
            Ok(())
        })?;
        self.company_transfer_view(epoch)
    }
    pub async fn company_selection(
        &self,
        selection: &CompanySelection,
    ) -> Result<wire::CompanyFile> {
        let ctx = self.company_context(selection.session_epoch, wire::CompanyOperation::Read)?;
        if uuid::Uuid::parse_str(&selection.asset_id).is_err() {
            return Err("云文件编号无效。".into());
        }
        let active = self.active()?;
        self.with_account(&ctx, || Ok(()))?;
        let file: wire::CompanyFile = self
            .request(
                &active,
                Method::GET,
                &format!("company/files/{}", selection.asset_id),
                None,
            )
            .await?;
        self.with_account(&ctx, || {
            if file.deleted_at.is_some() {
                Err("云文件已回收，请重新选择。".into())
            } else {
                Ok(file)
            }
        })
    }
    pub async fn company_view(
        &self,
        epoch: u64,
        recycled: bool,
        query: String,
    ) -> Result<CompanyView> {
        let active = self.active()?;
        if active.epoch != epoch {
            return Err("账号已变化，请重新打开云文件。".into());
        }
        if active.offline {
            if recycled {
                return Err("连接已断开，回收站暂不可用。".into());
            }
            return self.offline_company_view(epoch, &query);
        }
        let ctx = self.company_context(
            epoch,
            if recycled {
                wire::CompanyOperation::RecycleBin
            } else {
                wire::CompanyOperation::Read
            },
        )?;
        self.with_account(&ctx, || Ok(()))?;
        let complete = !recycled && query.is_empty();
        let url = Url::parse_with_params(
            &format!("{}/api/company/files", ctx.server),
            &[("recycled", recycled.to_string()), ("q", query.clone())],
        )
        .map_err(|_| "云文件查询无效。")?;
        let request = self
            .client
            .get(url)
            .bearer_auth(&active.token)
            .timeout(Duration::from_secs(30));
        let response = tokio::select! {
            _ = ctx.cancel.cancelled() => return Err("账号已变化，目录查询已取消。".into()),
            response = request.send() => match response {
                Ok(response) => response,
                Err(_) if !recycled => return self.offline_company_view(epoch, &query),
                Err(_) => return Err("云文件目录加载失败，请检查网络。".into()),
            },
        };
        if response.status().is_server_error() && !recycled {
            return self.offline_company_view(epoch, &query);
        }
        let catalog: wire::CompanyFiles = match self.check(response).await?.json().await {
            Ok(catalog) => catalog,
            Err(_) if !recycled => return self.offline_company_view(epoch, &query),
            Err(_) => return Err("云文件目录无法读取。".into()),
        };
        self.with_account(&ctx, || {
            {
                let mut names = self.company_names.lock().unwrap();
                if names.epoch != epoch {
                    *names = CompanyNames {
                        epoch,
                        ..Default::default()
                    };
                }
                if catalog.version >= names.version {
                    names.version = catalog.version;
                    for file in &catalog.files {
                        names.names.insert(file.asset_id.clone(), file.name.clone());
                    }
                }
                if complete
                    && names
                        .catalog
                        .as_ref()
                        .is_none_or(|old| catalog.version >= old.version)
                {
                    names.catalog = Some(catalog.clone());
                    save_json(&self.company_catalog_path(&active), &catalog)?;
                }
            }
            let files = catalog
                .files
                .into_iter()
                .map(|file| {
                    let cached = self
                        .reuse_company_cache(&active, &file)
                        .is_ok_and(|path| ready_path(&path));
                    CompanyEntry { file, cached }
                })
                .collect();
            let uploads = self.upload_tasks(&ctx);
            Ok(CompanyView {
                version: catalog.version,
                files,
                uploads,
                offline: false,
            })
        })
    }
    /// 离线快照只用于展示，不能借此新增引用；每次读取重新检查完整缓存字节。
    fn offline_company_view(&self, epoch: u64, query: &str) -> Result<CompanyView> {
        let ctx = self.company_cached_context(epoch)?;
        let active = self.active()?;
        self.with_account(&ctx, || {
            let catalog = {
                let names = self.company_names.lock().unwrap();
                (names.epoch == epoch)
                    .then(|| names.catalog.clone())
                    .flatten()
            }
            .ok_or("连接已断开，尚无可用的最近目录。")?;
            let query = query.to_lowercase();
            let files = catalog
                .files
                .into_iter()
                .filter(|file| {
                    file.deleted_at.is_none() && file.name.to_lowercase().contains(&query)
                })
                .map(|file| {
                    let cached = self
                        .existing_company_cache(&active, &file.asset_id, &file.name)
                        .is_some();
                    CompanyEntry { file, cached }
                })
                .collect();
            Ok(CompanyView {
                version: catalog.version,
                files,
                uploads: vec![],
                offline: true,
            })
        })
    }
    fn upload_dir(&self, id: &str) -> Result<PathBuf> {
        if uuid::Uuid::parse_str(id).is_err() {
            return Err("上传任务编号无效。".into());
        }
        Ok(self.dir.join("company-uploads").join(id))
    }
    /// 入队只登记路径和固定 ID，实际文件校验及副本复制在获得执行名额后进行。
    pub fn enqueue_company_uploads(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        epoch: u64,
        paths: Vec<String>,
    ) -> Result<Vec<CompanyTransfer>> {
        let _activity = crate::update_activity::Activity::enter()?;
        let ctx = self.company_context(epoch, wire::CompanyOperation::Upload)?;
        let version = self
            .active()?
            .authorization
            .user
            .company_file_permissions
            .upload_version;
        let mut seen = std::collections::HashSet::new();
        let mut jobs = vec![];
        for path in paths {
            let normalized: PathBuf = Path::new(&path).components().collect();
            #[cfg(windows)]
            let normalized = normalized.to_string_lossy().to_lowercase();
            if !seen.insert(normalized) {
                continue;
            }
            let name = Path::new(&path)
                .file_name()
                .map(|v| v.to_string_lossy().into_owned())
                .filter(|v| !v.is_empty())
                .unwrap_or_else(|| "文件".into());
            jobs.push((
                UploadJob {
                    ctx: ctx.clone(),
                    id: local::id(),
                    name,
                    version,
                    cancel: ctx.cancel.child_token(),
                },
                path,
            ));
        }
        if jobs.is_empty() {
            return Err("请选择要上传的文件。".into());
        }
        // 权限检查与任务登记在同一短锁内，SSE 撤权一定能取消已经接受的任务。
        let initial = self.with_upload_authority(&ctx, version, &ctx.cancel, || {
            let mut tasks = self.company_transfers.lock().unwrap();
            let mut history = self.company_history.lock().unwrap();
            let mut initial = vec![];
            for (job, _) in &jobs {
                let key = transfer_key(epoch, "upload", &job.id);
                tasks.insert(key.clone(), job.cancel.clone());
                let record = CompanyTransfer {
                    session_epoch: epoch,
                    asset_id: job.id.clone(),
                    name: job.name.clone(),
                    direction: "upload".into(),
                    done: 0,
                    total: 0,
                    status: "等待上传".into(),
                    revision: 1,
                    message: String::new(),
                };
                history.insert(key, record.clone());
                initial.push(record);
            }
            Ok(initial)
        })?;
        for record in &initial {
            let _ = app.emit("company-transfer", record);
        }
        for (job, path) in jobs {
            let client = self.clone();
            let app = app.clone();
            tauri::async_runtime::spawn(async move {
                client.run_queued_upload(&app, job, path).await;
            });
        }
        Ok(initial)
    }
    async fn run_queued_upload(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        job: UploadJob,
        path: String,
    ) {
        let mut prepared = false;
        let result: Result<wire::CompanyFile> = async {
            let _permit = tokio::select! {
                biased;
                _ = job.cancel.cancelled() => return Err("上传已停止。".into()),
                permit = self.uploads.acquire() => permit.map_err(|_| "上传已关闭。")?,
            };
            self.with_upload_authority(&job.ctx, job.version, &job.cancel, || Ok(()))?;
            self.company_progress(
                app,
                &job.ctx,
                (&job.id, &job.name),
                "upload",
                (0, 0),
                "准备中",
            );
            let client = self.clone();
            let preparation = job.clone();
            let task = tauri::async_runtime::spawn_blocking(move || {
                client.prepare_upload_source(&preparation, &path)
            })
            .await
            .map_err(|_| "上传文件准备中断。".to_owned())??;
            prepared = true;
            let result = self.company_upload_body(app, &job, &task, true).await;
            // 网络失败时先核验连接再交出名额；离线会取消后续任务，单文件失败仍可继续其他项。
            if result.is_err()
                && !job.cancel.is_cancelled()
                && self.with_account(&job.ctx, || Ok(())).is_ok()
            {
                let _ = self.refresh_authorization(app).await;
            }
            result
        }
        .await;
        self.finish_upload(
            app,
            &job,
            result.as_ref().map(|_| ()).map_err(Clone::clone),
            prepared,
        );
    }
    #[cfg(test)]
    pub fn prepare_company_upload(&self, epoch: u64, path: &str) -> Result<CompanyUpload> {
        let ctx = self.company_context(epoch, wire::CompanyOperation::Upload)?;
        let version = self
            .active()?
            .authorization
            .user
            .company_file_permissions
            .upload_version;
        let name = Path::new(path)
            .file_name()
            .ok_or("文件名无效。")?
            .to_string_lossy()
            .into_owned();
        let job = UploadJob {
            id: local::id(),
            name,
            version,
            cancel: ctx.cancel.child_token(),
            ctx,
        };
        self.prepare_upload_source(&job, path)
    }
    pub async fn prepare_company_upload_async(
        self: &Arc<Self>,
        epoch: u64,
        path: String,
    ) -> Result<CompanyUpload> {
        let _activity = crate::update_activity::Activity::enter()?;
        let ctx = self.company_context(epoch, wire::CompanyOperation::Upload)?;
        let job = UploadJob {
            id: local::id(),
            name: Path::new(&path)
                .file_name()
                .ok_or("文件名无效。")?
                .to_string_lossy()
                .into_owned(),
            version: self
                .active()?
                .authorization
                .user
                .company_file_permissions
                .upload_version,
            cancel: ctx.cancel.child_token(),
            ctx,
        };
        // 旧单文件 IPC 也保存入队时授权；等待名额期间撤权再授权不能接收旧准备请求。
        let _permit = tokio::select! {
            biased;
            _ = job.cancel.cancelled() => return Err("上传准备已停止。".into()),
            permit = self.uploads.acquire() => permit.map_err(|_| "上传已关闭。")?,
        };
        let client = self.clone();
        tauri::async_runtime::spawn_blocking(move || client.prepare_upload_source(&job, &path))
            .await
            .map_err(|_| "上传文件准备中断，请重新选择。".to_owned())?
    }
    /// 复制不占账号锁；Windows 禁止源文件写入与替换，每块检查取消，提交前复核原授权。
    fn prepare_upload_source(&self, job: &UploadJob, path: &str) -> Result<CompanyUpload> {
        self.with_upload_authority(&job.ctx, job.version, &job.cancel, || Ok(()))?;
        let source = Path::new(path);
        if !fs::metadata(source)
            .map_err(|_| "文件无法读取或不存在。")?
            .is_file()
        {
            return Err("文件夹不支持上传，请选择文件。".into());
        }
        let mut options = fs::OpenOptions::new();
        options.read(true);
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            options.share_mode(1);
        }
        let mut input = options
            .open(source)
            .map_err(|_| "文件无法读取或正在被修改，请稍后再试。")?;
        let metadata = input.metadata().map_err(|_| "文件无法读取。")?;
        if !metadata.is_file() {
            return Err("只能上传普通文件。".into());
        }
        let size = metadata.len();
        if size == 0 || size > LIMIT {
            return Err("单文件需为1字节至128MB。".into());
        }
        let task = CompanyUpload {
            id: job.id.clone(),
            name: job.name.clone(),
            size,
            server: job.ctx.server.clone(),
            user_id: job.ctx.user_id.clone(),
        };
        let dir = self.upload_dir(&job.id)?;
        fs::create_dir_all(&dir).map_err(io_error)?;
        let result = (|| {
            let mut output = fs::File::create(dir.join("content")).map_err(io_error)?;
            let mut buffer = [0_u8; 64 * 1024];
            let mut copied = 0;
            loop {
                if job.cancel.is_cancelled() {
                    return Err("上传准备已停止。".into());
                }
                let read = input.read(&mut buffer).map_err(io_error)?;
                if read == 0 {
                    break;
                }
                copied += read as u64;
                if copied > size {
                    return Err("文件在复制期间变化，请重新选择。".into());
                }
                output.write_all(&buffer[..read]).map_err(io_error)?;
            }
            if copied != size {
                return Err("文件在复制期间变化，请重新选择。".into());
            }
            output.sync_all().map_err(io_error)?;
            self.with_upload_authority(&job.ctx, job.version, &job.cancel, || {
                save_json(&dir.join("task.json"), &task)?;
                Ok(task)
            })
        })();
        if result.is_err() {
            let _ = fs::remove_dir_all(dir);
        }
        result
    }
    pub async fn company_upload(
        &self,
        app: &tauri::AppHandle,
        epoch: u64,
        id: String,
        retry: bool,
    ) -> Result<wire::CompanyFile> {
        let _activity = crate::update_activity::Activity::enter()?;
        // 查询原任务仍属于读取，撤权后可以核对已经登记的云文件。
        let ctx = self.company_context(epoch, wire::CompanyOperation::Read)?;
        let active = self.active()?;
        let task = load_json::<CompanyUpload>(&self.upload_dir(&id)?.join("task.json"))
            .ok_or("上传任务不存在。")?;
        if task.id != id || task.server != ctx.server || task.user_id != ctx.user_id {
            return Err("上传任务不属于当前账号。".into());
        }
        let job = UploadJob {
            ctx,
            id,
            name: task.name.clone(),
            version: active
                .authorization
                .user
                .company_file_permissions
                .upload_version,
            cancel: active.cancel.child_token(),
        };
        self.with_account(&job.ctx, || {
            let key = transfer_key(epoch, "upload", &job.id);
            let mut tasks = self.company_transfers.lock().unwrap();
            if tasks.contains_key(&key) {
                return Err("该文件正在上传或等待上传。".into());
            }
            tasks.insert(key, job.cancel.clone());
            Ok(())
        })?;
        self.company_progress(
            app,
            &job.ctx,
            (&job.id, &job.name),
            "upload",
            (0, task.size),
            if retry { "等待上传" } else { "传输中" },
        );
        let result = async {
            let _permit = if retry {
                Some(tokio::select! {
                    biased;
                    _ = job.cancel.cancelled() => return Err("上传已停止，请查询原任务。".into()),
                    permit = self.uploads.acquire() => permit.map_err(|_| "上传已关闭。")?,
                })
            } else {
                None
            };
            self.company_upload_body(app, &job, &task, retry).await
        }
        .await;
        self.finish_upload(
            app,
            &job,
            result.as_ref().map(|_| ()).map_err(Clone::clone),
            true,
        );
        result
    }
    async fn company_upload_body(
        &self,
        app: &tauri::AppHandle,
        job: &UploadJob,
        task: &CompanyUpload,
        retry: bool,
    ) -> Result<wire::CompanyFile> {
        let ctx = &job.ctx;
        let id = &job.id;
        let cancel = &job.cancel;
        let active = self.active()?;
        self.company_context(ctx.epoch, wire::CompanyOperation::Read)?;
        self.with_account(ctx, || Ok(()))?;
        self.company_progress(
            app,
            ctx,
            (id, &task.name),
            "upload",
            (0, task.size),
            "传输中",
        );
        // 只有明确不存在才传输；网络错误、权限错误和永久删除标记不能触发新上传。
        let query = self
            .client
            .get(format!("{}/api/company/files/{id}", ctx.server))
            .bearer_auth(&active.token)
            .timeout(Duration::from_secs(30));
        let response = tokio::select! {
            biased;
            _ = cancel.cancelled() => return Err("上传已停止，请查询原任务。".into()),
            response = query.send() => response.map_err(|_| "上传结果查询失败，不会重新上传。")?,
        };
        let file = if response.status() != reqwest::StatusCode::NOT_FOUND {
            let file = self
                .check(response)
                .await?
                .json::<CompanyMetadata>()
                .await
                .map_err(|_| "上传结果无法读取。")?;
            if !file.upload_matches(id, &task.name, task.size) {
                return Err("上传结果与原任务不符，源副本已保留。".into());
            }
            self.with_account(ctx, || Ok(file.file))?
        } else {
            if !retry {
                return Err("原任务尚未登记，源副本已保留；获得上传权限后可明确继续。".into());
            }
            self.with_upload_authority(ctx, job.version, cancel, || Ok(()))?;
            let source = tokio::fs::File::open(self.upload_dir(id)?.join("content"))
                .await
                .map_err(io_error)?;
            if source.metadata().await.map_err(io_error)?.len() != task.size {
                return Err("上传源副本不完整，请重新选择文件。".into());
            }
            let mut done = 0;
            let progress_app = app.clone();
            let history = self.company_history.clone();
            let key = transfer_key(ctx.epoch, "upload", id);
            let total = task.size;
            let stream = tokio_util::io::ReaderStream::new(source).map(move |chunk| {
                if let Ok(bytes) = &chunk {
                    done += bytes.len() as u64;
                    advance_transfer(&progress_app, &history, &key, (done, total), "传输中");
                }
                chunk
            });
            let request = self
                .client
                .put(format!("{}/api/company/files/{id}/content", ctx.server))
                .bearer_auth(&active.token)
                .query(&[
                    ("name", task.name.clone()),
                    ("size", task.size.to_string()),
                    ("expectedUploadVersion", job.version.to_string()),
                ])
                .header("content-length", task.size)
                .timeout(Duration::from_secs(120))
                .body(reqwest::Body::wrap_stream(stream));
            let response = tokio::select! {
                biased;
                _ = cancel.cancelled() => return Err("上传已停止，结果需查询原任务；不会自动重试。".into()),
                response = request.send() => response.map_err(|_| "上传中断或回执丢失，请查询原任务结果。")?,
            };
            let file: CompanyMetadata = self
                .check(response)
                .await?
                .json()
                .await
                .map_err(|_| "上传结果无法读取，请查询原任务。")?;
            if !file.upload_matches(id, &task.name, task.size) {
                return Err("上传结果与原任务不符，源副本已保留。".into());
            }
            self.with_upload_authority(ctx, job.version, cancel, || Ok(file.file))?
        };
        self.with_account(ctx, || {
            fs::remove_dir_all(self.upload_dir(id)?).map_err(io_error)?;
            Ok(())
        })?;
        let _ = app.emit("company-files-changed", ctx.epoch);
        Ok(file)
    }
    fn finish_upload(
        &self,
        app: &tauri::AppHandle,
        job: &UploadJob,
        result: Result<()>,
        prepared: bool,
    ) {
        let key = transfer_key(job.ctx.epoch, "upload", &job.id);
        self.company_transfers.lock().unwrap().remove(&key);
        let _ = self.with_account(&job.ctx, || {
            let mut history = self.company_history.lock().unwrap();
            if let Some(record) = history.get_mut(&key) {
                record.status = if result.is_ok() {
                    "已完成"
                } else if prepared {
                    "待核对"
                } else if job.cancel.is_cancelled() {
                    "已停止"
                } else {
                    "准备失败"
                }
                .into();
                if result.is_ok() {
                    record.done = record.total;
                }
                record.message = result.err().unwrap_or_default();
                record.revision += 1;
                let _ = app.emit("company-transfer", record.clone());
            }
            Ok(())
        });
    }
    #[cfg(test)]
    fn check_company_upload_version(&self, ctx: &AccountContext, version: u64) -> Result<()> {
        self.with_upload_authority(ctx, version, &ctx.cancel, || Ok(()))
    }
    /// 授权复核和提交只持短锁，撤权再授权也不能接收旧版本任务。
    fn with_upload_authority<T>(
        &self,
        ctx: &AccountContext,
        version: u64,
        cancel: &CancellationToken,
        work: impl FnOnce() -> Result<T>,
    ) -> Result<T> {
        let inner = self.inner.lock().unwrap();
        let active = inner
            .active
            .as_ref()
            .filter(|a| {
                a.epoch == ctx.epoch
                    && a.authorization.user.id == ctx.user_id
                    && a.connection.server == ctx.server
                    && !a.cancel.is_cancelled()
            })
            .ok_or("账号已变化，旧上传结果已丢弃。")?;
        let user = &active.authorization.user;
        if cancel.is_cancelled()
            || active.offline
            || !active.authorization.capabilities.company_files
            || user.disabled
            || user.must_change_password
            || user.company_file_permissions.upload_version != version
            || (!user.admin
                && (!active.authorization.capabilities.company_file_permissions
                    || !user.company_file_permissions.can_upload))
        {
            return Err("上传已停止或授权已变化；已固化的源副本保留，可查询原任务。".into());
        }
        work()
    }
    pub async fn company_download(
        &self,
        app: &tauri::AppHandle,
        epoch: u64,
        id: &str,
    ) -> Result<(wire::CompanyFile, PathBuf)> {
        let _activity = crate::update_activity::Activity::enter()?;
        if uuid::Uuid::parse_str(id).is_err() {
            return Err("云文件编号无效。".into());
        }
        let ctx = self.company_context(epoch, wire::CompanyOperation::Read)?;
        let active = self.active()?;
        self.with_account(&ctx, || Ok(()))?;
        let mut file: wire::CompanyFile = self
            .request(&active, Method::GET, &format!("company/files/{id}"), None)
            .await?;
        if file.deleted_at.is_some() {
            return Err("云文件已回收，请重新选择。".into());
        }
        let mut path = self.company_cache_path(&active, id, &file.name);
        let key = format!("{}:company:{id}", ctx.epoch);
        let lock = self
            .download_locks
            .lock()
            .unwrap()
            .entry(key.clone())
            .or_insert_with(|| Arc::new(AsyncMutex::new(())))
            .clone();
        let _lock = lock.lock().await;
        self.with_account(&ctx, || self.reuse_company_cache(&active, &file))?;
        if ready_path(&path) {
            self.company_progress(
                app,
                &ctx,
                (id, &file.name),
                "download",
                (file.size, file.size),
                "已完成",
            );
            return self.with_account(&ctx, || Ok((file, path)));
        }
        let cancel = ctx.cancel.child_token();
        self.company_transfers
            .lock()
            .unwrap()
            .insert(key.clone(), cancel.clone());
        self.company_progress(
            app,
            &ctx,
            (id, &file.name),
            "download",
            (0, file.size),
            "传输中",
        );
        let result = async {
            let _permit = tokio::select! {
                _ = cancel.cancelled() => return Err("下载已取消。".into()),
                permit = self.downloads.acquire() => permit.map_err(|_| "下载已关闭。")?,
            };
            let route = format!("company/files/{id}/content");
            let response = tokio::select! {
                _ = cancel.cancelled() => return Err("下载已取消。".into()),
                response = self.response(&active, Method::GET, &route, None) => response?,
            };
            fs::create_dir_all(self.dir.join("temp")).map_err(io_error)?;
            let temp = tempfile::NamedTempFile::new_in(self.dir.join("temp")).map_err(io_error)?;
            let mut target = tokio::fs::File::from_std(temp.reopen().map_err(io_error)?);
            let mut stream = response.bytes_stream();
            let mut done = 0;
            loop {
                let chunk = tokio::select! {
                    _ = cancel.cancelled() => return Err("下载已取消。".into()),
                    chunk = stream.next() => chunk,
                };
                let Some(chunk) = chunk else { break };
                let chunk = chunk.map_err(|_| "云文件下载中断。")?;
                done += chunk.len() as u64;
                if done > file.size || done > LIMIT {
                    return Err("云文件大小不符。".into());
                }
                target.write_all(&chunk).await.map_err(io_error)?;
                self.company_progress(
                    app,
                    &ctx,
                    (id, &file.name),
                    "download",
                    (done, file.size),
                    "传输中",
                );
            }
            target.sync_all().await.map_err(io_error)?;
            drop(target);
            if done != file.size || done == 0 {
                return Err("云文件未完整下载。".into());
            }
            file = self
                .request(&active, Method::GET, &format!("company/files/{id}"), None)
                .await?;
            if file.deleted_at.is_some() {
                return Err("云文件已回收，下载结果已丢弃。".into());
            }
            path = self.company_cache_path(&active, id, &file.name);
            if cancel.is_cancelled() {
                return Err("下载已取消。".into());
            }
            self.with_account(&ctx, || {
                fs::create_dir_all(path.parent().unwrap()).map_err(io_error)?;
                temp.persist(&path).map_err(io_error)?;
                save_json(&path.with_extension("ready.json"), &done)
            })
        }
        .await;
        self.company_transfers.lock().unwrap().remove(&key);
        self.company_progress(
            app,
            &ctx,
            (id, &file.name),
            "download",
            (if result.is_ok() { file.size } else { 0 }, file.size),
            if result.is_ok() {
                "已完成"
            } else if cancel.is_cancelled() {
                "已停止"
            } else {
                "下载失败"
            },
        );
        result?;
        let _ = app.emit("company-files-changed", epoch);
        self.with_account(&ctx, || Ok((file, path)))
    }
    pub fn cancel_company_transfer(&self, epoch: u64, id: &str) -> Result<()> {
        let active = self.active()?;
        if active.epoch != epoch {
            return Err("账号已变化，旧传输任务已失效。".into());
        }
        let tasks = self.company_transfers.lock().unwrap();
        for key in [
            format!("{epoch}:upload:{id}"),
            format!("{epoch}:company:{id}"),
        ] {
            if let Some(cancel) = tasks.get(&key) {
                cancel.cancel();
            }
        }
        Ok(())
    }
    pub async fn company_manage(
        &self,
        app: &tauri::AppHandle,
        epoch: u64,
        id: String,
        operation: String,
        confirm_name: String,
    ) -> Result<()> {
        if uuid::Uuid::parse_str(&id).is_err() {
            return Err("云文件编号无效。".into());
        }
        let required = match operation.as_str() {
            "recycle" => wire::CompanyOperation::Recycle,
            "restore" => wire::CompanyOperation::Restore,
            "purge" => wire::CompanyOperation::Purge,
            _ => return Err("云文件操作无效。".into()),
        };
        let ctx = self.company_context(epoch, required)?;
        let active = self.active()?;
        self.with_account(&ctx, || Ok(()))?;
        let (method, path) = match operation.as_str() {
            "recycle" | "restore" => (Method::POST, format!("company/files/{id}/{operation}")),
            "purge" => (Method::DELETE, format!("company/files/{id}")),
            _ => return Err("云文件操作无效。".into()),
        };
        let response = self
            .response(
                &active,
                method,
                &path,
                if operation == "purge" {
                    Some(serde_json::json!({"confirmName":confirm_name}))
                } else {
                    None
                },
            )
            .await?;
        self.check(response).await?;
        self.with_account(&ctx, || Ok(()))?;
        let _ = app.emit("company-files-changed", epoch);
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::super::tests::setup;
    use super::*;
    use std::path::Path;

    fn cached_catalog(client: &TeamClient) -> (Active, wire::CompanyFiles, PathBuf) {
        let active = {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.authorization.capabilities.company_files = true;
            active.clone()
        };
        let file: wire::CompanyFile = serde_json::from_value(serde_json::json!({
            "assetId":local::id(), "name":"缓存资料.pdf", "size":4,
            "uploadedBy":active.authorization.user.id, "uploaderName":"上传者", "createdAt":1, "deletedAt":null
        }))
        .unwrap();
        let path = client.company_cache_path(&active, &file.asset_id, &file.name);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, b"data").unwrap();
        save_json(&path.with_extension("ready.json"), &4_u64).unwrap();
        let catalog = wire::CompanyFiles {
            version: 3,
            files: vec![file],
        };
        save_json(&client.company_catalog_path(&active), &catalog).unwrap();
        client.restore_company_catalog();
        (active, catalog, path)
    }

    fn catalog_response(client: &TeamClient, status: u16, body: String) -> Result<CompanyView> {
        use std::net::TcpListener;
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.connection.server = format!("http://{}", listener.local_addr().unwrap());
            active.offline = false;
        }
        let server = std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut request = [0; 4096];
            let size = stream.read(&mut request).unwrap();
            assert!(String::from_utf8_lossy(&request[..size]).starts_with("GET /api/company/files"));
            write!(stream, "HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}", body.len()).unwrap();
        });
        let result = tokio::runtime::Runtime::new()
            .unwrap()
            .block_on(client.company_view(1, false, String::new()));
        server.join().unwrap();
        result
    }

    #[test]
    fn offline_catalog_restores_names_filters_locally_and_checks_complete_bytes() {
        let (_dir, client, _) = setup();
        let (_, catalog, path) = cached_catalog(&client);
        *client.company_names.lock().unwrap() = CompanyNames::default();
        client.restore_company_catalog();
        let runtime = tokio::runtime::Runtime::new().unwrap();
        let view = runtime
            .block_on(client.company_view(1, false, "缓存".into()))
            .unwrap();
        assert!(view.offline);
        assert_eq!(view.files.len(), 1);
        assert!(view.files[0].cached);
        assert_eq!(view.files[0].file.name, catalog.files[0].name);
        assert!(runtime
            .block_on(client.company_view(1, false, "不存在".into()))
            .unwrap()
            .files
            .is_empty());
        assert!(runtime
            .block_on(client.company_view(2, false, String::new()))
            .is_err());
        assert!(runtime
            .block_on(client.company_view(1, true, String::new()))
            .is_err());
        fs::write(path, b"partial").unwrap();
        assert!(
            !runtime
                .block_on(client.company_view(1, false, String::new()))
                .unwrap()
                .files[0]
                .cached
        );
    }

    #[test]
    fn offline_linked_cache_requires_the_current_usable_account() {
        let (_dir, client, _) = setup();
        let (_, catalog, path) = cached_catalog(&client);
        let file = &catalog.files[0];
        assert_eq!(
            client
                .linked_company_existing_cache(1, &file.asset_id, &file.name)
                .unwrap(),
            Some(path)
        );
        assert!(client
            .company_context(1, wire::CompanyOperation::Read)
            .is_err());
        assert!(client
            .linked_company_existing_cache(2, &file.asset_id, &file.name)
            .is_err());
        for condition in ["disabled", "password", "other-account"] {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.authorization.user.disabled = condition == "disabled";
            active.authorization.user.must_change_password = condition == "password";
            if condition == "other-account" {
                active.authorization.user.id = local::id();
            }
            drop(inner);
            let result = client.linked_company_existing_cache(1, &file.asset_id, &file.name);
            if condition == "other-account" {
                assert!(result.unwrap().is_none());
            } else {
                assert!(result.is_err());
            }
        }
    }

    #[test]
    fn offline_cache_use_and_delivery_do_not_enable_cloud_operations() {
        let (_dir, client, _) = setup();
        let (_, catalog, path) = cached_catalog(&client);
        let ctx = client.company_cached_context(1).unwrap();
        assert_eq!(
            client
                .with_account(&ctx, || fs::read(&path).map_err(io_error))
                .unwrap(),
            b"data"
        );
        client.begin_company_delivery(1).unwrap();
        assert_eq!(client.inner.lock().unwrap().delivery_company, Some(1));
        client.end_delivery();
        assert!(client.begin_company_delivery(2).is_err());
        assert!(client
            .linked_company_existing_cache(1, &local::id(), "未缓存.pdf")
            .unwrap()
            .is_none());
        for operation in [
            wire::CompanyOperation::Read,
            wire::CompanyOperation::Upload,
            wire::CompanyOperation::Recycle,
        ] {
            assert!(client.company_context(1, operation).is_err());
        }
        for condition in ["disabled", "password", "capability", "cancelled"] {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.authorization.user.disabled = condition == "disabled";
            active.authorization.user.must_change_password = condition == "password";
            active.authorization.capabilities.company_files = condition != "capability";
            if condition == "cancelled" {
                active.cancel.cancel();
            }
            drop(inner);
            assert!(client.begin_company_delivery(1).is_err());
            assert!(client.company_cached_context(1).is_err());
            assert!(client
                .linked_company_existing_cache(
                    1,
                    &catalog.files[0].asset_id,
                    &catalog.files[0].name
                )
                .is_err());
        }
        assert!(client.with_account(&ctx, || Ok(())).is_err());
    }

    #[test]
    fn persisted_catalog_cannot_be_restored_for_another_account_or_server() {
        let (_dir, client, _) = setup();
        let (original, _, _) = cached_catalog(&client);
        for other_server in [false, true] {
            {
                let mut inner = client.inner.lock().unwrap();
                let active = inner.active.as_mut().unwrap();
                *active = original.clone();
                active.epoch = 2;
                if other_server {
                    active.connection.partition = local::id();
                    active.connection.server = "https://other.example".into();
                } else {
                    active.authorization.user.id = local::id();
                }
            }
            *client.company_names.lock().unwrap() = CompanyNames::default();
            client.restore_company_catalog();
            assert!(client.offline_company_view(2, "").is_err());
            assert!(client.cached_company_files(2).is_none());
        }
    }

    #[test]
    fn catalog_server_errors_use_snapshot_but_authentication_errors_do_not() {
        let (_dir, client, _) = setup();
        cached_catalog(&client);
        let view = catalog_response(&client, 503, "{}".into()).unwrap();
        assert!(view.offline);
        assert_eq!(view.files.len(), 1);
        assert!(
            catalog_response(&client, 200, "{\"version\":".into())
                .unwrap()
                .offline
        );
        let error = catalog_response(&client, 401, "{\"message\":\"登录已失效\"}".into())
            .err()
            .unwrap();
        assert!(error.starts_with("401:"), "{error}");
    }

    #[test]
    fn successful_complete_catalog_is_persisted_for_the_next_offline_load() {
        let (_dir, client, _) = setup();
        let (active, mut catalog, _) = cached_catalog(&client);
        catalog.version += 1;
        catalog.files[0].name = "新目录名称.pdf".into();
        let view =
            catalog_response(&client, 200, serde_json::to_string(&catalog).unwrap()).unwrap();
        assert!(!view.offline);
        let stored: wire::CompanyFiles = load_json(&client.company_catalog_path(&active)).unwrap();
        assert_eq!(stored.version, catalog.version);
        assert_eq!(stored.files[0].name, catalog.files[0].name);
    }

    #[test]
    fn purged_company_cache_keeps_identity_and_requires_complete_local_bytes() {
        let (_dir, client, mut ctx) = setup();
        let id = local::id();
        ctx.company_asset_ids = vec![id.clone()];
        let path = client.company_cache_path(&ctx.active, &id, "改名后.pdf");
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, b"data").unwrap();
        assert!(client
            .existing_company_cache(&ctx.active, &id, "旧快照.pdf")
            .is_none());
        save_json(&path.with_extension("ready.json"), &4_u64).unwrap();
        assert_eq!(client.asset_path(&ctx, &id, "旧快照.pdf", false), path);
        let mut other = ctx.active.clone();
        other.authorization.user.id = "other".into();
        assert!(client
            .existing_company_cache(&other, &id, "旧快照.pdf")
            .is_none());
        assert!(client
            .existing_company_cache(&ctx.active, "../outside", "旧快照.pdf")
            .is_none());
        fs::write(&path, b"partial").unwrap();
        assert!(client
            .existing_company_cache(&ctx.active, &id, "旧快照.pdf")
            .is_none());
    }

    #[test]
    fn renamed_upload_uses_original_name_and_cache_reuses_only_same_identity() {
        let (_dir, client, ctx) = setup();
        let id = local::id();
        let metadata: CompanyMetadata = serde_json::from_value(serde_json::json!({
            "assetId":id,"name":"新名称.pdf","originalName":"原名称.pdf","size":4,
            "uploadedBy":"owner","uploaderName":"上传者","createdAt":1,"deletedAt":null
        }))
        .unwrap();
        assert!(metadata.upload_matches(&id, "原名称.pdf", 4));
        assert!(!metadata.upload_matches(&id, "新名称.pdf", 4));
        assert!(!metadata.upload_matches(&id, "原名称.pdf", 5));
        let old = client.company_cache_path(&ctx.active, &id, "原名称.pdf");
        fs::create_dir_all(old.parent().unwrap()).unwrap();
        fs::write(&old, b"data").unwrap();
        save_json(&old.with_extension("ready.json"), &4_u64).unwrap();
        let new = client
            .reuse_company_cache(&ctx.active, &metadata.file)
            .unwrap();
        assert_eq!(fs::read(new).unwrap(), b"data");
        assert!(old.is_file(), "旧路径可能正在投递，不能删除");
        let mut other = ctx.active.clone();
        other.authorization.user.id = "other".into();
        assert!(!client
            .reuse_company_cache(&other, &metadata.file)
            .unwrap()
            .exists());
        assert!(!client
            .reuse_company_named_cache(&ctx.active, &local::id(), "新名称.pdf", Some(4))
            .unwrap()
            .exists());
    }

    fn authorize_uploads(client: &TeamClient) -> UploadJob {
        {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.offline = false;
            active.authorization.capabilities.company_files = true;
            active.authorization.capabilities.company_file_permissions = true;
            active
                .authorization
                .user
                .company_file_permissions
                .can_upload = true;
        }
        let ctx = client
            .company_context(1, wire::CompanyOperation::Upload)
            .unwrap();
        UploadJob {
            id: local::id(),
            name: "受控源.pdf".into(),
            version: 0,
            cancel: ctx.cancel.child_token(),
            ctx,
        }
    }

    #[test]
    fn upload_preparation_rejects_each_invalid_source_without_touching_it() {
        let (dir, client, _) = setup();
        let job = authorize_uploads(&client);
        let empty = dir.path().join("空文件.pdf");
        fs::write(&empty, []).unwrap();
        let large = dir.path().join("超限.pdf");
        fs::File::create(&large)
            .unwrap()
            .set_len(LIMIT + 1)
            .unwrap();
        for (path, reason) in [
            (dir.path().to_owned(), "文件夹"),
            (empty.clone(), "1字节至128MB"),
            (large.clone(), "1字节至128MB"),
            (dir.path().join("不存在.pdf"), "不存在"),
        ] {
            assert!(client
                .prepare_upload_source(&job, path.to_str().unwrap())
                .err()
                .unwrap()
                .contains(reason));
            assert!(!client.upload_dir(&job.id).unwrap().exists());
        }
        assert_eq!(fs::metadata(empty).unwrap().len(), 0);
        assert_eq!(fs::metadata(large).unwrap().len(), LIMIT + 1);
        let source = dir.path().join("有效.pdf");
        fs::write(&source, "有效源文件").unwrap();
        let task = client
            .prepare_upload_source(&job, source.to_str().unwrap())
            .unwrap();
        fs::write(&source, "源文件后来修改").unwrap();
        assert_eq!(
            fs::read_to_string(client.upload_dir(&task.id).unwrap().join("content")).unwrap(),
            "有效源文件"
        );
        assert_eq!(fs::read_to_string(source).unwrap(), "源文件后来修改");
    }

    #[test]
    fn legacy_preparation_waiting_for_a_slot_keeps_its_original_upload_version() {
        let (dir, client, _) = setup();
        authorize_uploads(&client);
        let source = dir.path().join("等待名额.pdf");
        fs::write(&source, "保留源文件").unwrap();
        tokio::runtime::Runtime::new().unwrap().block_on(async {
            let permits = client.uploads.acquire_many(2).await.unwrap();
            let mut pending = Box::pin(
                client.prepare_company_upload_async(1, source.to_string_lossy().into_owned()),
            );
            assert!(futures_util::poll!(pending.as_mut()).is_pending());
            client
                .inner
                .lock()
                .unwrap()
                .active
                .as_mut()
                .unwrap()
                .authorization
                .user
                .company_file_permissions
                .upload_version += 1;
            drop(permits);
            assert!(pending.await.is_err());
        });
        assert_eq!(fs::read_to_string(source).unwrap(), "保留源文件");
        assert!(client
            .upload_tasks(
                &client
                    .company_context(1, wire::CompanyOperation::Upload)
                    .unwrap()
            )
            .is_empty());
    }

    #[test]
    fn preparation_does_not_hold_account_lock_and_rechecks_cancel_or_authority_before_commit() {
        let (dir, client, _) = setup();
        let source = dir.path().join("受控源.pdf");
        fs::File::create(&source).unwrap().set_len(LIMIT).unwrap();
        for invalidation in ["cancel", "version", "offline", "account"] {
            let job = authorize_uploads(&client);
            let output = client.upload_dir(&job.id).unwrap().join("content");
            let worker_client = client.clone();
            let worker_job = job.clone();
            let worker_source = source.clone();
            let worker = std::thread::spawn(move || {
                worker_client.prepare_upload_source(&worker_job, worker_source.to_str().unwrap())
            });
            let deadline = std::time::Instant::now() + Duration::from_secs(5);
            while !output.exists() {
                assert!(std::time::Instant::now() < deadline, "未观察到后台复制");
                std::thread::yield_now();
            }
            // 文件已经开始复制时仍能立即获得账号锁，账号变动不会被长时间 I/O 阻塞。
            let mut inner = client.inner.try_lock().expect("复制不能持有账号锁");
            let active = inner.active.as_mut().unwrap();
            match invalidation {
                "cancel" => job.cancel.cancel(),
                "version" => {
                    active
                        .authorization
                        .user
                        .company_file_permissions
                        .upload_version += 1
                }
                "offline" => active.offline = true,
                "account" => active.authorization.user.id = local::id(),
                _ => unreachable!(),
            }
            drop(inner);
            assert!(worker.join().unwrap().is_err(), "旧准备结果不能提交");
            assert!(!output.parent().unwrap().exists());
            assert_eq!(fs::metadata(&source).unwrap().len(), LIMIT);
            client
                .inner
                .lock()
                .unwrap()
                .active
                .as_mut()
                .unwrap()
                .authorization
                .user
                .company_file_permissions
                .upload_version = 0;
        }
    }

    #[test]
    fn restart_cleans_only_uncommitted_copies_and_keeps_pending_tasks() {
        let (dir, client, _) = setup();
        let job = authorize_uploads(&client);
        let source = dir.path().join("受控源.pdf");
        fs::write(&source, "保留源文件").unwrap();
        let task = client
            .prepare_upload_source(&job, source.to_str().unwrap())
            .unwrap();
        let incomplete = client.upload_dir(&local::id()).unwrap();
        fs::create_dir_all(&incomplete).unwrap();
        fs::write(incomplete.join("content"), "半份副本").unwrap();
        let unknown = client.dir.join("company-uploads/用户目录");
        fs::create_dir_all(&unknown).unwrap();
        fs::write(unknown.join("content"), "未知文件").unwrap();
        client.clean_uncommitted_uploads().unwrap();
        assert!(!incomplete.exists());
        assert!(unknown.join("content").exists());
        assert!(client
            .upload_dir(&task.id)
            .unwrap()
            .join("task.json")
            .exists());
        assert_eq!(client.company_transfer_view(1).unwrap()[0].status, "待核对");
        assert!(source.exists());
    }

    #[test]
    fn windows_names_preserve_chinese_extensions_and_valid_length() {
        assert_eq!(safe_filename("价格表.xlsx"), "价格表.xlsx");
        assert_eq!(safe_filename("CON.txt"), "_CON.txt");
        assert_eq!(safe_filename("资料:价格?.pdf. "), "资料_价格_.pdf");
        assert_eq!(safe_filename(".. "), "文件");
        for name in [
            format!("{}.xlsx", "长资料".repeat(100)),
            format!("{}.pdf", "📁".repeat(110)),
        ] {
            let safe = safe_filename(&name);
            assert!(safe.encode_utf16().count() <= 200);
            assert_eq!(Path::new(&safe).extension(), Path::new(&name).extension());
        }
    }

    #[test]
    fn company_permissions_and_upload_versions_are_checked_without_group_roles() {
        let (_dir, client, _) = setup();
        {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.offline = false;
            active.authorization.capabilities.company_files = true;
            active.authorization.teams[0].role = wire::Role::Manager;
            active
                .authorization
                .user
                .company_file_permissions
                .can_upload = true;
        }
        // 旧版闪云不能因为缺省字段或库管理员身份被误判为已委派上传。
        assert!(client
            .company_context(1, wire::CompanyOperation::Read)
            .is_ok());
        assert!(client
            .company_context(1, wire::CompanyOperation::Upload)
            .is_err());
        {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.authorization.capabilities.company_file_permissions = true;
            active
                .authorization
                .user
                .company_file_permissions
                .upload_version = 1;
        }
        let ctx = client
            .company_context(1, wire::CompanyOperation::Upload)
            .unwrap();
        assert!(client
            .company_context(1, wire::CompanyOperation::Recycle)
            .is_err());
        assert!(client
            .company_context(1, wire::CompanyOperation::Purge)
            .is_err());
        assert!(client.check_company_upload_version(&ctx, 1).is_ok());
        {
            let mut inner = client.inner.lock().unwrap();
            let permissions = &mut inner
                .active
                .as_mut()
                .unwrap()
                .authorization
                .user
                .company_file_permissions;
            permissions.can_restore = true;
            permissions.version = 2;
        }
        assert!(client.check_company_upload_version(&ctx, 1).is_ok());
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .authorization
            .user
            .company_file_permissions
            .upload_version = 3;
        assert!(client.check_company_upload_version(&ctx, 1).is_err());
        assert!(client
            .company_context(0, wire::CompanyOperation::Read)
            .is_err());
    }

    #[test]
    fn transfer_snapshots_and_clear_preserve_unresolved_source_and_current_account() {
        let (dir, client, _) = setup();
        {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.offline = false;
            active.authorization.capabilities.company_files = true;
            active.authorization.capabilities.company_file_permissions = true;
            active
                .authorization
                .user
                .company_file_permissions
                .can_upload = true;
        }
        let source = dir.path().join("独立源.pdf");
        fs::write(&source, "源文件内容").unwrap();
        let task = client
            .prepare_company_upload(1, source.to_str().unwrap())
            .unwrap();
        let snapshot = client.company_transfer_view(1).unwrap();
        assert_eq!(snapshot[0].name, "独立源.pdf");
        assert_eq!(snapshot[0].status, "待核对");
        let record = CompanyTransfer {
            session_epoch: 1,
            asset_id: "download".into(),
            name: "下载.docx".into(),
            direction: "download".into(),
            done: 3,
            total: 3,
            status: "已完成".into(),
            revision: 1,
            message: String::new(),
        };
        {
            let mut history = client.company_history.lock().unwrap();
            history.insert("finished".into(), record.clone());
            history.insert(
                "running".into(),
                CompanyTransfer {
                    asset_id: "running".into(),
                    status: "传输中".into(),
                    ..record.clone()
                },
            );
            history.insert(
                "other-account".into(),
                CompanyTransfer {
                    session_epoch: 0,
                    ..record
                },
            );
        }
        // 本地管理记录允许离线清空；撤权也必须保留待核对源副本。
        {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.offline = true;
            active
                .authorization
                .user
                .company_file_permissions
                .can_upload = false;
        }
        let records = client.clear_company_transfers(1, "download").unwrap();
        assert_eq!(records.len(), 2);
        assert!(!records.iter().any(|r| r.asset_id == "download"));
        assert_eq!(
            client.clear_company_transfers(1, "upload").unwrap().len(),
            2
        );
        assert!(client
            .upload_dir(&task.id)
            .unwrap()
            .join("content")
            .is_file());
        assert!(client.clear_company_transfers(0, "download").is_err());
        assert!(client.company_transfer_view(0).is_err());
        assert!(source.is_file());
    }
}
