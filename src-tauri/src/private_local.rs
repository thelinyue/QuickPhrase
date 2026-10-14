//! 个人库的本地事务边界。触发器与内容修改同事务记录待同步键，崩溃不能漏掉离线修改。
//! 服务器基准、当前草稿和已应用设置分别保存，收到远端设置不会把尚未确认的旧值反写云端。
use crate::{
    model::*,
    store::{db_error, write_phrase, Store},
};
use rusqlite::{params, OptionalExtension, Transaction, TransactionBehavior};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use shanyu_team_model::{private as wire, Category as WireCategory};
use std::{collections::HashMap, path::Path};

pub fn init(store: &Store) -> Result<()> {
    store.conn.execute_batch("CREATE TABLE IF NOT EXISTS sync_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS sync_clock(id INTEGER PRIMARY KEY,value INTEGER NOT NULL); INSERT OR IGNORE INTO sync_clock VALUES(1,0);
      CREATE TABLE IF NOT EXISTS sync_control(id INTEGER PRIMARY KEY,capture INTEGER NOT NULL);INSERT OR IGNORE INTO sync_control VALUES(1,1);
      CREATE TABLE IF NOT EXISTS sync_revisions(kind TEXT NOT NULL,id TEXT NOT NULL,revision INTEGER NOT NULL,PRIMARY KEY(kind,id));
      CREATE TABLE IF NOT EXISTS sync_dirty(kind TEXT NOT NULL,id TEXT NOT NULL,revision INTEGER NOT NULL,PRIMARY KEY(kind,id));
      CREATE TABLE IF NOT EXISTS sync_records(kind TEXT NOT NULL,id TEXT NOT NULL,version INTEGER NOT NULL,value TEXT,conflict INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(kind,id));
      CREATE TABLE IF NOT EXISTS sync_desired_settings(id TEXT PRIMARY KEY,value TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS sync_assets(path TEXT PRIMARY KEY,id TEXT NOT NULL UNIQUE,kind TEXT NOT NULL,name TEXT NOT NULL,size INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS sync_inflight(id INTEGER PRIMARY KEY,body TEXT NOT NULL,revisions TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS sync_merge(kind TEXT NOT NULL,id TEXT NOT NULL,target TEXT NOT NULL,PRIMARY KEY(kind,id));
      CREATE TABLE IF NOT EXISTS import_batches(id TEXT PRIMARY KEY);") .map_err(db_error)?;
    store
        .conn
        .execute(
            "INSERT OR IGNORE INTO sync_meta VALUES('libraryId',?1)",
            [id()],
        )
        .map_err(db_error)?;
    // 写入段落和内容块同样影响整条话术。级联删除时所属段落可能已消失，
    // 由话术／段落的 BEFORE DELETE 捕获修订，内容块不再写入空身份。
    // 事务替换触发器也修复已经打开过的旧库，不能只修复新建数据库。
    // 重开账号库时可能存在同步写入，初始化先等待写锁，不从旧读取快照升级。
    let tx = Transaction::new_unchecked(&store.conn, TransactionBehavior::Immediate)
        .map_err(db_error)?;
    for (table, kind, expr) in [
        ("categories", "category", "row.id"),
        ("phrases", "phrase", "row.id"),
        ("common_phrases", "common", "row.id"),
        ("recycle_bin", "recycle", "row.id"),
        ("segments", "phrase", "row.phrase_id"),
        (
            "blocks",
            "phrase",
            "(SELECT phrase_id FROM segments WHERE id=row.segment_id)",
        ),
    ] {
        for (event, row, timing) in [
            ("INSERT", "new", "AFTER"),
            ("UPDATE", "new", "AFTER"),
            ("DELETE", "old", "BEFORE"),
        ] {
            let expr = expr.replace("row.", &format!("{row}."));
            let filter = if table == "categories" {
                format!(" AND {row}.kind='phrase'")
            } else {
                String::new()
            };
            tx.execute_batch(&format!("DROP TRIGGER IF EXISTS sync_{table}_{event}; CREATE TRIGGER sync_{table}_{event} {timing} {event} ON {table} WHEN (SELECT capture FROM sync_control WHERE id=1)=1 AND {expr} IS NOT NULL{filter} BEGIN
              UPDATE sync_clock SET value=value+1 WHERE id=1;
              INSERT INTO sync_revisions VALUES('{kind}',{expr},(SELECT value FROM sync_clock WHERE id=1)) ON CONFLICT(kind,id) DO UPDATE SET revision=excluded.revision;
              INSERT INTO sync_dirty VALUES('{kind}',{expr},(SELECT value FROM sync_clock WHERE id=1)) ON CONFLICT(kind,id) DO UPDATE SET revision=excluded.revision;
              END;" )).map_err(db_error)?;
        }
    }
    tx.commit().map_err(db_error)
}
pub fn meta(store: &Store, key: &str) -> Result<String> {
    Ok(store
        .conn
        .query_row("SELECT value FROM sync_meta WHERE key=?1", [key], |r| {
            r.get(0)
        })
        .optional()
        .map_err(db_error)?
        .unwrap_or_default())
}
pub fn set_meta(store: &Store, key: &str, value: &str) -> Result<()> {
    store.conn.execute("INSERT INTO sync_meta VALUES(?1,?2) ON CONFLICT(key) DO UPDATE SET value=excluded.value",params![key,value]).map_err(db_error)?;
    Ok(())
}
pub fn identity(store: &Store) -> Result<String> {
    meta(store, "libraryId")
}
pub fn revision(store: &Store, kind: &str, id: &str) -> Result<u64> {
    Ok(store
        .conn
        .query_row(
            "SELECT revision FROM sync_revisions WHERE kind=?1 AND id=?2",
            params![kind, id],
            |r| r.get::<_, i64>(0),
        )
        .optional()
        .map_err(db_error)?
        .unwrap_or(0) as u64)
}
pub fn scope(store: &Store, expected: Option<&str>) -> Result<()> {
    if let Some(expected) = expected {
        if identity(store)? != expected {
            return Err("个人库已切换，请重新打开窗口；草稿已保留。".into());
        }
    }
    Ok(())
}
pub fn content_key(store: &Store, kind: &str, id: &str) -> Result<String> {
    Ok(format!(
        "private:{}:{}:{}:{}",
        identity(store)?,
        kind,
        id,
        revision(store, kind, id)?
    ))
}
pub fn resolve_key(store: &Store, key: &str, kind: &str) -> Result<String> {
    if !key.starts_with("private:") {
        return Ok(key.into());
    }
    let parts: Vec<_> = key.split(':').collect();
    if parts.len() != 5
        || parts[1] != identity(store)?
        || parts[2] != kind
        || parts[4].parse::<u64>().ok() != Some(revision(store, kind, parts[3])?)
    {
        return Err("内容已更新或个人库已切换，请重新选择。".into());
    }
    Ok(parts[3].into())
}
pub fn revisions(store: &Store) -> Result<HashMap<String, u64>> {
    let mut stmt = store
        .conn
        .prepare("SELECT kind,id,revision FROM sync_revisions")
        .map_err(db_error)?;
    let pairs = stmt
        .query_map([], |r| {
            Ok((
                format!("{}:{}", r.get::<_, String>(0)?, r.get::<_, String>(1)?),
                r.get::<_, i64>(2)? as u64,
            ))
        })
        .map_err(db_error)?
        .collect::<std::result::Result<_, _>>()
        .map_err(db_error)?;
    Ok(pairs)
}
pub fn bound(store: &Store) -> bool {
    meta(store, "owner").is_ok_and(|v| !v.is_empty())
}
/// 只清理本批复制的文件；业务事务提交后才保留，任何失败均不触碰外部源文件。
#[derive(Default)]
pub struct MaterialBatch {
    paths: Vec<std::path::PathBuf>,
    keep: bool,
}
impl MaterialBatch {
    pub fn track(&mut self, path: std::path::PathBuf) {
        self.paths.push(path);
    }
    pub fn keep(mut self) {
        self.keep = true;
    }
    pub fn append(&mut self, mut batch: MaterialBatch) {
        self.paths.append(&mut batch.paths);
    }
}
impl Drop for MaterialBatch {
    fn drop(&mut self) {
        if !self.keep {
            for path in &self.paths {
                let _ = std::fs::remove_file(path);
                if let Some(dir) = path.parent().filter(|dir| {
                    dir.file_name()
                        .is_some_and(|name| uuid::Uuid::parse_str(&name.to_string_lossy()).is_ok())
                }) {
                    let _ = std::fs::remove_dir(dir);
                }
            }
        }
    }
}
pub fn safe_materials(store: &Store, p: &mut Phrase) -> Result<MaterialBatch> {
    let mut batch = MaterialBatch::default();
    if !bound(store) {
        return Ok(batch);
    }
    std::fs::create_dir_all(store.dir.join("assets")).map_err(db_error)?;
    for b in p.segments.iter_mut().flat_map(|s| &mut s.blocks) {
        let (path, image) = match b {
            Block::Image { path, .. } => (path, true),
            Block::File { path, .. } => (path, false),
            _ => continue,
        };
        let known: bool = store
            .conn
            .query_row(
                "SELECT EXISTS(SELECT 1 FROM sync_assets WHERE path=?1 AND kind=?2)",
                params![path.as_str(), if image { "image" } else { "file" }],
                |r| r.get(0),
            )
            .map_err(db_error)?;
        if known && !Path::new(path).exists() {
            continue;
        }
        let source =
            std::fs::canonicalize(&*path).map_err(|_| "素材缺失，请重新选择。".to_owned())?;
        let owned = std::fs::canonicalize(&store.dir).map_err(db_error)?;
        if source.starts_with(owned.join("assets")) || source.starts_with(owned.join("images")) {
            continue;
        }
        if std::fs::metadata(&source).map_err(db_error)?.len() > 128 * 1024 * 1024 {
            return Err("单个素材不能超过128MB。".into());
        }
        let filename = source.file_name().ok_or("素材文件名无效。")?;
        let folder = if image {
            store.dir.join("images")
        } else {
            store.dir.join("assets").join(id())
        };
        std::fs::create_dir_all(&folder).map_err(db_error)?;
        let target = if image {
            folder.join(format!("{}.png", id()))
        } else {
            folder.join(filename)
        };
        batch.paths.push(target.clone());
        if image {
            crate::backup::decode_image(&std::fs::read(&source).map_err(db_error)?)?
                .save(&target)
                .map_err(db_error)?;
        } else {
            std::fs::copy(&source, &target).map_err(db_error)?;
        }
        *path = external_path(&target);
    }
    Ok(batch)
}
pub fn materials_ready(store: &Store, p: &Phrase) -> bool {
    p.segments.iter().flat_map(|s| &s.blocks).all(|b| match b {
        Block::Text { .. } => true,
        Block::Image { path, .. } | Block::File { path, .. } => {
            let Ok(meta) = std::fs::metadata(path) else {
                return false;
            };
            let expected: Option<i64> = store
                .conn
                .query_row("SELECT size FROM sync_assets WHERE path=?1", [path], |r| {
                    r.get(0)
                })
                .optional()
                .ok()
                .flatten();
            meta.is_file() && expected.is_none_or(|size| size > 0 && meta.len() == size as u64)
        }
    })
}
fn category(c: &Category) -> Value {
    serde_json::to_value(WireCategory {
        id: c.id.clone(),
        name: c.name.clone(),
        parent_id: c.parent_id.clone(),
        position: c.position,
        background: c.background.clone(),
    })
    .unwrap()
}
pub fn local_records(store: &Store) -> Result<HashMap<(wire::Kind, String), Option<Value>>> {
    let lib = store.library()?;
    let mut records = HashMap::new();
    for c in lib.categories.iter().filter(|c| c.kind == "phrase") {
        records.insert((wire::Kind::Category, c.id.clone()), Some(category(c)));
    }
    for p in lib.phrases {
        records.insert(
            (wire::Kind::Phrase, p.id.clone()),
            Some(serde_json::to_value(p).map_err(db_error)?),
        );
    }
    for c in lib.common_phrases {
        records.insert(
            (wire::Kind::Common, c.id.clone()),
            Some(serde_json::to_value(c).map_err(db_error)?),
        );
    }
    for r in store.recycle_records()? {
        let mut value = serde_json::to_value(&r).map_err(db_error)?;
        value["categories"] = Value::Array(r.categories.iter().map(category).collect());
        records.insert((wire::Kind::Recycle, r.id.clone()), Some(value));
    }
    let settings = serde_json::to_value(lib.settings).map_err(db_error)?;
    for key in wire::SETTINGS {
        let desired: Option<String> = store
            .conn
            .query_row(
                "SELECT value FROM sync_desired_settings WHERE id=?1",
                [key],
                |r| r.get(0),
            )
            .optional()
            .map_err(db_error)?;
        records.insert(
            (wire::Kind::Setting, (*key).into()),
            Some(match desired {
                Some(v) => serde_json::from_str(&v).map_err(db_error)?,
                None => settings[*key].clone(),
            }),
        );
    }
    Ok(records)
}
/// 设置差异按本次明确修改的字段入队，不能把未确认的云端发送配置替换为旧本机配置。
pub fn save_setting_changes(tx: &Transaction<'_>, old: &Settings, next: &Settings) -> Result<()> {
    let old = serde_json::to_value(old).map_err(db_error)?;
    let next = serde_json::to_value(next).map_err(db_error)?;
    for key in wire::SETTINGS {
        if old[*key] != next[*key] {
            tx.execute("INSERT INTO sync_desired_settings VALUES(?1,?2) ON CONFLICT(id) DO UPDATE SET value=excluded.value",params![key,next[*key].to_string()]).map_err(db_error)?;
            tx.execute("UPDATE sync_clock SET value=value+1 WHERE id=1", [])
                .map_err(db_error)?;
            tx.execute("INSERT INTO sync_dirty VALUES('setting',?1,(SELECT value FROM sync_clock WHERE id=1)) ON CONFLICT(kind,id) DO UPDATE SET revision=excluded.revision",[key]).map_err(db_error)?;
        }
    }
    Ok(())
}
pub fn base_records(store: &Store) -> Result<HashMap<(wire::Kind, String), wire::Record>> {
    let mut stmt = store
        .conn
        .prepare("SELECT kind,id,version,value FROM sync_records")
        .map_err(db_error)?;
    let rows = stmt
        .query_map([], |r| {
            Ok((
                r.get::<_, String>(0)?,
                r.get::<_, String>(1)?,
                r.get::<_, i64>(2)?,
                r.get::<_, Option<String>>(3)?,
            ))
        })
        .map_err(db_error)?
        .collect::<std::result::Result<Vec<_>, _>>()
        .map_err(db_error)?;
    rows.into_iter()
        .map(|(kind, id, version, value)| {
            let kind: wire::Kind = serde_json::from_value(Value::String(kind)).map_err(db_error)?;
            Ok((
                (kind, id.clone()),
                wire::Record {
                    kind,
                    id,
                    version: version as u64,
                    value: value
                        .map(|v| serde_json::from_str(&v).map_err(db_error))
                        .transpose()?,
                },
            ))
        })
        .collect()
}
pub fn dirty(store: &Store) -> Result<HashMap<(wire::Kind, String), u64>> {
    let mut stmt = store
        .conn
        .prepare("SELECT kind,id,revision FROM sync_dirty")
        .map_err(db_error)?;
    let rows = stmt
        .query_map([], |r| {
            Ok((
                r.get::<_, String>(0)?,
                r.get::<_, String>(1)?,
                r.get::<_, i64>(2)?,
            ))
        })
        .map_err(db_error)?
        .collect::<std::result::Result<Vec<_>, _>>()
        .map_err(db_error)?;
    rows.into_iter()
        .map(|(kind, id, rev)| {
            Ok((
                (
                    serde_json::from_value(Value::String(kind)).map_err(db_error)?,
                    id,
                ),
                rev as u64,
            ))
        })
        .collect()
}
pub fn put_base(tx: &Transaction<'_>, r: &wire::Record, conflict: bool) -> Result<()> {
    tx.execute("INSERT INTO sync_records VALUES(?1,?2,?3,?4,?5) ON CONFLICT(kind,id) DO UPDATE SET version=excluded.version,value=excluded.value,conflict=excluded.conflict",params![r.kind.as_str(),r.id,r.version as i64,r.value.as_ref().map(Value::to_string),conflict]).map_err(db_error)?;
    Ok(())
}
pub fn bump(tx: &Transaction<'_>, kind: wire::Kind, id: &str, dirty: bool) -> Result<()> {
    tx.execute("UPDATE sync_clock SET value=value+1 WHERE id=1", [])
        .map_err(db_error)?;
    tx.execute("INSERT INTO sync_revisions VALUES(?1,?2,(SELECT value FROM sync_clock WHERE id=1)) ON CONFLICT(kind,id) DO UPDATE SET revision=excluded.revision",params![kind.as_str(),id]).map_err(db_error)?;
    if dirty {
        tx.execute("INSERT INTO sync_dirty VALUES(?1,?2,(SELECT value FROM sync_clock WHERE id=1)) ON CONFLICT(kind,id) DO UPDATE SET revision=excluded.revision",params![kind.as_str(),id]).map_err(db_error)?;
    }
    Ok(())
}
/// 拉取应用到干净记录，脏记录仍留在业务表供离线使用。全部依赖先投影再一次事务重建，避免分类级联删掉保留的草稿。
pub fn project_in(
    tx: &Transaction<'_>,
    settings: Settings,
    records: &HashMap<(wire::Kind, String), Option<Value>>,
    capture: bool,
    confirm_settings: bool,
) -> Result<()> {
    let applied = settings.clone();
    let mut categories = vec![];
    let mut phrases = vec![];
    let mut common = vec![];
    let mut recycle = vec![];
    let mut settings = serde_json::to_value(settings).map_err(db_error)?;
    for ((kind, _), value) in records {
        let Some(value) = value else { continue };
        match kind {
            wire::Kind::Category => {
                let mut value = value.clone();
                value["kind"] = Value::String("phrase".into());
                categories.push(serde_json::from_value::<Category>(value).map_err(db_error)?);
            }
            wire::Kind::Phrase => {
                phrases.push(serde_json::from_value::<Phrase>(value.clone()).map_err(db_error)?)
            }
            wire::Kind::Common => common
                .push(serde_json::from_value::<CommonPhrase>(value.clone()).map_err(db_error)?),
            wire::Kind::Recycle => {
                let mut value = value.clone();
                if let Some(cats) = value["categories"].as_array_mut() {
                    for c in cats {
                        c["kind"] = Value::String("phrase".into());
                    }
                }
                recycle.push(serde_json::from_value::<RecycleRecord>(value).map_err(db_error)?);
            }
            wire::Kind::Setting => {}
        }
    }
    for ((kind, key), value) in records {
        if *kind == wire::Kind::Setting && (confirm_settings || !wire::requires_confirmation(key)) {
            if let Some(value) = value {
                settings[key] = value.clone();
            }
        }
    }
    let mut settings: Settings = serde_json::from_value(settings).map_err(db_error)?;
    if let Err(error) = settings.validate() {
        if confirm_settings {
            return Err(error);
        }
        // 字段合并可能让两项显示开关互相矛盾。保留本机已应用配置供确认，正文仍继续同步。
        settings = applied;
    }
    tx.execute("UPDATE sync_control SET capture=?1 WHERE id=1", [capture])
        .map_err(db_error)?;
    tx.execute("DELETE FROM categories WHERE kind='phrase'", [])
        .map_err(db_error)?;
    tx.execute("DELETE FROM common_phrases", [])
        .map_err(db_error)?;
    tx.execute("DELETE FROM recycle_bin", [])
        .map_err(db_error)?;
    for c in categories
        .iter()
        .filter(|c| c.parent_id.is_none())
        .chain(categories.iter().filter(|c| c.parent_id.is_some()))
    {
        tx.execute(
            "INSERT INTO categories VALUES(?1,?2,'phrase',?3,?4,?5)",
            params![c.id, c.name, c.parent_id, c.position, c.background],
        )
        .map_err(db_error)?;
    }
    for p in phrases {
        write_phrase(tx, &p)?;
    }
    for c in common {
        tx.execute(
            "INSERT INTO common_phrases VALUES(?1,?2,?3)",
            params![c.id, c.text, c.position],
        )
        .map_err(db_error)?;
    }
    for r in recycle {
        tx.execute(
            "INSERT INTO recycle_bin VALUES(?1,?2,?3)",
            params![
                r.id,
                r.deleted_at as i64,
                serde_json::to_string(&r).map_err(db_error)?
            ],
        )
        .map_err(db_error)?;
    }
    tx.execute(
        "INSERT INTO settings VALUES(1,?1) ON CONFLICT(id) DO UPDATE SET content=excluded.content",
        [serde_json::to_string(&settings).map_err(db_error)?],
    )
    .map_err(db_error)?;
    tx.execute("UPDATE sync_control SET capture=1 WHERE id=1", [])
        .map_err(db_error)?;
    Ok(())
}

#[derive(Serialize, Deserialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct Profile {
    pub library_id: String,
    pub server: String,
    pub user_id: String,
    pub username: String,
}
pub fn registry(store: &Store) -> Result<()> {
    store.conn.execute_batch("CREATE TABLE IF NOT EXISTS private_profiles(server TEXT NOT NULL,user_id TEXT NOT NULL,library_id TEXT NOT NULL UNIQUE,username TEXT NOT NULL,PRIMARY KEY(server,user_id));CREATE TABLE IF NOT EXISTS private_active(id INTEGER PRIMARY KEY,library_id TEXT NOT NULL);").map_err(db_error)?;
    Ok(())
}
pub fn find_profile(store: &Store, server: &str, user: &str) -> Result<Option<Profile>> {
    store.conn.query_row("SELECT library_id,server,user_id,username FROM private_profiles WHERE server=?1 AND user_id=?2",params![server,user],|r|Ok(Profile{library_id:r.get(0)?,server:r.get(1)?,user_id:r.get(2)?,username:r.get(3)?})).optional().map_err(db_error)
}
pub fn open_profile(dir: &Path, profile: &Profile) -> Result<Store> {
    let store = Store::open(&dir.join("private").join(&profile.library_id))?;
    init(&store)?;
    for (key, value) in [
        ("libraryId", &profile.library_id),
        ("server", &profile.server),
        ("owner", &profile.user_id),
        ("username", &profile.username),
    ] {
        set_meta(&store, key, value)?;
    }
    Ok(store)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn reopening_during_background_writes_preserves_library() {
        use std::sync::{
            atomic::{AtomicBool, Ordering},
            Arc, Barrier,
        };
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        let root = store
            .save_category(Category {
                id: String::new(),
                name: "原有分类".into(),
                kind: "phrase".into(),
                parent_id: None,
                position: 0,
                background: String::new(),
            })
            .unwrap();
        let writer = Store::open(dir.path()).unwrap();
        let stop = Arc::new(AtomicBool::new(false));
        let ready = Arc::new(Barrier::new(2));
        let background = {
            let stop = stop.clone();
            let ready = ready.clone();
            let root = root.clone();
            std::thread::spawn(move || -> Result<i64> {
                ready.wait();
                let mut commits = 0;
                while !stop.load(Ordering::Acquire) {
                    writer
                        .conn
                        .execute(
                            "UPDATE categories SET position=position+1 WHERE id=?1",
                            [&root],
                        )
                        .map_err(db_error)?;
                    commits += 1;
                    std::thread::yield_now();
                }
                Ok(commits)
            })
        };
        ready.wait();
        // 登录、预览会重开仍在同步的账号库，初始化不能在读后升级锁时直接失败。
        let mut opening_error = None;
        for _ in 0..40 {
            if let Err(error) = Store::open(dir.path()) {
                opening_error = Some(error);
                break;
            }
        }
        stop.store(true, Ordering::Release);
        let commits = background.join().unwrap().unwrap();
        assert!(opening_error.is_none(), "账号库重开失败：{opening_error:?}");
        let categories = store.categories().unwrap();
        assert_eq!(categories.len(), 1);
        assert_eq!(categories[0].id, root);
        assert_eq!(categories[0].position as i64, commits);
    }
    #[test]
    fn cascading_delete_reopens_old_triggers_and_rollback_never_leaves_dirty_keys() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = Store::open(dir.path()).unwrap();
        let root = store
            .save_category(Category {
                id: String::new(),
                name: "私人".into(),
                kind: "phrase".into(),
                parent_id: None,
                position: 0,
                background: String::new(),
            })
            .unwrap();
        let phrase = store
            .save_phrase(
                Phrase {
                    id: String::new(),
                    root_id: root.clone(),
                    group_id: None,
                    title: "测试".into(),
                    color: String::new(),
                    background: String::new(),
                    shortcut: String::new(),
                    position: 0,
                    segments: vec![Segment {
                        blocks: vec![Block::Text {
                            text: "正文".into(),
                        }],
                    }],
                },
                None,
            )
            .unwrap();
        store.conn.execute_batch("DROP TRIGGER sync_blocks_DELETE; CREATE TRIGGER sync_blocks_DELETE BEFORE DELETE ON blocks BEGIN INSERT INTO sync_dirty VALUES('phrase',(SELECT phrase_id FROM segments WHERE id=old.segment_id),1);END;").unwrap();
        drop(store);
        let store = Store::open(dir.path()).unwrap();
        store.conn.execute("DELETE FROM sync_dirty", []).unwrap();
        {
            let tx = store.conn.unchecked_transaction().unwrap();
            tx.execute("DELETE FROM categories WHERE id=?1", [&root])
                .unwrap();
        }
        assert!(dirty(&store).unwrap().is_empty());
        assert!(store.phrase(&phrase).is_ok());
        let tx = store.conn.unchecked_transaction().unwrap();
        tx.execute("DELETE FROM categories WHERE id=?1", [&root])
            .unwrap();
        tx.commit().unwrap();
        assert!(dirty(&store)
            .unwrap()
            .contains_key(&(wire::Kind::Category, root)));
        assert!(dirty(&store)
            .unwrap()
            .contains_key(&(wire::Kind::Phrase, phrase)));
    }
    #[test]
    fn account_material_copy_preserves_source_and_failed_save_cleans_new_files() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = Store::open(&dir.path().join("account")).unwrap();
        set_meta(&store, "owner", &id()).unwrap();
        let root = store
            .save_category(Category {
                id: String::new(),
                name: "私人".into(),
                kind: "phrase".into(),
                parent_id: None,
                position: 0,
                background: String::new(),
            })
            .unwrap();
        let file = dir.path().join("外部.txt");
        std::fs::write(&file, "原件").unwrap();
        let make = |root: String| Phrase {
            id: String::new(),
            root_id: root,
            group_id: None,
            title: "附件".into(),
            color: String::new(),
            background: String::new(),
            shortcut: String::new(),
            position: 0,
            segments: vec![Segment {
                blocks: vec![Block::File {
                    path: external_path(&file),
                    name: "外部.txt".into(),
                }],
            }],
        };
        assert!(store.save_phrase(make(id()), None).is_err());
        assert_eq!(
            std::fs::read_dir(store.dir.join("assets")).unwrap().count(),
            0
        );
        let pid = store.save_phrase(make(root), None).unwrap();
        let p = store.phrase(&pid).unwrap();
        let Block::File { path, .. } = &p.segments[0].blocks[0] else {
            panic!()
        };
        assert_ne!(Path::new(path), file);
        assert_eq!(Path::new(path).file_name().unwrap(), "外部.txt");
        std::fs::write(&file, "后来修改").unwrap();
        assert_eq!(std::fs::read_to_string(path).unwrap(), "原件");
        assert_eq!(std::fs::read_to_string(&file).unwrap(), "后来修改");
    }
}
