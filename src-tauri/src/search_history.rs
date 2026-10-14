//! 搜索记录仅属于当前个人库的本机元数据，不进入同步队列或内容备份。
//! 两个搜索窗口共用数据库事务，投递回执携带的库身份用于拒绝切号后的旧写入。
use crate::{model::Result, private_commands, store::db_error, store::Store, AppState};
use rusqlite::{Connection, OptionalExtension, Transaction, TransactionBehavior};
use tauri::{Emitter, State};

fn read(conn: &Connection) -> Result<Vec<String>> {
    let value: Option<String> = conn
        .query_row(
            "SELECT value FROM sync_meta WHERE key='searchHistory'",
            [],
            |r| r.get(0),
        )
        .optional()
        .map_err(db_error)?;
    match value {
        Some(value) => serde_json::from_str(&value)
            .map_err(|_| "搜索记录无法读取，请清空搜索记录后重试。".into()),
        None => Ok(vec![]),
    }
}

/// 先取得写锁再读取，避免两处窗口同时使用结果时丢失另一处刚保存的记录。
fn record(store: &Store, query: &str) -> Result<()> {
    let query = query.trim();
    if query.is_empty() {
        return Ok(());
    }
    let tx = Transaction::new_unchecked(&store.conn, TransactionBehavior::Immediate)
        .map_err(db_error)?;
    let mut items = read(&tx)?;
    items.retain(|item| item != query);
    items.insert(0, query.to_owned());
    items.truncate(10);
    let value = serde_json::to_string(&items).map_err(db_error)?;
    tx.execute("INSERT INTO sync_meta VALUES('searchHistory',?1) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [value]).map_err(db_error)?;
    tx.commit().map_err(db_error)
}

fn clear(store: &Store) -> Result<()> {
    let tx = store.conn.unchecked_transaction().map_err(db_error)?;
    tx.execute("DELETE FROM sync_meta WHERE key='searchHistory'", [])
        .map_err(db_error)?;
    tx.commit().map_err(db_error)
}

#[tauri::command]
pub fn get_search_history(state: State<AppState>, library_id: String) -> Result<Vec<String>> {
    let store = private_commands::active_store(&state, Some(&library_id))?;
    read(&store.conn)
}

#[tauri::command]
pub fn record_search_history(
    app: tauri::AppHandle,
    state: State<AppState>,
    library_id: String,
    query: String,
) -> Result<()> {
    let store = private_commands::active_store(&state, Some(&library_id))?;
    record(&store, &query)?;
    drop(store);
    app.emit("search-history-changed", library_id)
        .map_err(|_| "搜索记录已保存，但界面刷新通知失败。".into())
}

#[tauri::command]
pub fn clear_search_history(
    app: tauri::AppHandle,
    state: State<AppState>,
    library_id: String,
) -> Result<()> {
    let store = private_commands::active_store(&state, Some(&library_id))?;
    clear(&store)?;
    drop(store);
    app.emit("search-history-changed", library_id)
        .map_err(|_| "搜索记录已清空，但界面刷新通知失败。".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn history_is_trimmed_recent_unique_bounded_and_persistent() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        assert!(read(&store.conn).unwrap().is_empty());
        record(&store, " \t ").unwrap();
        assert!(read(&store.conn).unwrap().is_empty());
        for i in 0..12 {
            record(&store, &format!("关键词{i}")).unwrap();
        }
        record(&store, " 关键词5 ").unwrap();
        let expected = vec![
            "关键词5",
            "关键词11",
            "关键词10",
            "关键词9",
            "关键词8",
            "关键词7",
            "关键词6",
            "关键词4",
            "关键词3",
            "关键词2",
        ];
        assert_eq!(read(&store.conn).unwrap(), expected);
        drop(store);
        let store = Store::open(dir.path()).unwrap();
        assert_eq!(read(&store.conn).unwrap(), expected);
        clear(&store).unwrap();
        drop(store);
        assert!(read(&Store::open(dir.path()).unwrap().conn)
            .unwrap()
            .is_empty());
    }

    #[test]
    fn history_does_not_change_sync_state_or_content_backup() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(&dir.path().join("source")).unwrap();
        let before = serde_json::to_value(store.library().unwrap()).unwrap();
        record(&store, "本机关键词").unwrap();
        assert_eq!(
            serde_json::to_value(store.library().unwrap()).unwrap(),
            before
        );
        for table in ["sync_dirty", "sync_inflight", "sync_revisions"] {
            let count: i64 = store
                .conn
                .query_row(&format!("SELECT count(*) FROM {table}"), [], |r| r.get(0))
                .unwrap();
            assert_eq!(count, 0);
        }
        let backup = dir.path().join("content.zip");
        crate::backup::export(&store, &backup, "backup", None).unwrap();
        let preview = crate::backup::preview(&backup).unwrap();
        assert!(!serde_json::to_string(&preview.library)
            .unwrap()
            .contains("本机关键词"));
    }

    #[test]
    fn separate_libraries_and_old_library_identity_remain_isolated() {
        let dir = tempfile::tempdir().unwrap();
        let a = Store::open(&dir.path().join("a")).unwrap();
        let b = Store::open(&dir.path().join("b")).unwrap();
        record(&a, "账号A").unwrap();
        record(&b, "账号B").unwrap();
        assert!(crate::private_local::scope(
            &b,
            Some(&crate::private_local::identity(&a).unwrap())
        )
        .is_err());
        clear(&b).unwrap();
        assert_eq!(read(&a.conn).unwrap(), ["账号A"]);
        assert!(read(&b.conn).unwrap().is_empty());
    }

    #[test]
    fn failed_save_rolls_back_and_corrupt_history_can_be_cleared() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        record(&store, "原记录").unwrap();
        store.conn.execute_batch("CREATE TRIGGER fail_history BEFORE UPDATE ON sync_meta WHEN new.key='searchHistory' BEGIN SELECT RAISE(ABORT,'测试写入失败'); END;").unwrap();
        assert!(record(&store, "新记录").is_err());
        assert_eq!(read(&store.conn).unwrap(), ["原记录"]);
        store.conn.execute_batch("DROP TRIGGER fail_history; UPDATE sync_meta SET value='broken' WHERE key='searchHistory';").unwrap();
        assert!(read(&store.conn).is_err());
        clear(&store).unwrap();
        assert!(read(&store.conn).unwrap().is_empty());
    }
}
