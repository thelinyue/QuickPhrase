//! 数据根目录的定位与离线迁移。迁移在后台服务启动前执行，原库始终保留到新库验证成功。
//! 定位文件留在默认目录；持久化复制阶段与清理文件列表，防止中断后误删用户新放入的文件。
use crate::model::{external_path, id, Result};
use rusqlite::{backup::Backup, params, Connection, OpenFlags};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{
    collections::HashSet,
    fs,
    io::{Read, Write},
    path::{Path, PathBuf},
    time::Duration,
};

const LOCATOR: &str = "data-directory.json";
const DIRECTORIES: &[&str] = &[
    "images",
    "assets",
    "thumbnails",
    "temp",
    "teams",
    "transfer",
];

fn error(e: impl std::fmt::Display) -> String {
    format!("数据目录操作失败：{e}")
}

#[derive(Default, Clone, Serialize, Deserialize)]
/// 目录位置属于本机，不进入账号同步或备份；原子写入同时提交位置和恢复状态。
struct Location {
    current: Option<PathBuf>,
    pending: Option<Migration>,
    cleanup: Option<Cleanup>,
    #[serde(default)]
    message: String,
}
#[derive(Clone, Serialize, Deserialize)]
/// ready 只在完整复制验证后保存；临时目录发布后仍可据此核验并完成位置提交。
struct Migration {
    target: PathBuf,
    staging: PathBuf,
    ready: bool,
    #[serde(default)]
    files: Vec<PathBuf>,
}
#[derive(Clone, Serialize, Deserialize)]
/// 保存本次复制对应的旧文件集合，排除外部关联，重启清理不扩大到后来添加的文件。
struct Cleanup {
    source: PathBuf,
    files: Vec<PathBuf>,
}

#[derive(Clone, Serialize)]
pub struct DirectoryInfo {
    pub path: String,
    pub message: String,
}

/// 只管理应用根目录，不改变当前账号库的目录或身份。实例保存在 AppState 中串行处理请求。
pub struct DataDirectory {
    base: PathBuf,
    location: Location,
    pub show_result: bool,
}
impl DataDirectory {
    pub fn startup(base: &Path) -> Result<Self> {
        fs::create_dir_all(base).map_err(error)?;
        let base = canonical(base)?;
        let location = match fs::File::open(base.join(LOCATOR)) {
            Ok(file) => serde_json::from_reader(file).map_err(|_| {
                "数据目录定位配置损坏，未创建空库。请检查 data-directory.json。".to_owned()
            })?,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Location::default(),
            Err(e) => return Err(error(e)),
        };
        let mut this = Self {
            base,
            show_result: location.pending.is_some() || location.cleanup.is_some(),
            location,
        };
        this.check_current()?;
        if this.location.pending.is_some() {
            if let Err(message) = this.migrate() {
                // 必须先持久化取消请求，才允许原库继续写入，避免下次启动接管过时的复制结果。
                let pending = this.location.pending.take();
                this.location.message = format!("迁移失败，继续使用原目录。{message}");
                this.save()?;
                if let Some(pending) = pending {
                    if pending.ready && pending.target.join("shanyu.sqlite3").is_file() {
                        this.location.message.push_str(&format!(
                            " 新目录已生成副本但尚未启用，请保留或检查后清理：{}。",
                            external_path(&pending.target)
                        ));
                        this.save()?;
                    }
                    if valid_staging(&pending) {
                        if let Err(e) = remove_staging(&pending.staging) {
                            this.location.message.push_str(&format!(
                                " 临时副本未清理：{}。{e}",
                                external_path(&pending.staging)
                            ));
                            this.save()?;
                        }
                    }
                }
            }
        }
        this.check_current()?;
        if let Some(current) = this.location.current.as_mut() {
            // 旧定位记录可能保留8.3短名称；对外统一实际目录名，不改变目录身份。
            *current = canonical(current)?;
        }
        Ok(this)
    }
    pub fn root(&self) -> PathBuf {
        self.location.current.clone().unwrap_or(self.base.clone())
    }
    pub fn info(&self) -> DirectoryInfo {
        DirectoryInfo {
            path: external_path(&self.root()),
            message: self.location.message.clone(),
        }
    }
    fn check_current(&self) -> Result<()> {
        if let Some(dir) = &self.location.current {
            check_ancestors(dir)?;
            if !dir.is_dir() || !dir.join("shanyu.sqlite3").is_file() {
                return Err(format!(
                    "已设置的数据目录不可访问或数据库缺失：{}。请恢复该目录，未创建空库。",
                    external_path(dir)
                ));
            }
        }
        Ok(())
    }
    fn save(&self) -> Result<()> {
        let mut file = tempfile::NamedTempFile::new_in(&self.base).map_err(error)?;
        serde_json::to_writer_pretty(file.as_file_mut(), &self.location).map_err(error)?;
        file.as_file_mut().sync_all().map_err(error)?;
        file.persist(self.base.join(LOCATOR)).map_err(error)?;
        Ok(())
    }
    pub fn prepare(&mut self, target: &Path) -> Result<()> {
        if self.location.pending.is_some() || self.location.cleanup.is_some() {
            return Err("请先完成上次目录更换。".into());
        }
        let source = self.root();
        let target = validate_target(&source, target)?;
        // 提前检查迁移范围内的链接；实际复制在新进程中再次核验。
        inventory(&source)?;
        let mut probe = tempfile::NamedTempFile::new_in(&target)
            .map_err(|e| format!("新目录无法写入，请选择有写入权限的空文件夹：{e}"))?;
        probe.write_all(b"QuickPhrase").map_err(error)?;
        probe.as_file().sync_all().map_err(error)?;
        probe.close().map_err(error)?;
        let staging = target
            .parent()
            .ok_or("不能将磁盘根目录作为数据目录。")?
            .join(format!(".quickphrase-migration-{}", id()));
        let previous = self.location.clone();
        self.location.pending = Some(Migration {
            target,
            staging,
            ready: false,
            files: vec![],
        });
        self.location.message.clear();
        if let Err(e) = self.save() {
            self.location = previous;
            return Err(e);
        }
        Ok(())
    }
    fn migrate(&mut self) -> Result<()> {
        let source = self.root();
        let mut pending = self.location.pending.clone().unwrap();
        let was_ready = pending.ready;
        if !valid_staging(&pending) {
            return Err("迁移临时目录记录无效，未执行复制或清理。".into());
        }
        check_ancestors(&source)?;
        check_ancestors(&pending.target)?;
        if relative_under(&pending.target, &source).is_some()
            || relative_under(&source, &pending.target).is_some()
        {
            return Err("新旧数据目录不能相同或相互包含。".into());
        }
        if !pending.ready {
            validate_target(&source, &pending.target)?;
            remove_staging(&pending.staging)?;
            fs::create_dir(&pending.staging).map_err(error)?;
            let files = inventory(&source)?;
            for relative in &files {
                let from = source.join(relative);
                let to = pending.staging.join(relative);
                fs::create_dir_all(to.parent().unwrap()).map_err(error)?;
                if is_database(relative) {
                    let from = read_database(&from)?;
                    let mut to = Connection::open(&to).map_err(error)?;
                    Backup::new(&from, &mut to)
                        .map_err(error)?
                        .run_to_completion(256, Duration::from_millis(10), None)
                        .map_err(error)?;
                    relocate_database(&mut to, &source, &pending.target)?;
                    validate_database(&to)?;
                } else if !is_sidecar(relative) {
                    fs::copy(&from, &to).map_err(error)?;
                    if is_batch(relative) {
                        relocate_batch(&to, &source, &pending.target)?;
                    }
                    fs::OpenOptions::new()
                        .write(true)
                        .open(&to)
                        .map_err(error)?
                        .sync_all()
                        .map_err(error)?;
                }
            }
            let protected = protected_files(&source, &files)?;
            pending.files = files
                .into_iter()
                .filter(|p| !protected.contains(&path_key(p)))
                .collect();
            validate_copy(&source, &pending.staging, &pending.target)?;
            pending.ready = true;
            self.location.pending = Some(pending.clone());
            self.save()?;
        }
        if pending.staging.exists() {
            if was_ready {
                validate_copy(&source, &pending.staging, &pending.target)?;
            }
            if pending.target.exists() {
                validate_target(&source, &pending.target)?;
                // Windows 不允许重命名到已存在的目录；这里只移除重新确认过的空目录。
                fs::remove_dir(&pending.target).map_err(error)?;
            }
            fs::rename(&pending.staging, &pending.target).map_err(error)?;
        } else {
            // 覆盖“发布复制目录后、提交定位前”退出的情况。
            validate_copy(&source, &pending.target, &pending.target)?;
        }
        let previous = self.location.clone();
        self.location.current = Some(pending.target);
        self.location.pending = None;
        self.location.cleanup = Some(Cleanup {
            source,
            files: pending.files,
        });
        self.location.message = "迁移完成，正在清理原数据。".into();
        if let Err(e) = self.save() {
            self.location = previous;
            return Err(e);
        }
        Ok(())
    }
    /// 在新数据库与账号缓存可初始化后调用；逐文件删除，目录只删除空目录，不递归清空旧根。
    pub fn finish_cleanup(&mut self) -> Result<()> {
        let Some(cleanup) = self.location.cleanup.clone() else {
            return Ok(());
        };
        let result = clean_files(&cleanup, &self.root());
        match result {
            Ok(()) => {
                self.location.cleanup = None;
                self.location.message = "目录已更换，原数据已清理。".into();
            }
            Err(e) => {
                self.location.message =
                    format!("目录已更换，原数据未清理：{e}。解除占用或权限限制后，重启继续清理。");
            }
        }
        self.save()
    }
}

