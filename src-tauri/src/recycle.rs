use crate::{
    model::*,
    store::{check_categories, db_error, valid_category_background, write_phrase, Store},
};
use pinyin::ToPinyin;
use rusqlite::{params, Transaction};
use std::{
    collections::{HashMap, HashSet},
    time::{SystemTime, UNIX_EPOCH},
};

/// 单条与批量回收共用话术快照；批量删除只读取一次话术和分类，避免逐条重复加载全库。
pub(crate) fn phrase_snapshot(
    phrase: &Phrase,
    all_categories: &[Category],
) -> Result<RecycleRecord> {
    let categories: Vec<_> = all_categories
        .iter()
        .filter(|c| c.id == phrase.root_id || phrase.group_id.as_deref() == Some(&c.id))
        .cloned()
        .collect();
    let location = [Some(phrase.root_id.as_str()), phrase.group_id.as_deref()]
        .into_iter()
        .flatten()
        .filter_map(|id| {
            categories
                .iter()
                .find(|c| c.id == id)
                .map(|c| c.name.as_str())
        })
        .collect::<Vec<_>>()
        .join(" / ");
    Ok(RecycleRecord {
        id: id(),
        kind: "phrase".into(),
        label: if phrase.label().is_empty() {
            "图片或附件话术".into()
        } else {
            phrase.label()
        },
        location,
        deleted_at: SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(db_error)?
            .as_millis() as u64,
        categories,
        phrases: vec![phrase.clone()],
    })
}

