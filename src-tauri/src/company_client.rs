//! 云文件网络任务使用账号世代和固定任务 ID；上传源文件先固化，未知回执只查询原任务。
//! 云文件缓存随账号退出清理，上传待核对任务独立保存，个人副本不放在此缓存目录。
use super::*;

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
            return Err("服务端尚不支持云文件，请升级 Hub。".into());
        }
        if active.authorization.user.must_change_password
            || active.authorization.user.disabled
            || active.offline
        {
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
    pub(super) fn company_cache_path(&self, active: &Active, id: &str, name: &str) -> PathBuf {
        self.dir
            .join(&active.connection.partition)
            .join("company")
            .join(&active.authorization.user.id)
            .join(id)
            .join(safe_filename(name))
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
                });
            advance_transfer(app, &self.company_history, &key, bytes, status);
            Ok(())
        });
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
                    || (direction == "upload" && r.status != "已完成")
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
        let ctx = self.company_context(
            epoch,
            if recycled {
                wire::CompanyOperation::RecycleBin
            } else {
                wire::CompanyOperation::Read
            },
        )?;
        let active = self.active()?;
        self.with_account(&ctx, || Ok(()))?;
        let url = Url::parse_with_params(
            &format!("{}/api/company/files", ctx.server),
            &[("recycled", recycled.to_string()), ("q", query)],
        )
        .map_err(|_| "云文件查询无效。")?;
        let request = self
            .client
            .get(url)
            .bearer_auth(&active.token)
            .timeout(Duration::from_secs(30));
        let response = tokio::select! {
            _ = ctx.cancel.cancelled() => return Err("账号已变化，目录查询已取消。".into()),
            response = request.send() => response.map_err(|_| "云文件目录加载失败，请检查网络。")?,
        };
        let catalog: wire::CompanyFiles = self
            .check(response)
            .await?
            .json()
            .await
            .map_err(|_| "云文件目录无法读取。")?;
        self.with_account(&ctx, || {
            let files = catalog
                .files
                .into_iter()
                .map(|file| {
                    let cached =
                        ready_path(&self.company_cache_path(&active, &file.asset_id, &file.name));
                    CompanyEntry { file, cached }
                })
                .collect();
            let uploads = self.upload_tasks(&ctx);
            Ok(CompanyView {
                version: catalog.version,
                files,
                uploads,
            })
        })
    }
    fn upload_dir(&self, id: &str) -> Result<PathBuf> {
        if uuid::Uuid::parse_str(id).is_err() {
            return Err("上传任务编号无效。".into());
        }
        Ok(self.dir.join("company-uploads").join(id))
    }
    pub fn prepare_company_upload(&self, epoch: u64, path: &str) -> Result<CompanyUpload> {
        let ctx = self.company_context(epoch, wire::CompanyOperation::Upload)?;
        let source = Path::new(path);
        let mut options = fs::OpenOptions::new();
        options.read(true);
        // Windows 复制期间拒绝写入及替换源文件，避免正在保存的文档混入同一副本。
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            options.share_mode(1);
        }
        let mut input = options
            .open(source)
            .map_err(|_| "文件无法读取或正在被修改，请稍后再试。")?;
        let size = input.metadata().map_err(|_| "文件无法读取。")?.len();
        if size == 0 || size > LIMIT {
            return Err("单文件需为1字节至128MB。".into());
        }
        let task = CompanyUpload {
            id: local::id(),
            name: source
                .file_name()
                .ok_or("文件名无效。")?
                .to_string_lossy()
                .into_owned(),
            size,
            server: ctx.server.clone(),
            user_id: ctx.user_id.clone(),
        };
        let dir = self.upload_dir(&task.id)?;
        fs::create_dir_all(&dir).map_err(io_error)?;
        let result = self.with_account(&ctx, || {
            let mut output = fs::File::create(dir.join("content")).map_err(io_error)?;
            if std::io::copy(&mut input, &mut output).map_err(io_error)? != size {
                return Err("文件在复制期间变化，请重新选择。".into());
            }
            output.sync_all().map_err(io_error)?;
            save_json(&dir.join("task.json"), &task)?;
            Ok(task.clone())
        });
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
        // 查询既有登记属于读取，撤回上传授权后也不能丢失待核对任务。
        let ctx = self.company_context(epoch, wire::CompanyOperation::Read)?;
        let active = self.active()?;
        self.with_account(&ctx, || Ok(()))?;
        let dir = self.upload_dir(&id)?;
        let task = load_json::<CompanyUpload>(&dir.join("task.json")).ok_or("上传任务不存在。")?;
        if task.id != id || task.server != ctx.server || task.user_id != ctx.user_id {
            return Err("上传任务不属于当前账号。".into());
        }
        let key = format!("{epoch}:upload:{id}");
        let cancel = ctx.cancel.child_token();
        {
            let mut tasks = self.company_transfers.lock().unwrap();
            if tasks.contains_key(&key) {
                return Err("该文件正在上传。".into());
            }
            tasks.insert(key.clone(), cancel.clone());
        }
        self.company_progress(
            app,
            &ctx,
            (&id, &task.name),
            "upload",
            (0, task.size),
            "传输中",
        );
        let result = async {
            // 只有明确不存在才传输；网络错误、权限错误和永久删除标记必须停止。
            let query = self.client.get(format!("{}/api/company/files/{id}", ctx.server))
                .bearer_auth(&active.token).timeout(Duration::from_secs(30));
            let response = tokio::select! {
                _ = cancel.cancelled() => return Err("上传已停止，请查询原任务。".into()),
                response = query.send() => response.map_err(|_| "上传结果查询失败，不会重新上传。")?,
            };
            if response.status() != reqwest::StatusCode::NOT_FOUND {
                let file = self.check(response).await?.json::<wire::CompanyFile>().await
                    .map_err(|_| "上传结果无法读取。")?;
                if file.asset_id != id || file.name != task.name || file.size != task.size {
                    return Err("上传结果与原任务不符，源副本已保留。".into());
                }
                return self.with_account(&ctx, || Ok(file));
            }
            if !retry { return Err("原任务尚未登记，源副本已保留；获得上传权限后可明确继续。".into()); }
            self.company_context(epoch, wire::CompanyOperation::Upload)?;
            let upload_version = self.active()?.authorization.user.company_file_permissions.upload_version;
            self.check_company_upload_version(&ctx, upload_version)?;
            if cancel.is_cancelled() { return Err("上传授权已变化，请查询原任务。".into()); }
            let file = tokio::fs::File::open(dir.join("content")).await.map_err(io_error)?;
            if file.metadata().await.map_err(io_error)?.len() != task.size {
                return Err("上传源副本不完整，请重新选择文件。".into());
            }
            let mut done = 0;
            let progress_app = app.clone();
            let progress_history = self.company_history.clone();
            let progress_key = key.clone();
            let total = task.size;
            let stream = tokio_util::io::ReaderStream::new(file).map(move |chunk| {
                if let Ok(bytes) = &chunk {
                    done += bytes.len() as u64;
                    advance_transfer(&progress_app, &progress_history, &progress_key, (done, total), "传输中");
                }
                chunk
            });
            let request = self.client.put(format!("{}/api/company/files/{id}/content", ctx.server))
                .bearer_auth(&active.token).query(&[("name", task.name.clone()), ("size", task.size.to_string()), ("expectedUploadVersion", upload_version.to_string())])
                .header("content-length", task.size).timeout(Duration::from_secs(120))
                .body(reqwest::Body::wrap_stream(stream));
            let response = tokio::select! {
                _ = cancel.cancelled() => return Err("上传已停止，结果需查询原任务；不会自动重试。".into()),
                response = request.send() => response.map_err(|_| "上传中断或回执丢失，请查询原任务结果。")?,
            };
            let file = self.check(response).await?.json().await
                .map_err(|_| "上传结果无法读取，请查询原任务。")?;
            self.check_company_upload_version(&ctx, upload_version)?;
            self.with_account(&ctx, || Ok(file))
        }.await;
        self.company_transfers.lock().unwrap().remove(&key);
        self.company_progress(
            app,
            &ctx,
            (&id, &task.name),
            "upload",
            (if result.is_ok() { task.size } else { 0 }, task.size),
            if result.is_ok() {
                "已完成"
            } else {
                "待核对"
            },
        );
        if result.is_ok() {
            self.with_account(&ctx, || {
                fs::remove_dir_all(&dir).map_err(io_error)?;
                Ok(())
            })?;
            let _ = app.emit("company-files-changed", epoch);
        }
        result
    }
    fn check_company_upload_version(&self, ctx: &AccountContext, version: u64) -> Result<()> {
        let inner = self.inner.lock().unwrap();
        let active = inner
            .active
            .as_ref()
            .filter(|a| {
                a.epoch == ctx.epoch
                    && a.authorization.user.id == ctx.user_id
                    && !a.cancel.is_cancelled()
            })
            .ok_or("账号已变化，旧上传结果已丢弃。")?;
        let user = &active.authorization.user;
        if user.disabled
            || user.must_change_password
            || user.company_file_permissions.upload_version != version
            || (!user.admin
                && (!active.authorization.capabilities.company_file_permissions
                    || !user.company_file_permissions.can_upload))
        {
            return Err("上传授权已变化，源副本已保留，请查询原任务。".into());
        }
        Ok(())
    }
    pub async fn company_download(
        &self,
        app: &tauri::AppHandle,
        epoch: u64,
        id: &str,
    ) -> Result<(wire::CompanyFile, PathBuf)> {
        if uuid::Uuid::parse_str(id).is_err() {
            return Err("云文件编号无效。".into());
        }
        let ctx = self.company_context(epoch, wire::CompanyOperation::Read)?;
        let active = self.active()?;
        self.with_account(&ctx, || Ok(()))?;
        let file: wire::CompanyFile = self
            .request(&active, Method::GET, &format!("company/files/{id}"), None)
            .await?;
        let path = self.company_cache_path(&active, id, &file.name);
        let key = format!("{}:company:{id}", ctx.epoch);
        let lock = self
            .download_locks
            .lock()
            .unwrap()
            .entry(key.clone())
            .or_insert_with(|| Arc::new(AsyncMutex::new(())))
            .clone();
        let _lock = lock.lock().await;
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
            let _: wire::CompanyFile = self
                .request(&active, Method::GET, &format!("company/files/{id}"), None)
                .await?;
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
        // 旧 Hub 不能因为缺省字段或库管理员身份被误判为已委派上传。
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