fn canonical(path: &Path) -> Result<PathBuf> {
    Ok(PathBuf::from(external_path(
        &fs::canonicalize(path).map_err(error)?,
    )))
}
fn path_key(path: &Path) -> String {
    let path = external_path(path).replace('\\', "/");
    if cfg!(windows) {
        path.to_lowercase()
    } else {
        path
    }
}
fn relative_under(path: &Path, root: &Path) -> Option<PathBuf> {
    let path = PathBuf::from(external_path(path));
    let root = PathBuf::from(external_path(root));
    // 必须先拒绝父目录跳转，再扩展短名称；不能由路径展开掩盖越界输入。
    if path
        .components()
        .chain(root.components())
        .any(|p| matches!(p, std::path::Component::ParentDir))
    {
        return None;
    }
    #[cfg(windows)]
    let (path, root) = (long_path(&path), long_path(&root));
    let parts: Vec<_> = path.components().collect();
    let base: Vec<_> = root.components().collect();
    if parts.len() < base.len()
        || parts
            .iter()
            .zip(&base)
            .any(|(a, b)| path_key(Path::new(a.as_os_str())) != path_key(Path::new(b.as_os_str())))
    {
        return None;
    }
    Some(parts[base.len()..].iter().map(|p| p.as_os_str()).collect())
}