/// 删除与保存回收记录在同一 SQLite 事务内完成；恢复亦先写回全部内容，再移除记录。
/// 不按名称合并分类、不覆盖当前内容、不移除图片或外部文件，失败时完整回滚。
impl Store {
    pub(crate) fn recycle_snapshot(&self, kind: &str, source: &str) -> Result<RecycleRecord> {
        if kind == "phrase" {
            return phrase_snapshot(&self.phrase(source)?, &self.categories()?);
        }
        let library = self.library()?;
        let record_kind = if library
            .categories
            .iter()
            .any(|c| c.id == source && c.parent_id.is_some())
        {
            "secondary-category"
        } else {
            "primary-category"
        };
        let (label, location, phrases, categories) = {
            let category = library
                .categories
                .iter()
                .find(|c| c.id == source && c.kind == "phrase")
                .cloned()
                .ok_or("话术分类已不存在。")?;
            let parent = category.parent_id.as_deref();
            let phrases = library
                .phrases
                .into_iter()
                .filter(|p| {
                    if parent.is_some() {
                        p.group_id.as_deref() == Some(source)
                    } else {
                        p.root_id == source
                    }
                })
                .collect();
            let location = parent
                .and_then(|id| library.categories.iter().find(|c| c.id == id))
                .map_or("一级分类".into(), |c| c.name.clone());
            let categories = library
                .categories
                .into_iter()
                .filter(|c| {
                    c.id == source
                        || c.parent_id.as_deref() == Some(source)
                        || parent == Some(c.id.as_str())
                })
                .collect();
            (category.name.clone(), location, phrases, categories)
        };
        Ok(RecycleRecord {
            id: id(),
            kind: record_kind.into(),
            label,
            location,
            deleted_at: SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map_err(db_error)?
                .as_millis() as u64,
            categories,
            phrases,
        })
    }
    pub fn recycle_records(&self) -> Result<Vec<RecycleRecord>> {
        let mut stmt = self
            .conn
            .prepare("SELECT content FROM recycle_bin ORDER BY deleted_at DESC,rowid DESC")
            .map_err(db_error)?;
        let rows = stmt
            .query_map([], |r| r.get::<_, String>(0))
            .map_err(db_error)?;
        rows.map(|r| {
            serde_json::from_str(&r.map_err(db_error)?)
                .map_err(|e| format!("回收站记录无法读取：{e}"))
        })
        .collect()
    }
    pub fn recycle_items(&self, query: &str) -> Result<Vec<RecycleItem>> {
        let terms: Vec<_> = query.split_whitespace().map(str::to_lowercase).collect();
        Ok(self
            .recycle_records()?
            .into_iter()
            .filter(|r| {
                let content = format!(
                    "{} {} {}",
                    r.label,
                    r.location,
                    r.phrases
                        .iter()
                        .map(Phrase::text)
                        .collect::<Vec<_>>()
                        .join(" ")
                )
                .to_lowercase();
                let mut full = String::new();
                let mut initials = String::new();
                for syllable in content.as_str().to_pinyin().flatten() {
                    full.push_str(syllable.plain());
                    if let Some(c) = syllable.plain().chars().next() {
                        initials.push(c);
                    }
                }
                terms.iter().all(|term| {
                    content.contains(term) || full.contains(term) || initials.contains(term)
                })
            })
            .map(|r| RecycleItem {
                id: r.id,
                kind: r.kind,
                label: r.label,
                location: r.location,
                deleted_at: r.deleted_at,
                phrase_count: r.phrases.len(),
            })
            .collect())
    }
    pub fn recycle_record(&self, id: &str) -> Result<RecycleRecord> {
        self.recycle_records()?
            .into_iter()
            .find(|r| r.id == id)
            .ok_or_else(|| "回收站记录已不存在，请刷新后重试。".into())
    }
    pub fn restore_recycled(&mut self, ids: &[String], without_shortcuts: bool) -> Result<usize> {
        check_selection(ids)?;
        let records = self.recycle_records()?;
        let selected = ids
            .iter()
            .map(|id| {
                records
                    .iter()
                    .find(|r| &r.id == id)
                    .cloned()
                    .ok_or_else(|| "回收站记录已不存在，请刷新后重试。".to_owned())
            })
            .collect::<Result<Vec<_>>>()?;
        for phrase in selected.iter().flat_map(|r| &r.phrases) {
            validate_phrase(phrase)?;
            self.validate_images(phrase)?;
        }
        let settings = self.settings()?;
        let tx = self.conn.transaction().map_err(db_error)?;
        let mut recreated = HashMap::new();
        for record in &selected {
            for c in record
                .categories
                .iter()
                .filter(|c| c.parent_id.is_none())
                .chain(record.categories.iter().filter(|c| c.parent_id.is_some()))
            {
                let mut c = c.clone();
                if let Some(parent) = &c.parent_id {
                    let moved: i64 = tx
                        .query_row(
                            "SELECT count(*) FROM categories WHERE id=?1 AND parent_id IS NOT ?2",
                            params![c.id, parent],
                            |r| r.get(0),
                        )
                        .map_err(db_error)?;
                    if moved > 0 {
                        // 删除后分组可能被用户移动；在原位置重建快照，不迁回或改写活动分组。
                        c.id = recreated
                            .entry((c.id.clone(), parent.clone()))
                            .or_insert_with(id)
                            .clone();
                    }
                }
                let exists: i64 = tx
                    .query_row(
                        "SELECT count(*) FROM categories WHERE id=?1",
                        [&c.id],
                        |r| r.get(0),
                    )
                    .map_err(db_error)?;
                if exists == 0 {
                    tx.execute("UPDATE categories SET position=position+1 WHERE kind=?1 AND parent_id IS ?2 AND position>=?3", params![c.kind,c.parent_id,c.position]).map_err(db_error)?;
                    tx.execute(
                        "INSERT INTO categories VALUES(?1,?2,?3,?4,?5,?6)",
                        params![c.id, c.name, c.kind, c.parent_id, c.position, c.background],
                    )
                    .map_err(db_error)?;
                }
            }
        }
        // 先补齐全部分类，再按原位置还原本批次的全部话术；不按回收记录的选择顺序逐条插入，
        // 避免倒序恢复时反复推后已经还原的话术，导致整库删除后的原排序位置变化。
        let mut phrases: Vec<_> = selected
            .iter()
            .flat_map(|record| record.phrases.iter().cloned())
            .collect();
        phrases.sort_by_key(|p| p.position);
        for mut p in phrases {
            if let Some(group) = &p.group_id {
                if let Some(new_id) = recreated.get(&(group.clone(), p.root_id.clone())) {
                    p.group_id = Some(new_id.clone());
                }
            }
            check_categories(&tx, &p)?;
            let exists: i64 = tx
                .query_row("SELECT count(*) FROM phrases WHERE id=?1", [&p.id], |r| {
                    r.get(0)
                })
                .map_err(db_error)?;
            if exists != 0 {
                return Err("恢复的话术编号已被使用，未覆盖现有话术。".into());
            }
            if without_shortcuts {
                p.shortcut.clear();
            }
            if !p.shortcut.is_empty() {
                let key = crate::native::parse_shortcut(&p.shortcut)?;
                p.shortcut = key.canonical();
                let occupied = crate::store::shortcut_collision(&tx, &p.id, &key)?.is_some();
                let reserved = settings.shortcut_reserved(&key);
                if occupied || reserved {
                    return Err(format!("快捷键「{}」已被占用。可勾选“恢复时不恢复快捷键”后重试，回收站记录已保留。", p.shortcut));
                }
            }
            tx.execute("UPDATE phrases SET position=position+1 WHERE root_id=?1 AND group_id IS ?2 AND position>=?3", params![p.root_id,p.group_id,p.position]).map_err(db_error)?;
            write_phrase(&tx, &p)?;
        }
        for record in &selected {
            tx.execute("DELETE FROM recycle_bin WHERE id=?1", [&record.id])
                .map_err(db_error)?;
        }
        // 其余回收话术复用此次重建的分组；与还原同事务更新，避免逐条还原生成重复分组。
        for mut record in records.into_iter().filter(|r| !ids.contains(&r.id)) {
            let mut updated = false;
            for c in &mut record.categories {
                if let Some(parent) = &c.parent_id {
                    if let Some(new_id) = recreated.get(&(c.id.clone(), parent.clone())) {
                        c.id = new_id.clone();
                        updated = true;
                    }
                }
            }
            for p in &mut record.phrases {
                if let Some(group) = &p.group_id {
                    if let Some(new_id) = recreated.get(&(group.clone(), p.root_id.clone())) {
                        p.group_id = Some(new_id.clone());
                        updated = true;
                    }
                }
            }
            if updated {
                tx.execute(
                    "UPDATE recycle_bin SET content=?2 WHERE id=?1",
                    params![record.id, serde_json::to_string(&record).map_err(db_error)?],
                )
                .map_err(db_error)?;
            }
        }
        tx.commit().map_err(db_error)?;
        Ok(selected.len())
    }
    pub fn remove_recycled(&mut self, ids: &[String]) -> Result<()> {
        check_selection(ids)?;
        let tx = self.conn.transaction().map_err(db_error)?;
        for id in ids {
            if tx
                .execute("DELETE FROM recycle_bin WHERE id=?1", [id])
                .map_err(db_error)?
                == 0
            {
                return Err("回收站记录已不存在，请刷新后重试。".into());
            }
        }
        tx.commit().map_err(db_error)
    }
    pub fn empty_recycle(&self) -> Result<()> {
        self.conn
            .execute("DELETE FROM recycle_bin", [])
            .map_err(db_error)?;
        Ok(())
    }
}

