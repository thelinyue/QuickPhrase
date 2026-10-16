use crate::{
    model::*,
    store::{db_error, Store},
};
use rusqlite::params;
use std::collections::HashSet;

/// 文件库只维护路径关联及显示名。移动、排序和批量移除使用同一事务，
/// 失效文件仍可管理；任何操作都不重命名、移动或删除磁盘上的源文件。
impl Store {
    pub fn linked_files(&self, ids: &[String]) -> Result<Vec<LinkedFile>> {
        let selected: HashSet<_> = ids.iter().collect();
        if ids.is_empty() || selected.len() != ids.len() {
            return Err("请选择有效的文件关联。".into());
        }
        let files: Vec<_> = self
            .library()?
            .files
            .into_iter()
            .filter(|f| selected.contains(&f.id))
            .collect();
        if files.len() != ids.len() {
            return Err("文件关联已不存在，请刷新后重试。".into());
        }
        Ok(files)
    }
    pub fn rename_file(&self, id: &str, name: &str) -> Result<()> {
        let name = name.trim();
        if name.is_empty() || name.chars().count() > 200 || name.contains(['\r', '\n']) {
            return Err("显示名应为1—200个字的单行文字。".into());
        }
        if self
            .conn
            .execute(
                "UPDATE files SET name=?2,custom_name=1 WHERE id=?1",
                params![id, name],
            )
            .map_err(db_error)?
            == 0
        {
            return Err("文件关联已不存在。".into());
        }
        Ok(())
    }
    pub fn move_files(&mut self, ids: &[String], target: &str) -> Result<()> {
        let files = self.linked_files(ids)?;
        let tx = self.conn.transaction().map_err(db_error)?;
        let valid: i64 = tx
            .query_row(
                "SELECT count(*) FROM categories WHERE id=?1 AND kind='file' AND parent_id IS NULL",
                [target],
                |r| r.get(0),
            )
            .map_err(db_error)?;
        if valid == 0 {
            return Err("请选择有效的文件分类。".into());
        }
        for file in files.into_iter().filter(|f| f.category_id != target) {
            let position: i64 = tx
                .query_row(
                    "SELECT coalesce(max(position),-1)+1 FROM files WHERE category_id=?1",
                    [target],
                    |r| r.get(0),
                )
                .map_err(db_error)?;
            tx.execute(
                "UPDATE files SET category_id=?2,position=?3 WHERE id=?1",
                params![file.id, target, position],
            )
            .map_err(db_error)?;
        }
        tx.commit().map_err(db_error)
    }
    pub fn remove_files(&mut self, ids: &[String]) -> Result<()> {
        self.linked_files(ids)?;
        let tx = self.conn.transaction().map_err(db_error)?;
        for id in ids {
            tx.execute("DELETE FROM files WHERE id=?1", [id])
                .map_err(db_error)?;
        }
        tx.commit().map_err(db_error)
    }
    pub fn reorder_files(&mut self, ids: &[String], category: &str) -> Result<()> {
        let actual: HashSet<_> = self
            .library()?
            .files
            .into_iter()
            .filter(|f| f.category_id == category)
            .map(|f| f.id)
            .collect();
        if actual.len() != ids.len()
            || ids.iter().collect::<HashSet<_>>().len() != ids.len()
            || ids.iter().any(|id| !actual.contains(id))
        {
            return Err("文件列表已变化，请刷新后重新排序。".into());
        }
        let tx = self.conn.transaction().map_err(db_error)?;
        for (position, id) in ids.iter().enumerate() {
            tx.execute(
                "UPDATE files SET position=?2 WHERE id=?1",
                params![id, position as i64],
            )
            .map_err(db_error)?;
        }
        tx.commit().map_err(db_error)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cloud_links_preserve_identity_alias_backup_and_external_source() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let source = dir.path().join("源文件.txt");
        std::fs::write(&source, b"keep").unwrap();
        store
            .add_files("local-files", vec![external_path(&source)])
            .unwrap();
        let reference = CloudFileReference {
            server: "https://example.test".into(),
            user_id: "owner".into(),
            asset_id: id(),
        };
        let cloud_id = id();
        store.conn.execute("INSERT INTO files(id,category_id,path,name,position,cloud) VALUES(?1,'local-files','','云端.pdf',1,?2)", params![cloud_id,serde_json::to_string(&reference).unwrap()]).unwrap();
        store.rename_file(&cloud_id, "我的显示名").unwrap();
        let serialized = serde_json::to_value(store.library().unwrap()).unwrap();
        let library: Library = serde_json::from_value(serialized).unwrap();
        assert_eq!(library.files[1].cloud.as_ref(), Some(&reference));
        assert!(library.files[1].path.is_empty());
        assert!(library.files[1].custom_name);
        let target = tempfile::tempdir().unwrap();
        let mut restored = Store::open(target.path()).unwrap();
        restored.import(&library, false, false, &[]).unwrap();
        let restored_files = restored.library().unwrap().files;
        assert_eq!(restored_files[1].cloud.as_ref(), Some(&reference));
        assert_eq!(restored_files[1].name, "我的显示名");
        store
            .reorder_files(
                &[cloud_id.clone(), library.files[0].id.clone()],
                "local-files",
            )
            .unwrap();
        store
            .remove_files(&[cloud_id, library.files[0].id.clone()])
            .unwrap();
        assert_eq!(std::fs::read(source).unwrap(), b"keep");
    }
    #[test]
    fn old_file_schema_is_migrated_without_changing_rows() {
        let dir = tempfile::tempdir().unwrap();
        let conn = rusqlite::Connection::open(dir.path().join("shanyu.sqlite3")).unwrap();
        conn.execute_batch("CREATE TABLE files(id TEXT PRIMARY KEY,category_id TEXT NOT NULL,path TEXT NOT NULL,name TEXT NOT NULL,position INTEGER NOT NULL); INSERT INTO files VALUES('old','category','C:\\old.txt','原显示名',7);").unwrap();
        drop(conn);
        let store = Store::open(dir.path()).unwrap();
        let file = store.library().unwrap().files.remove(0);
        assert_eq!(
            (file.id.as_str(), file.name.as_str(), file.position),
            ("old", "原显示名", 7)
        );
        assert!(file.cloud.is_none());
    }
    #[test]
    fn file_management_preserves_sources_and_rolls_back_invalid_batches() {
        let dir = tempfile::tempdir().unwrap();
        let mut s = crate::store::test_store(dir.path()).unwrap();
        let a = dir.path().join("原始 A.txt");
        let b = dir.path().join("原始 B.txt");
        std::fs::write(&a, "A").unwrap();
        std::fs::write(&b, "B").unwrap();
        s.add_files(
            "local-files",
            vec![a.to_string_lossy().into(), b.to_string_lossy().into()],
        )
        .unwrap();
        let files = s.library().unwrap().files;
        let ids = files.iter().map(|f| f.id.clone()).collect::<Vec<_>>();
        s.rename_file(&ids[0], "显示名称").unwrap();
        assert_eq!(s.linked_files(&ids).unwrap()[0].name, "显示名称");
        let reversed = vec![ids[1].clone(), ids[0].clone()];
        s.reorder_files(&reversed, "local-files").unwrap();
        assert_eq!(s.library().unwrap().files[0].id, ids[1]);
        assert!(s
            .reorder_files(&[ids[0].clone(), ids[0].clone()], "local-files")
            .is_err());
        let target = s
            .save_category(Category {
                id: String::new(),
                name: "目标".into(),
                kind: "file".into(),
                parent_id: None,
                position: 0,
                background: String::new(),
            })
            .unwrap();
        assert!(s
            .move_files(&[ids[0].clone(), "不存在".into()], &target)
            .is_err());
        assert!(s
            .library()
            .unwrap()
            .files
            .iter()
            .all(|f| f.category_id == "local-files"));
        s.move_files(&reversed, &target).unwrap();
        assert!(s
            .library()
            .unwrap()
            .files
            .iter()
            .all(|f| f.category_id == target));
        assert!(s.remove_files(&[ids[0].clone(), "不存在".into()]).is_err());
        assert_eq!(s.library().unwrap().files.len(), 2);
        std::fs::remove_file(&b).unwrap();
        s.rename_file(&ids[1], "失效记录").unwrap();
        s.move_files(&ids, "local-files").unwrap();
        assert!(s.library().unwrap().files.iter().any(|f| f.missing));
        s.remove_files(&ids).unwrap();
        assert_eq!(std::fs::read_to_string(&a).unwrap(), "A");
        assert!(s.recycle_records().unwrap().is_empty());
    }
}