#[cfg(windows)]
fn long_path(path: &Path) -> PathBuf {
    use std::os::windows::ffi::{OsStrExt, OsStringExt};
    use windows::{core::PCWSTR, Win32::Storage::FileSystem::GetLongPathNameW};
    // 素材可能已经缺失，只展开可访问的祖先，保留缺失后缀；不解析链接或更改外部文件。
    for ancestor in path.ancestors() {
        let wide: Vec<u16> = ancestor.as_os_str().encode_wide().chain(Some(0)).collect();
        let required = unsafe { GetLongPathNameW(PCWSTR(wide.as_ptr()), None) };
        if required == 0 {
            continue;
        }
        let mut buffer = vec![0; required as usize];
        let count = unsafe { GetLongPathNameW(PCWSTR(wide.as_ptr()), Some(&mut buffer)) };
        if count > 0 && (count as usize) < buffer.len() {
            let expanded = PathBuf::from(std::ffi::OsString::from_wide(&buffer[..count as usize]));
            return expanded.join(path.strip_prefix(ancestor).unwrap());
        }
    }
    path.to_path_buf()
}
fn linked(metadata: &fs::Metadata) -> bool {
    if metadata.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        metadata.file_attributes() & 0x400 != 0
    }
    #[cfg(not(windows))]
    false
}
fn check_ancestors(path: &Path) -> Result<()> {
    for parent in path.ancestors() {
        match fs::symlink_metadata(parent) {
            Ok(m) if linked(&m) => {
                return Err("数据目录包含符号链接或目录联接，未执行迁移或清理。".into())
            }
            Ok(_) => (),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => (),
            Err(e) => return Err(error(e)),
        }
    }
    Ok(())
}
fn validate_target(source: &Path, target: &Path) -> Result<PathBuf> {
    if !target.is_absolute() {
        return Err("请选择绝对路径的空文件夹。".into());
    }
    check_ancestors(target)?;
    let target = canonical(target)?;
    let source = canonical(source)?;
    if target.parent().is_none()
        || relative_under(&target, &source).is_some()
        || relative_under(&source, &target).is_some()
    {
        return Err("新旧数据目录不能相同、相互包含或使用磁盘根目录。".into());
    }
    if !target.is_dir() || fs::read_dir(&target).map_err(error)?.next().is_some() {
        return Err("请选择空文件夹。".into());
    }
    Ok(target)
}
fn valid_staging(pending: &Migration) -> bool {
    pending.target.is_absolute()
        && pending.staging.parent() == pending.target.parent()
        && pending
            .staging
            .file_name()
            .and_then(|n| n.to_str())
            .and_then(|n| n.strip_prefix(".quickphrase-migration-"))
            .is_some_and(|n| uuid::Uuid::parse_str(n).is_ok())
}
fn remove_staging(path: &Path) -> Result<()> {
    if !path.exists() {
        return Ok(());
    }
    check_ancestors(path)?;
    // 遍历不跟随任何重解析点，删除时也只逐个处理已经核对的文件及空目录。
    let mut files = vec![];
    collect(path, path, &mut files)?;
    clean_files(
        &Cleanup {
            source: path.to_path_buf(),
            files,
        },
        &PathBuf::new(),
    )?;
    remove_empty_tree(path)
}
fn remove_empty_tree(path: &Path) -> Result<()> {
    check_ancestors(path)?;
    for entry in fs::read_dir(path).map_err(error)? {
        let entry = entry.map_err(error)?;
        if fs::symlink_metadata(entry.path()).map_err(error)?.is_dir() {
            remove_empty_tree(&entry.path())?;
        }
    }
    fs::remove_dir(path).map_err(error)
}
fn collect(root: &Path, path: &Path, files: &mut Vec<PathBuf>) -> Result<()> {
    let metadata = match fs::symlink_metadata(path) {
        Ok(m) => m,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(error(e)),
    };
    if linked(&metadata) {
        return Err("应用数据中包含符号链接或目录联接，未执行迁移或清理。".into());
    }
    if metadata.is_dir() {
        for entry in fs::read_dir(path).map_err(error)? {
            collect(root, &entry.map_err(error)?.path(), files)?;
        }
    } else if metadata.is_file() {
        files.push(path.strip_prefix(root).map_err(error)?.to_path_buf());
    } else {
        return Err("应用数据包含无法迁移的文件类型。".into());
    }
    Ok(())
}
fn collect_library(root: &Path, dir: &Path, files: &mut Vec<PathBuf>) -> Result<()> {
    for name in [
        "shanyu.sqlite3",
        "shanyu.sqlite3-wal",
        "shanyu.sqlite3-shm",
        "shanyu.sqlite3-journal",
        "shanyu.log",
        "images",
        "assets",
        "thumbnails",
        "temp",
    ] {
        collect(root, &dir.join(name), files)?;
    }
    Ok(())
}
fn inventory(root: &Path) -> Result<Vec<PathBuf>> {
    check_ancestors(root)?;
    let mut files = vec![];
    collect_library(root, root, &mut files)?;
    for name in ["teams", "transfer"] {
        collect(root, &root.join(name), &mut files)?;
    }
    let private = root.join("private");
    if private.exists() {
        check_ancestors(&private)?;
        for entry in fs::read_dir(private).map_err(error)? {
            let entry = entry.map_err(error)?;
            if linked(&fs::symlink_metadata(entry.path()).map_err(error)?) {
                return Err("账号库包含符号链接或目录联接，未执行迁移。".into());
            }
            if entry.path().join("shanyu.sqlite3").is_file() {
                collect_library(root, &entry.path(), &mut files)?;
            }
        }
    }
    files.sort();
    Ok(files)
}
fn is_database(path: &Path) -> bool {
    database_location(path) && path.file_name().is_some_and(|n| n == "shanyu.sqlite3")
}
fn is_sidecar(path: &Path) -> bool {
    database_location(path)
        && path.file_name().is_some_and(|n| {
            [
                "shanyu.sqlite3-wal",
                "shanyu.sqlite3-shm",
                "shanyu.sqlite3-journal",
            ]
            .iter()
            .any(|s| n == *s)
        })
}
fn database_location(path: &Path) -> bool {
    let parts: Vec<_> = path.components().collect();
    parts.len() == 1 || (parts.len() == 3 && parts[0].as_os_str() == "private")
}
fn is_batch(path: &Path) -> bool {
    let parts: Vec<_> = path.components().collect();
    parts.first().is_some_and(|p| p.as_os_str() == "transfer")
        && ((parts.len() == 2 && path.extension().is_some_and(|e| e == "json"))
            || (parts.len() == 3 && path.file_name().is_some_and(|n| n == "batch.json")))
}
fn read_database(path: &Path) -> Result<Connection> {
    Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY).map_err(error)
}
fn validate_database(conn: &Connection) -> Result<()> {
    let check: String = conn
        .query_row("PRAGMA quick_check", [], |r| r.get(0))
        .map_err(error)?;
    let mut statement = conn.prepare("PRAGMA foreign_key_check").map_err(error)?;
    if check != "ok"
        || statement
            .query([])
            .map_err(error)?
            .next()
            .map_err(error)?
            .is_some()
    {
        return Err("迁移数据库完整性验证失败，原数据已保留。".into());
    }
    Ok(())
}
fn table_exists(conn: &Connection, table: &str) -> Result<bool> {
    conn.query_row(
        "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=?1)",
        [table],
        |r| r.get(0),
    )
    .map_err(error)
}
fn material_relative(path: &Path, source: &Path) -> Option<PathBuf> {
    let relative = relative_under(path, source)?;
    let parts: Vec<_> = relative.components().collect();
    let first = path_key(Path::new(parts.first()?.as_os_str()));
    if DIRECTORIES.contains(&first.as_str())
        || (first == "private"
            && parts.get(2).is_some_and(|p| {
                ["images", "assets", "thumbnails", "temp"]
                    .contains(&path_key(Path::new(p.as_os_str())).as_str())
            }))
    {
        Some(relative)
    } else {
        None
    }
}
fn relocate_path(value: &mut String, source: &Path, target: &Path) {
    if let Some(relative) = material_relative(Path::new(value), source) {
        *value = external_path(&target.join(relative));
    }
}
/// 只识别图片／附件块的 path；正文、标题、外部关联与已固化网络请求均不作字符串替换。
fn relocate_blocks(value: &mut Value, source: &Path, target: &Path) {
    match value {
        Value::Array(values) => {
            for value in values {
                relocate_blocks(value, source, target);
            }
        }
        Value::Object(values) => {
            if values
                .get("kind")
                .and_then(Value::as_str)
                .is_some_and(|k| k == "image" || k == "file")
            {
                if let Some(Value::String(path)) = values.get_mut("path") {
                    relocate_path(path, source, target);
                }
            }
            for value in values.values_mut() {
                relocate_blocks(value, source, target);
            }
        }
        _ => (),
    }
}
fn relocate_database(conn: &mut Connection, source: &Path, target: &Path) -> Result<()> {
    let capture = if table_exists(conn, "sync_control")? {
        Some(
            conn.query_row("SELECT capture FROM sync_control WHERE id=1", [], |r| {
                r.get::<_, i64>(0)
            })
            .map_err(error)?,
        )
    } else {
        None
    };
    let tx = conn.transaction().map_err(error)?;
    if capture.is_some() {
        tx.execute("UPDATE sync_control SET capture=0 WHERE id=1", [])
            .map_err(error)?;
    }
    for table in ["blocks", "recycle_bin"] {
        let sql = format!("SELECT rowid,content FROM {table}");
        let rows = tx
            .prepare(&sql)
            .map_err(error)?
            .query_map([], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?)))
            .map_err(error)?
            .collect::<std::result::Result<Vec<_>, _>>()
            .map_err(error)?;
        for (row, content) in rows {
            let mut value: Value = serde_json::from_str(&content)
                .map_err(|_| "库内素材记录无法读取，原数据已保留。".to_owned())?;
            let original = value.clone();
            relocate_blocks(&mut value, source, target);
            if value != original {
                tx.execute(
                    &format!("UPDATE {table} SET content=?1 WHERE rowid=?2"),
                    params![value.to_string(), row],
                )
                .map_err(error)?;
            }
        }
    }
    for table in ["sync_assets", "company_copies"] {
        if !table_exists(&tx, table)? {
            continue;
        }
        let rows = tx
            .prepare(&format!("SELECT rowid,path FROM {table}"))
            .map_err(error)?
            .query_map([], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?)))
            .map_err(error)?
            .collect::<std::result::Result<Vec<_>, _>>()
            .map_err(error)?;
        for (row, mut path) in rows {
            let old = path.clone();
            relocate_path(&mut path, source, target);
            if path != old {
                tx.execute(
                    &format!("UPDATE {table} SET path=?1 WHERE rowid=?2"),
                    params![path, row],
                )
                .map_err(error)?;
            }
        }
    }
    if let Some(capture) = capture {
        tx.execute("UPDATE sync_control SET capture=?1 WHERE id=1", [capture])
            .map_err(error)?;
    }
    tx.commit().map_err(error)
}
fn mapped_batch(path: &Path, source: &Path, target: &Path) -> Result<Value> {
    let mut value: Value = serde_json::from_reader(fs::File::open(path).map_err(error)?)
        .map_err(|_| "持久化导入批次无法读取，原数据已保留。".to_owned())?;
    if let Some(pending) = value.get_mut("pending") {
        if let Some(library) = pending.get_mut("library") {
            if let Some(phrases) = library.get_mut("phrases") {
                relocate_blocks(phrases, source, target);
            }
            if let Some(Value::String(dir)) = library.get_mut("dataDir") {
                if let Some(relative) = relative_under(Path::new(dir), source) {
                    *dir = external_path(&target.join(relative));
                }
            }
        }
        if let Some(recycled) = pending.get_mut("recycle_bin") {
            relocate_blocks(recycled, source, target);
        }
        if let Some(Value::Object(materials)) = pending.get_mut("materials") {
            for material in materials.values_mut() {
                if let Value::String(path) = material {
                    relocate_path(path, source, target);
                }
            }
        }
    }
    Ok(value)
}
fn relocate_batch(path: &Path, source: &Path, target: &Path) -> Result<()> {
    let value = mapped_batch(path, source, target)?;
    let mut file = fs::File::create(path).map_err(error)?;
    serde_json::to_writer(&mut file, &value).map_err(error)?;
    file.sync_all().map_err(error)
}
fn same_bytes(a: &Path, b: &Path) -> Result<bool> {
    let mut a = fs::File::open(a).map_err(error)?;
    let mut b = fs::File::open(b).map_err(error)?;
    if a.metadata().map_err(error)?.len() != b.metadata().map_err(error)?.len() {
        return Ok(false);
    }
    let mut left = [0; 65536];
    let mut right = [0; 65536];
    loop {
        let count = a.read(&mut left).map_err(error)?;
        b.read_exact(&mut right[..count]).map_err(error)?;
        if left[..count] != right[..count] {
            return Ok(false);
        }
        if count == 0 {
            return Ok(true);
        }
    }
}
fn validate_copy(source: &Path, physical: &Path, target: &Path) -> Result<()> {
    for relative in inventory(source)? {
        let from = source.join(&relative);
        let to = physical.join(&relative);
        if is_database(&relative) {
            validate_database_copy(&read_database(&from)?, &read_database(&to)?, source, target)?;
        } else if is_batch(&relative) {
            let expected = mapped_batch(&from, source, target)?;
            let actual: Value =
                serde_json::from_reader(fs::File::open(&to).map_err(error)?).map_err(error)?;
            if actual != expected {
                return Err("导入批次路径验证失败，原数据已保留。".into());
            }
        } else if !is_sidecar(&relative) && !same_bytes(&from, &to)? {
            return Err("复制文件验证失败，原数据已保留。".into());
        }
    }
    Ok(())
}
/// 按数据库记录验证备份及唯一允许的路径变更，包含同步状态和未决请求，无需引入额外哈希清单。
fn validate_database_copy(
    from: &Connection,
    to: &Connection,
    source: &Path,
    target: &Path,
) -> Result<()> {
    validate_database(to)?;
    let tables = |conn: &Connection| -> Result<Vec<String>> {
        conn.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")
            .map_err(error)?.query_map([], |r| r.get(0)).map_err(error)?
            .collect::<std::result::Result<Vec<_>, _>>().map_err(error)
    };
    let names = tables(from)?;
    if names != tables(to)? {
        return Err("复制数据库的表结构不一致，原数据已保留。".into());
    }
    for table in names {
        let sql = format!(
            "SELECT * FROM \"{}\" ORDER BY rowid",
            table.replace('"', "\"\"")
        );
        let mut statement = from.prepare(&sql).map_err(error)?;
        let columns: Vec<_> = statement
            .column_names()
            .into_iter()
            .map(str::to_owned)
            .collect();
        let count = columns.len();
        let rows =
            |statement: &mut rusqlite::Statement<'_>| -> Result<Vec<Vec<rusqlite::types::Value>>> {
                statement
                    .query_map([], |row| {
                        (0..count)
                            .map(|i| row.get(i))
                            .collect::<rusqlite::Result<Vec<_>>>()
                    })
                    .map_err(error)?
                    .collect::<std::result::Result<Vec<_>, _>>()
                    .map_err(error)
            };
        let mut expected = rows(&mut statement)?;
        for row in &mut expected {
            for (column, value) in columns.iter().zip(row) {
                if let rusqlite::types::Value::Text(text) = value {
                    if ["blocks", "recycle_bin"].contains(&table.as_str()) && column == "content" {
                        let mut value: Value = serde_json::from_str(text).map_err(error)?;
                        let original = value.clone();
                        relocate_blocks(&mut value, source, target);
                        if value != original {
                            *text = value.to_string();
                        }
                    } else if ["sync_assets", "company_copies"].contains(&table.as_str())
                        && column == "path"
                    {
                        relocate_path(text, source, target);
                    }
                }
            }
        }
        if expected != rows(&mut to.prepare(&sql).map_err(error)?)? {
            return Err("复制数据库记录或素材路径不一致，原数据已保留。".into());
        }
    }
    Ok(())
}
fn protect_blocks(value: &Value, source: &Path, protected: &mut HashSet<String>) {
    match value {
        Value::Array(values) => {
            for value in values {
                protect_blocks(value, source, protected);
            }
        }
        Value::Object(values) => {
            if values.get("kind").and_then(Value::as_str) == Some("file") {
                if let Some(path) = values.get("path").and_then(Value::as_str) {
                    if material_relative(Path::new(path), source).is_none() {
                        protect_reference(path, source, protected);
                    }
                }
            }
            for value in values.values() {
                protect_blocks(value, source, protected);
            }
        }
        _ => (),
    }
}
fn protect_reference(path: &str, source: &Path, protected: &mut HashSet<String>) {
    let path = canonical(Path::new(path)).unwrap_or_else(|_| PathBuf::from(path));
    if let Some(relative) = relative_under(&path, source) {
        protected.insert(path_key(&relative));
    }
}
fn protected_files(source: &Path, files: &[PathBuf]) -> Result<HashSet<String>> {
    let mut protected = HashSet::new();
    for file in files.iter().filter(|p| is_database(p)) {
        let conn = read_database(&source.join(file))?;
        let paths = conn
            .prepare("SELECT path FROM files")
            .map_err(error)?
            .query_map([], |r| r.get::<_, String>(0))
            .map_err(error)?
            .collect::<std::result::Result<Vec<_>, _>>()
            .map_err(error)?;
        for path in paths {
            protect_reference(&path, source, &mut protected);
        }
        for table in ["blocks", "recycle_bin"] {
            let contents = conn
                .prepare(&format!("SELECT content FROM {table}"))
                .map_err(error)?
                .query_map([], |r| r.get::<_, String>(0))
                .map_err(error)?
                .collect::<std::result::Result<Vec<_>, _>>()
                .map_err(error)?;
            for content in contents {
                let value: Value = serde_json::from_str(&content).map_err(error)?;
                protect_blocks(&value, source, &mut protected);
            }
        }
    }
    // 待导入批次尚未写入 files 表，也必须保留其中的外部源文件关联。
    for file in files.iter().filter(|p| is_batch(p)) {
        let value: Value =
            serde_json::from_reader(fs::File::open(source.join(file)).map_err(error)?)
                .map_err(error)?;
        if let Some(pending) = value.get("pending") {
            if let Some(paths) = pending.pointer("/library/files").and_then(Value::as_array) {
                for item in paths {
                    if let Some(path) = item.get("path").and_then(Value::as_str) {
                        protect_reference(path, source, &mut protected);
                    }
                }
            }
            protect_blocks(pending, source, &mut protected);
        }
    }
    Ok(protected)
}
fn clean_files(cleanup: &Cleanup, target: &Path) -> Result<()> {
    check_ancestors(&cleanup.source)?;
    if !target.as_os_str().is_empty()
        && (relative_under(&cleanup.source, target).is_some()
            || relative_under(target, &cleanup.source).is_some())
    {
        return Err("清理来源与当前数据目录相同或相互包含，未执行删除。".into());
    }
    let mut directories = HashSet::new();
    // 先删素材，最后删数据库；清理计划已经排除了所有外部文件关联。
    let mut files = cleanup.files.clone();
    files.sort_by_key(|p| is_database(p));
    for relative in files {
        if relative.is_absolute()
            || relative
                .components()
                .any(|c| !matches!(c, std::path::Component::Normal(_)))
        {
            return Err("清理文件记录越过原数据目录，未执行删除。".into());
        }
        let path = cleanup.source.join(&relative);
        check_ancestors(&path)?;
        match fs::remove_file(&path) {
            Ok(()) => (),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => (),
            Err(e) => return Err(error(e)),
        }
        for parent in path
            .parent()
            .unwrap()
            .ancestors()
            .take_while(|p| *p != cleanup.source)
        {
            directories.insert(parent.to_path_buf());
        }
    }
    let mut directories: Vec<_> = directories.into_iter().collect();
    directories.sort_by_key(|p| std::cmp::Reverse(p.components().count()));
    for directory in directories {
        match fs::remove_dir(directory) {
            Ok(()) => (),
            Err(e)
                if matches!(
                    e.kind(),
                    std::io::ErrorKind::NotFound | std::io::ErrorKind::DirectoryNotEmpty
                ) => {}
            Err(e) => return Err(error(e)),
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    // 用真实 SQLite、WAL 和同步捕获触发器构造最小迁移库，不依赖 Windows 窗口或网络。
    fn library(dir: &Path, identity: &str) -> Connection {
        fs::create_dir_all(dir.join("images")).unwrap();
        fs::create_dir_all(dir.join("assets")).unwrap();
        let conn = Connection::open(dir.join("shanyu.sqlite3")).unwrap();
        conn.execute_batch(
            "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;
            CREATE TABLE blocks(content TEXT NOT NULL);
            CREATE TABLE recycle_bin(content TEXT NOT NULL);
            CREATE TABLE files(path TEXT NOT NULL);
            CREATE TABLE sync_assets(path TEXT PRIMARY KEY,id TEXT UNIQUE);
            CREATE TABLE company_copies(source TEXT PRIMARY KEY,path TEXT NOT NULL);
            CREATE TABLE sync_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE sync_control(id INTEGER PRIMARY KEY,capture INTEGER NOT NULL);
            INSERT INTO sync_control VALUES(1,1);
            CREATE TABLE sync_clock(id INTEGER PRIMARY KEY,value INTEGER NOT NULL);
            INSERT INTO sync_clock VALUES(1,17);
            CREATE TABLE sync_dirty(id TEXT PRIMARY KEY,revision INTEGER NOT NULL);
            INSERT INTO sync_dirty VALUES('offline',17);
            CREATE TABLE sync_inflight(id INTEGER PRIMARY KEY,body TEXT NOT NULL);
            CREATE TRIGGER capture_blocks AFTER UPDATE ON blocks
              WHEN (SELECT capture FROM sync_control WHERE id=1)=1 BEGIN
              UPDATE sync_clock SET value=value+1;
              INSERT OR REPLACE INTO sync_dirty VALUES('unexpected',99); END;",
        )
        .unwrap();
        conn.execute("INSERT INTO sync_meta VALUES('libraryId',?1)", [identity])
            .unwrap();
        conn
    }
    fn directories() -> (tempfile::TempDir, PathBuf, PathBuf) {
        let temp = tempfile::tempdir().unwrap();
        let source = temp.path().join("原数据 中文");
        let target = temp.path().join("新数据 空格");
        fs::create_dir(&target).unwrap();
        (temp, source, target)
    }
    fn insert(conn: &Connection, table: &str, content: Value) {
        conn.execute(
            &format!("INSERT INTO {table}(content) VALUES(?1)"),
            [content.to_string()],
        )
        .unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn windows_short_root_matches_missing_material_under_long_root() {
        use std::os::windows::ffi::OsStrExt;
        use windows::{core::PCWSTR, Win32::Storage::FileSystem::GetShortPathNameW};
        // 只读系统既有的短名称，缺失素材也必须按同一个目录身份映射，不创建系统文件。
        let root = PathBuf::from(std::env::var_os("ProgramFiles").unwrap());
        let wide: Vec<u16> = root.as_os_str().encode_wide().chain(Some(0)).collect();
        let mut buffer = vec![0; 32768];
        let count = unsafe { GetShortPathNameW(PCWSTR(wide.as_ptr()), Some(&mut buffer)) };
        assert!(count > 0 && (count as usize) < buffer.len());
        let short = PathBuf::from(String::from_utf16(&buffer[..count as usize]).unwrap());
        assert_eq!(
            relative_under(
                &short.join("images/missing.png"),
                &canonical(&root).unwrap()
            ),
            Some(PathBuf::from("images/missing.png"))
        );
        assert!(relative_under(&short.join("../outside.png"), &root).is_none());
    }

    #[test]
    fn complete_migration_preserves_all_libraries_requests_and_external_files() {
        let (temp, source, target) = directories();
        let conn = library(&source, "device");
        let account = source.join("private/account");
        let profile = library(&account, "account");
        let image = account.join("images/picture.png");
        fs::write(&image, b"actual-image").unwrap();
        let asset = account.join("assets/attachment.txt");
        fs::write(&asset, b"actual-file").unwrap();
        // 附件名与数据库同名时仍是普通素材，不能当作 SQLite 处理。
        fs::write(
            account.join("assets/shanyu.sqlite3"),
            b"ordinary-attachment",
        )
        .unwrap();
        let external = temp.path().join("外部附件.txt");
        fs::write(&external, b"external").unwrap();
        let linked = source.join("images/关联源.txt");
        fs::write(&linked, b"linked-source").unwrap();
        conn.execute("INSERT INTO files VALUES(?1)", [external_path(&linked)])
            .unwrap();
        let blocks = json!([
            {"kind":"image","path":external_path(&image),"name":"图片"},
            {"kind":"file","path":external_path(&asset),"name":"附件"},
            {"kind":"file","path":external_path(&external),"name":"外部"},
            {"kind":"text","text":external_path(&image)}
        ]);
        for block in blocks.as_array().unwrap() {
            insert(&profile, "blocks", block.clone());
        }
        insert(
            &profile,
            "recycle_bin",
            json!({"phrases":[{"segments":[{"blocks":blocks}]}]}),
        );
        profile
            .execute(
                "INSERT INTO sync_assets VALUES(?1,'asset-original')",
                [external_path(&asset)],
            )
            .unwrap();
        profile
            .execute(
                "INSERT INTO company_copies VALUES('original-source',?1)",
                [external_path(&asset)],
            )
            .unwrap();
        let inflight =
            json!({"requestId":"must-not-change","value":{"path":external_path(&image)}})
                .to_string();
        profile
            .execute("INSERT INTO sync_inflight VALUES(1,?1)", [&inflight])
            .unwrap();
        fs::create_dir_all(source.join("teams/company-uploads/task")).unwrap();
        fs::write(
            source.join("teams/company-uploads/task/content"),
            b"upload-copy",
        )
        .unwrap();
        fs::create_dir_all(source.join("transfer/batch/materials")).unwrap();
        let material = source.join("transfer/batch/materials/one.txt");
        fs::write(&material, b"batch-material").unwrap();
        let pending_source = source.join("images/待导入关联.txt");
        fs::write(&pending_source, b"pending-external").unwrap();
        let batch = json!({"request":{"originalId":"keep-request"},"pending":{
            "library":{"phrases":[{"segments":[{"blocks":[{"kind":"file","path":external_path(&material)}]}]}],"files":[{"path":external_path(&external)},{"path":external_path(&pending_source)}]},
            "recycle_bin":[{"phrases":[{"segments":[{"blocks":[{"kind":"image","path":external_path(&image)}]}]}]}],
            "materials":{"one":external_path(&material)}
        }});
        fs::write(source.join("transfer/batch/batch.json"), batch.to_string()).unwrap();
        fs::write(
            source.join("transfer/private-index.json"),
            batch.to_string(),
        )
        .unwrap();
        fs::write(source.join("用户文件.txt"), b"unknown").unwrap();
        fs::write(account.join("用户文件.txt"), b"unknown-profile").unwrap();
        fs::create_dir_all(source.join("EBWebView")).unwrap();
        fs::write(source.join("EBWebView/state"), b"webview").unwrap();
        drop(profile);
        drop(conn);
        let mut state = DataDirectory::startup(&source).unwrap();
        state.prepare(&target).unwrap();
        drop(state);
        let mut state = DataDirectory::startup(&source).unwrap();
        assert_eq!(state.root(), canonical(&target).unwrap());
        assert!(state.show_result);
        let conn = read_database(&target.join("private/account/shanyu.sqlite3")).unwrap();
        let contents: Vec<String> = conn
            .prepare("SELECT content FROM blocks ORDER BY rowid")
            .unwrap()
            .query_map([], |r| r.get(0))
            .unwrap()
            .map(|r| r.unwrap())
            .collect();
        assert_eq!(
            serde_json::from_str::<Value>(&contents[0]).unwrap()["path"],
            external_path(&canonical(&target.join("private/account/images/picture.png")).unwrap())
        );
        assert_eq!(
            serde_json::from_str::<Value>(&contents[2]).unwrap()["path"],
            external_path(&external)
        );
        assert_eq!(
            serde_json::from_str::<Value>(&contents[3]).unwrap()["text"],
            external_path(&image)
        );
        assert_eq!(
            conn.query_row("SELECT value FROM sync_clock", [], |r| r.get::<_, i64>(0))
                .unwrap(),
            17
        );
        assert_eq!(
            conn.query_row("SELECT count(*) FROM sync_dirty", [], |r| r
                .get::<_, i64>(0))
                .unwrap(),
            1
        );
        assert_eq!(
            conn.query_row("SELECT body FROM sync_inflight", [], |r| r
                .get::<_, String>(0))
                .unwrap(),
            inflight
        );
        assert_eq!(
            conn.query_row("SELECT capture FROM sync_control", [], |r| r
                .get::<_, i64>(0))
                .unwrap(),
            1
        );
        let moved_batch: Value = serde_json::from_reader(
            fs::File::open(target.join("transfer/batch/batch.json")).unwrap(),
        )
        .unwrap();
        assert_eq!(
            moved_batch["pending"]["materials"]["one"],
            external_path(&canonical(&target.join("transfer/batch/materials/one.txt")).unwrap())
        );
        assert_eq!(moved_batch["request"], batch["request"]);
        state.finish_cleanup().unwrap();
        assert!(!source.join("shanyu.sqlite3").exists());
        assert!(!image.exists());
        assert_eq!(fs::read(&external).unwrap(), b"external");
        assert_eq!(fs::read(&linked).unwrap(), b"linked-source");
        assert_eq!(fs::read(&pending_source).unwrap(), b"pending-external");
        assert_eq!(fs::read(source.join("用户文件.txt")).unwrap(), b"unknown");
        assert_eq!(
            fs::read(account.join("用户文件.txt")).unwrap(),
            b"unknown-profile"
        );
        assert_eq!(
            fs::read(source.join("EBWebView/state")).unwrap(),
            b"webview"
        );
        assert!(source.join(LOCATOR).is_file());
        drop(conn);
        drop(state);
        let state = DataDirectory::startup(&source).unwrap();
        assert!(!state.show_result);
        assert_eq!(state.root(), canonical(&target).unwrap());
    }

    #[test]
    fn backup_includes_committed_wal_and_preserves_disabled_capture() {
        let (_temp, source, target) = directories();
        let conn = library(&source, "wal");
        conn.execute("UPDATE sync_control SET capture=0", [])
            .unwrap();
        insert(
            &conn,
            "blocks",
            json!({"kind":"text","text":"committed-in-wal"}),
        );
        assert!(source.join("shanyu.sqlite3-wal").metadata().unwrap().len() > 0);
        // 保持连接打开确保 WAL 没被最后一个连接关闭时写回，直接验证 SQLite 备份接口。
        let mut copy = Connection::open(target.join("shanyu.sqlite3")).unwrap();
        Backup::new(
            &read_database(&source.join("shanyu.sqlite3")).unwrap(),
            &mut copy,
        )
        .unwrap()
        .run_to_completion(256, Duration::from_millis(10), None)
        .unwrap();
        relocate_database(&mut copy, &source, &target).unwrap();
        validate_database_copy(&conn, &copy, &source, &target).unwrap();
        assert_eq!(
            copy.query_row("SELECT count(*) FROM blocks", [], |r| r.get::<_, i64>(0))
                .unwrap(),
            1
        );
        assert_eq!(
            copy.query_row("SELECT capture FROM sync_control", [], |r| r
                .get::<_, i64>(0))
                .unwrap(),
            0
        );
    }

    #[test]
    fn rejected_targets_and_changed_target_leave_source_untouched() {
        let (temp, source, target) = directories();
        drop(library(&source, "unchanged"));
        let mut state = DataDirectory::startup(&source).unwrap();
        assert!(state.prepare(&source).is_err());
        assert!(state.prepare(temp.path()).is_err());
        assert!(state.prepare(&source.join("images")).is_err());
        assert!(state.prepare(Path::new("relative")).is_err());
        fs::write(target.join("existing.txt"), b"existing").unwrap();
        assert!(state.prepare(&target).is_err());
        fs::remove_file(target.join("existing.txt")).unwrap();
        state.prepare(&target).unwrap();
        assert!(state.prepare(&target).is_err());
        fs::write(target.join("later-user-file.txt"), b"later").unwrap();
        let state = DataDirectory::startup(&source).unwrap();
        assert_eq!(state.root(), canonical(&source).unwrap());
        assert!(state.info().message.contains("迁移失败"));
        assert!(source.join("shanyu.sqlite3").exists());
        assert_eq!(
            fs::read(target.join("later-user-file.txt")).unwrap(),
            b"later"
        );
        assert!(state.location.pending.is_none());
    }

    #[test]
    fn copying_interruption_restarts_only_its_staging_directory() {
        let (_temp, source, target) = directories();
        drop(library(&source, "partial"));
        let mut state = DataDirectory::startup(&source).unwrap();
        state.prepare(&target).unwrap();
        let staging = state.location.pending.as_ref().unwrap().staging.clone();
        fs::create_dir_all(staging.join("empty/subdirectory")).unwrap();
        fs::write(staging.join("partial"), b"partial-output").unwrap();
        let mut state = DataDirectory::startup(&source).unwrap();
        assert_eq!(state.root(), canonical(&target).unwrap());
        assert!(!staging.exists());
        state.finish_cleanup().unwrap();
        assert!(!source.join("shanyu.sqlite3").exists());
    }

    #[test]
    fn published_copy_and_cleanup_interruption_resume_without_new_library() {
        let (_temp, source, target) = directories();
        drop(library(&source, "published"));
        let mut state = DataDirectory::startup(&source).unwrap();
        state.prepare(&target).unwrap();
        state.migrate().unwrap();
        let cleanup = state.location.cleanup.clone().unwrap();
        // 模拟目标目录已发布但定位配置仍为 ready 阶段。
        state.location.current = None;
        state.location.pending = Some(Migration {
            target: target.clone(),
            staging: target
                .parent()
                .unwrap()
                .join(format!(".quickphrase-migration-{}", id())),
            ready: true,
            files: cleanup.files.clone(),
        });
        state.location.cleanup = None;
        state.save().unwrap();
        let state = DataDirectory::startup(&source).unwrap();
        assert_eq!(state.root(), canonical(&target).unwrap());
        drop(state);
        // 已经开始清理，但数据库删除后进程退出，重启按已保存的文件列表继续。
        fs::remove_file(source.join("shanyu.sqlite3")).unwrap();
        let mut state = DataDirectory::startup(&source).unwrap();
        state.finish_cleanup().unwrap();
        assert!(state.location.cleanup.is_none());
        let conn = read_database(&target.join("shanyu.sqlite3")).unwrap();
        assert_eq!(
            conn.query_row("SELECT value FROM sync_meta", [], |r| r.get::<_, String>(0))
                .unwrap(),
            "published"
        );
    }

    #[test]
    fn corrupt_copy_is_not_committed_and_missing_active_directory_is_not_recreated() {
        let (_temp, source, target) = directories();
        drop(library(&source, "corruption"));
        let mut state = DataDirectory::startup(&source).unwrap();
        state.prepare(&target).unwrap();
        state.migrate().unwrap();
        let files = state.location.cleanup.as_ref().unwrap().files.clone();
        state.location.current = None;
        state.location.cleanup = None;
        state.location.pending = Some(Migration {
            target: target.clone(),
            staging: target
                .parent()
                .unwrap()
                .join(format!(".quickphrase-migration-{}", id())),
            ready: true,
            files,
        });
        state.save().unwrap();
        Connection::open(target.join("shanyu.sqlite3"))
            .unwrap()
            .execute("DELETE FROM sync_dirty", [])
            .unwrap();
        let mut state = DataDirectory::startup(&source).unwrap();
        assert_eq!(state.root(), canonical(&source).unwrap());
        assert!(state.info().message.contains("记录或素材路径不一致"));
        // 激活目录缺失必须报错，不能创建另一个空库掩盖问题。
        state.location.current = Some(source.parent().unwrap().join("missing"));
        state.save().unwrap();
        assert!(DataDirectory::startup(&source).is_err());
        assert!(!source.parent().unwrap().join("missing").exists());
    }

    #[cfg(windows)]
    #[test]
    fn locked_cleanup_file_is_reported_and_retried() {
        use std::os::windows::fs::OpenOptionsExt;
        let (_temp, source, target) = directories();
        drop(library(&source, "locked"));
        let asset = source.join("images/locked.png");
        fs::write(&asset, b"locked").unwrap();
        let mut state = DataDirectory::startup(&source).unwrap();
        state.prepare(&target).unwrap();
        let mut state = DataDirectory::startup(&source).unwrap();
        let lock = fs::OpenOptions::new()
            .read(true)
            .share_mode(1)
            .open(&asset)
            .unwrap();
        state.finish_cleanup().unwrap();
        assert!(state.location.cleanup.is_some());
        assert!(state.info().message.contains("未清理"));
        assert_eq!(state.root(), canonical(&target).unwrap());
        drop(lock);
        drop(state);
        let mut state = DataDirectory::startup(&source).unwrap();
        state.finish_cleanup().unwrap();
        assert!(state.location.cleanup.is_none());
        assert!(!asset.exists());
    }

    #[cfg(windows)]
    #[test]
    fn copy_failure_preserves_source_and_cleans_partial_output() {
        use std::os::windows::fs::OpenOptionsExt;
        let (_temp, source, target) = directories();
        drop(library(&source, "copy-failure"));
        let asset = source.join("assets/locked.txt");
        fs::write(&asset, b"preserve-source").unwrap();
        let mut state = DataDirectory::startup(&source).unwrap();
        state.prepare(&target).unwrap();
        let staging = state.location.pending.as_ref().unwrap().staging.clone();
        let lock = fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(&asset)
            .unwrap();
        let state = DataDirectory::startup(&source).unwrap();
        assert_eq!(state.root(), canonical(&source).unwrap());
        assert!(state.info().message.contains("迁移失败"));
        assert!(!staging.exists());
        assert!(source.join("shanyu.sqlite3").exists());
        assert_eq!(fs::read_dir(&target).unwrap().count(), 0);
        drop(lock);
        assert_eq!(fs::read(asset).unwrap(), b"preserve-source");
    }

    #[cfg(windows)]
    #[test]
    fn junctions_are_rejected_for_selection_migration_and_cleanup() {
        use std::process::Command;
        let (temp, source, target) = directories();
        drop(library(&source, "junction"));
        let junction = temp.path().join("junction");
        let output = Command::new("cmd")
            .args(["/C", "mklink", "/J"])
            .arg(&junction)
            .arg(&target)
            .output()
            .unwrap();
        assert!(output.status.success());
        let mut state = DataDirectory::startup(&source).unwrap();
        assert!(state.prepare(&junction).is_err());
        fs::remove_dir(&junction).unwrap();
        let junction = source.join("images/outside");
        assert!(Command::new("cmd")
            .args(["/C", "mklink", "/J"])
            .arg(&junction)
            .arg(&target)
            .output()
            .unwrap()
            .status
            .success());
        assert!(state.prepare(&target).is_err());
        assert!(clean_files(
            &Cleanup {
                source: source.clone(),
                files: vec![PathBuf::from("images/outside/keep.txt")]
            },
            &target
        )
        .is_err());
        fs::remove_dir(junction).unwrap();
    }
}