fn check_selection(ids: &[String]) -> Result<()> {
    if ids.is_empty() || ids.iter().collect::<HashSet<_>>().len() != ids.len() {
        return Err("请选择有效的回收站记录。".into());
    }
    Ok(())
}
pub(crate) fn write_record(tx: &Transaction<'_>, record: &RecycleRecord) -> Result<()> {
    // SQLite INTEGER 使用有符号 64 位值，不能把无效备份的超大时间戳截断或写成负数。
    let deleted_at =
        i64::try_from(record.deleted_at).map_err(|_| "回收站删除时间超出有效范围。")?;
    tx.execute(
        "INSERT INTO recycle_bin VALUES(?1,?2,?3)",
        params![
            record.id,
            deleted_at,
            serde_json::to_string(record).map_err(db_error)?
        ],
    )
    .map_err(db_error)?;
    Ok(())
}
/// 备份导入统一重分配编号，多个回收记录共享的分类上下文仍使用同一映射。
/// 已导入的活动分类复用其新编号，恢复回收记录时不会另建重复的原分类。
pub(crate) fn import_records(
    tx: &Transaction<'_>,
    records: &[RecycleRecord],
    active_map: &HashMap<String, String>,
) -> Result<()> {
    let mut map = active_map.clone();
    for c in records.iter().flat_map(|r| &r.categories) {
        map.entry(c.id.clone()).or_insert_with(id);
    }
    for original in records {
        if !["phrase", "primary-category", "secondary-category"].contains(&original.kind.as_str()) {
            return Err("备份中的回收站类型无效。".into());
        }
        // 回收分类尚未写入活动表，外键不能验证其 JSON 快照；导入时先检查两级上下文，
        // 避免错误层级或跨分类话术成功入库后无法还原，同时让整个导入事务回滚。
        let roots: Vec<_> = original
            .categories
            .iter()
            .filter(|c| c.parent_id.is_none())
            .collect();
        if roots.len() != 1
            || (original.kind == "phrase" && original.phrases.len() != 1)
            || (original.kind == "secondary-category" && original.categories.len() != 2)
        {
            return Err("备份中的回收站分类层级或记录内容无效。".into());
        }
        let root = roots[0];
        for c in &original.categories {
            if c.kind != "phrase"
                || c.id.is_empty()
                || c.name.trim().is_empty()
                || c.name.chars().count() > 40
                || !valid_category_background(&c.background)
                || c.parent_id
                    .as_ref()
                    .is_some_and(|parent| parent != &root.id)
            {
                return Err("备份中的回收站分类无效。".into());
            }
        }
        for p in &original.phrases {
            if p.root_id != root.id
                || p.group_id.as_ref().is_some_and(|group| {
                    !original
                        .categories
                        .iter()
                        .any(|c| &c.id == group && c.parent_id.as_ref() == Some(&root.id))
                })
                || (original.kind == "secondary-category" && p.group_id.is_none())
            {
                return Err("备份中的回收站话术不属于记录中的分类。".into());
            }
        }
        let mut record = original.clone();
        record.id = id();
        let mut seen = HashSet::new();
        for c in &mut record.categories {
            if !seen.insert(c.id.clone()) {
                return Err("备份中的回收站分类无效。".into());
            }
            c.id = map.get(&c.id).cloned().ok_or("回收站分类编号无效。")?;
            c.parent_id = c
                .parent_id
                .as_ref()
                .map(|id| {
                    map.get(id)
                        .cloned()
                        .ok_or("回收站分类缺少上级。".to_owned())
                })
                .transpose()?;
        }
        for p in &mut record.phrases {
            validate_phrase(p)?;
            p.id = id();
            p.root_id = map
                .get(&p.root_id)
                .cloned()
                .ok_or("回收站话术缺少一级分类。")?;
            p.group_id = p
                .group_id
                .as_ref()
                .map(|id| {
                    map.get(id)
                        .cloned()
                        .ok_or("回收站话术缺少二级分类。".to_owned())
                })
                .transpose()?;
        }
        write_record(tx, &record)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn category(store: &Store, name: &str, parent: Option<&str>) -> String {
        store
            .save_category(Category {
                id: String::new(),
                name: name.into(),
                kind: "phrase".into(),
                parent_id: parent.map(str::to_owned),
                position: 0,
                background: "#84c394".into(),
            })
            .unwrap()
    }
    fn phrase(root: &str, group: Option<&str>) -> Phrase {
        Phrase {
            id: String::new(),
            root_id: root.into(),
            group_id: group.map(str::to_owned),
            title: "进群欢迎".into(),
            color: "#ff5100".into(),
            background: "#ffffcc".into(),
            shortcut: String::new(),
            position: 0,
            segments: vec![Segment {
                blocks: vec![Block::Text {
                    text: "您好，欢迎加入！".into(),
                }],
            }],
        }
    }
    #[test]
    fn phrase_recycling_survives_reopen_and_restores_media_and_current_category() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = Store::open(dir.path()).unwrap();
        let group = category(&store, "同名", Some("common"));
        let image = store.dir.join("images/测试.png");
        image::RgbaImage::new(1, 1).save(&image).unwrap();
        let file = store.dir.join("附件.txt");
        std::fs::write(&file, "源文件").unwrap();
        let mut p = phrase("common", Some(&group));
        p.segments.push(Segment {
            blocks: vec![
                Block::Image {
                    path: image.to_string_lossy().into(),
                    name: "图片".into(),
                },
                Block::File {
                    path: file.to_string_lossy().into(),
                    name: "附件".into(),
                },
            ],
        });
        let id = store.save_phrase(p, None).unwrap();
        let original = store.phrase(&id).unwrap();
        store.delete_item(&id, "phrase", true).unwrap();
        assert!(store.search("进群").unwrap().is_empty());
        assert!(store.phrase(&id).is_err());
        assert_eq!(store.recycle_items("jinq").unwrap().len(), 1);
        assert_eq!(store.recycle_items("jqhy").unwrap().len(), 1);
        drop(store);
        let mut store = Store::open(dir.path()).unwrap();
        let mut c = store
            .categories()
            .unwrap()
            .into_iter()
            .find(|c| c.id == group)
            .unwrap();
        c.name = "后来修改".into();
        store.save_category(c).unwrap();
        let record = store.recycle_records().unwrap().remove(0);
        store.restore_recycled(&[record.id], false).unwrap();
        assert_eq!(
            serde_json::to_value(store.phrase(&id).unwrap()).unwrap(),
            serde_json::to_value(original).unwrap()
        );
        assert!(store
            .categories()
            .unwrap()
            .iter()
            .any(|c| c.id == group && c.name == "后来修改"));
        assert!(store.recycle_items("").unwrap().is_empty());
        assert!(image.exists() && file.exists());
        store.delete_item(&id, "phrase", true).unwrap();
        let record = store.recycle_records().unwrap().remove(0);
        store.remove_recycled(&[record.id]).unwrap();
        assert!(image.exists() && file.exists());
    }
    #[test]
    fn separately_recycled_phrase_and_category_restore_shared_ids_without_merging_names() {
        let dir = tempfile::tempdir().unwrap();
        let mut s = Store::open(dir.path()).unwrap();
        let root = category(&s, "重复", None);
        let other = category(&s, "重复", None);
        let a = category(&s, "分组", Some(&root));
        let b = category(&s, "分组", Some(&root));
        let first = s.save_phrase(phrase(&root, Some(&a)), None).unwrap();
        let second = s.save_phrase(phrase(&root, Some(&b)), None).unwrap();
        s.delete_item(&first, "phrase", true).unwrap();
        let first_record = s.recycle_records().unwrap().remove(0);
        s.delete_category(&root, false, None, true).unwrap();
        let category_record = s
            .recycle_records()
            .unwrap()
            .into_iter()
            .find(|r| r.kind == "primary-category")
            .unwrap();
        s.restore_recycled(&[first_record.id], false).unwrap();
        s.restore_recycled(&[category_record.id], false).unwrap();
        let library = s.library().unwrap();
        assert!(library.categories.iter().any(|c| c.id == other));
        assert_eq!(
            library
                .categories
                .iter()
                .filter(|c| c.name == "重复")
                .count(),
            2
        );
        assert_eq!(s.phrase(&first).unwrap().group_id, Some(a));
        assert_eq!(s.phrase(&second).unwrap().group_id, Some(b));
        assert!(library
            .categories
            .iter()
            .any(|c| c.id == root && c.background == "#84c394"));
    }
    #[test]
    fn restoring_after_group_move_preserves_active_group_and_reuses_recreated_context() {
        let dir = tempfile::tempdir().unwrap();
        let mut s = Store::open(dir.path()).unwrap();
        let root = category(&s, "原分类", None);
        let target = category(&s, "移动目标", None);
        let group = category(&s, "通用", Some(&root));
        let first = s.save_phrase(phrase(&root, Some(&group)), None).unwrap();
        let second = s.save_phrase(phrase(&root, Some(&group)), None).unwrap();
        let active = s.save_phrase(phrase(&root, Some(&group)), None).unwrap();
        s.delete_item(&first, "phrase", true).unwrap();
        s.delete_item(&second, "phrase", true).unwrap();
        s.move_copy_category(&root, &target, false).unwrap();
        let first_record = s
            .recycle_records()
            .unwrap()
            .into_iter()
            .find(|r| r.phrases[0].id == first)
            .unwrap();
        s.restore_recycled(&[first_record.id], false).unwrap();
        let new_group = s.phrase(&first).unwrap().group_id.unwrap();
        assert_ne!(new_group, group);
        let second_record = s.recycle_records().unwrap().remove(0);
        assert_eq!(
            second_record.phrases[0].group_id.as_deref(),
            Some(new_group.as_str())
        );
        s.restore_recycled(&[second_record.id], false).unwrap();
        assert_eq!(s.phrase(&second).unwrap().group_id, Some(new_group.clone()));
        assert_eq!(s.phrase(&active).unwrap().root_id, target);
        assert_eq!(s.phrase(&active).unwrap().group_id, Some(group));
        assert!(s
            .categories()
            .unwrap()
            .iter()
            .any(|c| c.id == new_group && c.parent_id.as_deref() == Some(root.as_str())));
    }
    #[test]
    fn restore_conflicts_roll_back_all_changes_and_explicitly_omit_shortcuts() {
        let dir = tempfile::tempdir().unwrap();
        let mut s = Store::open(dir.path()).unwrap();
        let root = category(&s, "待恢复", None);
        let mut original = phrase(&root, None);
        original.shortcut = "Ctrl+Alt+1".into();
        let original_id = s.save_phrase(original.clone(), None).unwrap();
        s.delete_item(&original_id, "phrase", true).unwrap();
        s.delete_category(&root, false, None, true).unwrap();
        original.root_id = "common".into();
        original.shortcut = "Ctrl+Alt+1+C".into();
        let active = s.save_phrase(original, None).unwrap();
        let ids = s
            .recycle_records()
            .unwrap()
            .into_iter()
            .map(|r| r.id)
            .collect::<Vec<_>>();
        assert!(s
            .restore_recycled(&ids, false)
            .unwrap_err()
            .contains("已被占用"));
        assert_eq!(s.recycle_records().unwrap().len(), 2);
        assert!(!s.categories().unwrap().iter().any(|c| c.id == root));
        s.restore_recycled(&ids, true).unwrap();
        assert_eq!(s.phrase(&original_id).unwrap().shortcut, "");
        assert_eq!(s.phrase(&active).unwrap().shortcut, "Ctrl+Alt+1+C");
    }
    #[test]
    fn delete_transaction_failure_and_permanent_removal_never_touch_source_files() {
        let dir = tempfile::tempdir().unwrap();
        let mut s = Store::open(dir.path()).unwrap();
        let id = s.save_phrase(phrase("common", None), None).unwrap();
        s.conn.execute_batch("CREATE TRIGGER reject_delete BEFORE DELETE ON phrases BEGIN SELECT RAISE(ABORT,'模拟失败'); END;").unwrap();
        assert!(s.delete_item(&id, "phrase", true).is_err());
        assert!(s.recycle_records().unwrap().is_empty());
        assert!(s.phrase(&id).is_ok());
        s.conn.execute_batch("DROP TRIGGER reject_delete;").unwrap();
        s.delete_item(&id, "phrase", true).unwrap();
        let records = s.recycle_records().unwrap();
        assert!(s
            .remove_recycled(&[records[0].id.clone(), "不存在".into()])
            .is_err());
        assert_eq!(s.recycle_records().unwrap().len(), 1);
        s.empty_recycle().unwrap();
        assert!(s.recycle_records().unwrap().is_empty());
        let id = s.save_phrase(phrase("common", None), None).unwrap();
        s.delete_item(&id, "phrase", false).unwrap();
        assert!(s.recycle_records().unwrap().is_empty());
    }
}
