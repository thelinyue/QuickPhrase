use crate::model::*;
use rusqlite::{params, Connection, OptionalExtension, Transaction, TransactionBehavior};
use std::{
    collections::{HashMap, HashSet},
    path::{Path, PathBuf},
    time::Duration,
};

pub(crate) fn db_error(e: impl std::fmt::Display) -> String {
    format!("数据库操作失败：{e}")
}

/// 成员分类及其内容属于独立只读来源，不能让其显示用主键落入个人写入或批量操作。
fn writable_ids<'a>(ids: impl IntoIterator<Item = &'a str>) -> Result<()> {
    if ids
        .into_iter()
        .any(|id| id.starts_with("member:") || id.starts_with("member-category:"))
    {
        return Err("成员话术的分类为只读，请在本人分类中操作。".into());
    }
    Ok(())
}

/// 本地库只通过本模块写入。分类、话术、分段和块使用外键与事务保持一致；
/// 外部文件仅保存路径关联，任何删除操作都不会删除源文件。
pub struct Store {
    pub conn: Connection,
    pub dir: PathBuf,
}
impl Store {
    pub fn open(dir: &Path) -> Result<Self> {
        std::fs::create_dir_all(dir).map_err(|e| format!("无法创建用户数据目录：{e}"))?;
        for sub in ["images", "thumbnails", "temp"] {
            std::fs::create_dir_all(dir.join(sub)).map_err(db_error)?;
        }
        let mut conn = Connection::open(dir.join("shanyu.sqlite3")).map_err(db_error)?;
        conn.busy_timeout(Duration::from_secs(5))
            .map_err(db_error)?;
        conn.execute_batch("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;")
            .map_err(db_error)?;
        // 首发前只维护版本1的完整初始结构，开发期调整直接合并到这里，不累加迁移版本。
        // 现有开发库按实际字段和索引无损补齐，结构整理与版本标记在同一事务中完成。
        // 账号库可能仍在后台同步；先取得写锁，避免结构检查后的锁升级遇到旧快照而立即失败。
        let schema = conn
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(db_error)?;
        schema.execute_batch("CREATE TABLE IF NOT EXISTS categories(id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('phrase','file')), parent_id TEXT REFERENCES categories(id) ON DELETE CASCADE, position INTEGER NOT NULL, background TEXT NOT NULL DEFAULT '');
          CREATE TABLE IF NOT EXISTS phrases(id TEXT PRIMARY KEY,root_id TEXT NOT NULL REFERENCES categories(id) ON DELETE CASCADE,group_id TEXT REFERENCES categories(id) ON DELETE CASCADE,title TEXT NOT NULL,color TEXT NOT NULL,background TEXT NOT NULL,shortcut TEXT NOT NULL,position INTEGER NOT NULL);
          CREATE TABLE IF NOT EXISTS segments(id TEXT PRIMARY KEY,phrase_id TEXT NOT NULL REFERENCES phrases(id) ON DELETE CASCADE,position INTEGER NOT NULL);
          CREATE TABLE IF NOT EXISTS blocks(id TEXT PRIMARY KEY,segment_id TEXT NOT NULL REFERENCES segments(id) ON DELETE CASCADE,position INTEGER NOT NULL,content TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS files(id TEXT PRIMARY KEY,category_id TEXT NOT NULL REFERENCES categories(id) ON DELETE CASCADE,path TEXT NOT NULL,name TEXT NOT NULL,position INTEGER NOT NULL);
          CREATE TABLE IF NOT EXISTS common_phrases(id TEXT PRIMARY KEY,text TEXT NOT NULL,position INTEGER NOT NULL);
          CREATE TABLE IF NOT EXISTS settings(id INTEGER PRIMARY KEY CHECK(id=1),content TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS recycle_bin(id TEXT PRIMARY KEY,deleted_at INTEGER NOT NULL,content TEXT NOT NULL);").map_err(db_error)?;
        // 早期开发库可能缺少分类颜色列；只补列，不重建表，保留主键、层级与内容。
        let has_background = {
            let mut columns = schema
                .prepare("PRAGMA table_info(categories)")
                .map_err(db_error)?;
            let names = columns
                .query_map([], |r| r.get::<_, String>(1))
                .map_err(db_error)?;
            names
                .collect::<std::result::Result<Vec<_>, _>>()
                .map_err(db_error)?
                .iter()
                .any(|name| name == "background")
        };
        if !has_background {
            schema
                .execute(
                    "ALTER TABLE categories ADD COLUMN background TEXT NOT NULL DEFAULT ''",
                    [],
                )
                .map_err(db_error)?;
        }
        // 名称只用于展示，分类身份与内容关联均由主键区分。删除旧版名称唯一索引即可
        // 允许同级重名；不重建分类表，保留原有层级、颜色、排序及所有外键关联。
        // 同键话术通过候选选择，不再要求唯一。只替换旧索引，内容主键、外键及事务保持原样。
        let unique_shortcuts: bool = schema.query_row("SELECT EXISTS(SELECT 1 FROM pragma_index_list('phrases') WHERE name='phrase_shortcuts' AND \"unique\"=1)", [], |r| r.get(0)).map_err(db_error)?;
        if unique_shortcuts {
            schema
                .execute_batch("DROP INDEX phrase_shortcuts;")
                .map_err(db_error)?;
        }
        schema.execute_batch("DROP INDEX IF EXISTS category_names; CREATE INDEX IF NOT EXISTS phrase_shortcuts ON phrases(shortcut COLLATE NOCASE) WHERE shortcut<>''; PRAGMA user_version=1;")
            .map_err(db_error)?;
        schema.commit().map_err(db_error)?;
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS company_copies(source TEXT PRIMARY KEY,path TEXT NOT NULL)",
        )
        .map_err(db_error)?;
        // 分类由用户创建；新库和删除全部分类后的库都保持为空，不自动补默认分类。
        let store = Self {
            conn,
            dir: dir.to_path_buf(),
        };
        crate::private_local::init(&store)?;
        // 不记住吸附状态时，仅在启动读取库时关闭左右吸附，当前会话仍可手动开启。
        let mut settings = store.settings()?;
        if !settings.remember_dock && settings.dock {
            settings.dock = false;
            store.save_settings(&settings)?;
        }
        Ok(store)
    }
    pub fn categories(&self) -> Result<Vec<Category>> {
        let mut stmt = self
            .conn
            .prepare(
                "SELECT id,name,kind,parent_id,position,background FROM categories ORDER BY position,rowid",
            )
            .map_err(db_error)?;
        let rows = stmt
            .query_map([], |r| {
                Ok(Category {
                    id: r.get(0)?,
                    name: r.get(1)?,
                    kind: r.get(2)?,
                    parent_id: r.get(3)?,
                    position: r.get(4)?,
                    background: r.get(5)?,
                })
            })
            .map_err(db_error)?;
        rows.collect::<std::result::Result<Vec<_>, _>>()
            .map_err(db_error)
    }
    pub fn settings(&self) -> Result<Settings> {
        let text: Option<String> = self
            .conn
            .query_row("SELECT content FROM settings WHERE id=1", [], |r| r.get(0))
            .optional()
            .map_err(db_error)?;
        match text {
            Some(s) => serde_json::from_str(&s).map_err(|e| format!("设置文件无法读取：{e}")),
            None => Ok(Settings::default()),
        }
    }
    pub fn save_settings(&self, settings: &Settings) -> Result<()> {
        settings.validate()?;
        let mut reserved = settings.clone();
        if settings.sequence_bindings != self.settings()?.sequence_bindings {
            reserved.sequence_hotkeys = true;
        }
        for phrase in self
            .library()?
            .phrases
            .iter()
            .filter(|p| !p.shortcut.is_empty())
        {
            if reserved.shortcut_reserved(&crate::native::parse_shortcut(&phrase.shortcut)?) {
                return Err(format!(
                    "话术「{}」的快捷键与导航或序号快捷键冲突，请先修改。",
                    phrase.label()
                ));
            }
        }
        let old = self.settings()?;
        let tx = self.conn.unchecked_transaction().map_err(db_error)?;
        crate::private_local::save_setting_changes(&tx, &old, settings)?;
        tx.execute("INSERT INTO settings VALUES(1,?1) ON CONFLICT(id) DO UPDATE SET content=excluded.content", [serde_json::to_string(settings).map_err(db_error)?]).map_err(db_error)?;
        tx.commit().map_err(db_error)?;
        Ok(())
    }
    pub fn phrase(&self, id: &str) -> Result<Phrase> {
        let mut p = self.conn.query_row("SELECT id,root_id,group_id,title,color,background,shortcut,position FROM phrases WHERE id=?1", [id], read_phrase).optional().map_err(db_error)?.ok_or("话术已不存在，请刷新列表。")?;
        p.segments = self.segments(id)?;
        Ok(p)
    }
    fn segments(&self, phrase_id: &str) -> Result<Vec<Segment>> {
        let mut stmt = self
            .conn
            .prepare("SELECT id FROM segments WHERE phrase_id=?1 ORDER BY position")
            .map_err(db_error)?;
        let ids = stmt
            .query_map([phrase_id], |r| r.get::<_, String>(0))
            .map_err(db_error)?
            .collect::<std::result::Result<Vec<_>, _>>()
            .map_err(db_error)?;
        let mut result = Vec::new();
        for id in ids {
            let mut stmt = self
                .conn
                .prepare("SELECT content FROM blocks WHERE segment_id=?1 ORDER BY position")
                .map_err(db_error)?;
            let values = stmt
                .query_map([id], |r| r.get::<_, String>(0))
                .map_err(db_error)?
                .collect::<std::result::Result<Vec<_>, _>>()
                .map_err(db_error)?;
            let blocks = values
                .into_iter()
                .map(|v| serde_json::from_str(&v).map_err(|e| format!("话术内容无法读取：{e}")))
                .collect::<Result<Vec<_>>>()?;
            result.push(Segment { blocks });
        }
        Ok(result)
    }
    pub fn library(&self) -> Result<Library> {
        let mut stmt = self.conn.prepare("SELECT id,root_id,group_id,title,color,background,shortcut,position FROM phrases ORDER BY position,rowid").map_err(db_error)?;
        let mut phrases = stmt
            .query_map([], read_phrase)
            .map_err(db_error)?
            .collect::<std::result::Result<Vec<_>, _>>()
            .map_err(db_error)?;
        for p in &mut phrases {
            p.segments = self.segments(&p.id)?;
        }
        let mut stmt = self
            .conn
            .prepare("SELECT id,category_id,path,name,position FROM files ORDER BY position,rowid")
            .map_err(db_error)?;
        let files = stmt
            .query_map([], |r| {
                let path: String = r.get(2)?;
                let missing = !Path::new(&path).is_file();
                Ok(LinkedFile {
                    id: r.get(0)?,
                    category_id: r.get(1)?,
                    path,
                    name: r.get(3)?,
                    position: r.get(4)?,
                    missing,
                })
            })
            .map_err(db_error)?
            .collect::<std::result::Result<Vec<_>, _>>()
            .map_err(db_error)?;
        Ok(Library {
            categories: self.categories()?,
            phrases,
            files,
            common_phrases: self.common_phrases()?,
            settings: self.settings()?,
            data_dir: self.dir.to_string_lossy().into(),
        })
    }
    pub fn common_phrases(&self) -> Result<Vec<CommonPhrase>> {
        let mut stmt = self
            .conn
            .prepare("SELECT id,text,position FROM common_phrases ORDER BY position,rowid")
            .map_err(db_error)?;
        let rows = stmt
            .query_map([], |r| {
                Ok(CommonPhrase {
                    id: r.get(0)?,
                    text: r.get(1)?,
                    position: r.get(2)?,
                })
            })
            .map_err(db_error)?;
        rows.collect::<std::result::Result<Vec<_>, _>>()
            .map_err(db_error)
    }
    pub fn save_common_phrase(&self, mut phrase: CommonPhrase) -> Result<String> {
        validate_common_text(&phrase.text)?;
        if phrase.id.is_empty() {
            phrase.id = id();
            self.conn.execute("INSERT INTO common_phrases VALUES(?1,?2,(SELECT coalesce(max(position),-1)+1 FROM common_phrases))", params![phrase.id, phrase.text]).map_err(db_error)?;
        } else if self
            .conn
            .execute(
                "UPDATE common_phrases SET text=?2 WHERE id=?1",
                params![phrase.id, phrase.text],
            )
            .map_err(db_error)?
            == 0
        {
            return Err("常用短语已不存在，请关闭编辑窗口后重试。".into());
        }
        Ok(phrase.id)
    }
    pub fn reorder_common(&mut self, ids: &[String]) -> Result<()> {
        let tx = self.conn.transaction().map_err(db_error)?;
        let current: std::collections::HashSet<String> = {
            let mut stmt = tx
                .prepare("SELECT id FROM common_phrases")
                .map_err(db_error)?;
            let rows = stmt.query_map([], |row| row.get(0)).map_err(db_error)?;
            rows.collect::<std::result::Result<_, _>>()
                .map_err(db_error)?
        };
        let unique: std::collections::HashSet<&String> = ids.iter().collect();
        if ids.len() != current.len()
            || unique.len() != ids.len()
            || ids.iter().any(|id| !current.contains(id))
        {
            return Err("短语列表已变化，请等待列表更新后重新确认排序。".into());
        }
        for (position, id) in ids.iter().enumerate() {
            tx.execute(
                "UPDATE common_phrases SET position=?2 WHERE id=?1",
                params![id, position as i64],
            )
            .map_err(db_error)?;
        }
        tx.commit().map_err(db_error)
    }
    pub fn search(&self, query: &str) -> Result<Vec<SearchHit>> {
        self.search_matching(&crate::search_match::SearchQuery::new(
            query,
            self.settings()?.search_mode == "exact",
        ))
    }
    pub(crate) fn search_matching(
        &self,
        query: &crate::search_match::SearchQuery,
    ) -> Result<Vec<SearchHit>> {
        let library = self.library()?;
        let mut found = Vec::new();
        for p in library.phrases {
            let mut content = format!("{} {}", p.title, p.text());
            for s in &p.segments {
                for b in &s.blocks {
                    if let Block::File { name, .. } = b {
                        content.push(' ');
                        content.push_str(name);
                    }
                }
            }
            if query.matches(&content) {
                found.push(SearchHit {
                    kind: "phrase".into(),
                    id: p.id.clone(),
                    phrase: Some(p),
                    file: None,
                });
            }
        }
        for f in library.files {
            if query.matches(&f.name) {
                found.push(SearchHit {
                    kind: "file".into(),
                    id: f.id.clone(),
                    phrase: None,
                    file: Some(f),
                });
            }
        }
        Ok(found)
    }
    pub fn save_phrase(&mut self, p: Phrase, after_id: Option<&str>) -> Result<String> {
        self.save_phrase_with_company(p, after_id, &[])
    }
    /// 来源映射与话术同事务提交；失败只清理本次新副本，复用副本不能被回滚删除。
    pub fn save_phrase_with_company(
        &mut self,
        mut p: Phrase,
        after_id: Option<&str>,
        copies: &[(String, String)],
    ) -> Result<String> {
        writable_ids([
            p.id.as_str(),
            p.root_id.as_str(),
            p.group_id.as_deref().unwrap_or(""),
            after_id.unwrap_or(""),
        ])?;
        let materials = crate::private_local::safe_materials(self, &mut p)?;
        validate_phrase(&p)?;
        self.validate_images(&p)?;
        if !p.shortcut.is_empty() {
            p.shortcut = crate::native::parse_shortcut(&p.shortcut)?.canonical();
            if self
                .settings()?
                .shortcut_reserved(&crate::native::parse_shortcut(&p.shortcut)?)
            {
                return Err("该快捷键与导航或已启用的序号快捷键冲突，请更换。".into());
            }
        }
        let tx = self.conn.transaction().map_err(db_error)?;
        check_categories(&tx, &p)?;
        if !p.shortcut.is_empty() {
            if let Some(conflict) =
                shortcut_collision(&tx, &p.id, &crate::native::parse_shortcut(&p.shortcut)?)?
            {
                return Err(format!(
                    "快捷键「{}」与话术「{conflict}」冲突，请修改快捷键或先删除原绑定。",
                    p.shortcut
                ));
            }
        }
        if p.id.is_empty() {
            p.id = id();
            p.position = tx.query_row("SELECT coalesce(max(position),-1)+1 FROM phrases WHERE root_id=?1 AND coalesce(group_id,'')=coalesce(?2,'')", params![p.root_id,p.group_id], |r| r.get(0)).map_err(db_error)?;
            if let Some(after) = after_id {
                if let Some(pos) = tx.query_row("SELECT position FROM phrases WHERE id=?1 AND root_id=?2 AND coalesce(group_id,'')=coalesce(?3,'')", params![after,p.root_id,p.group_id], |r| r.get::<_,i64>(0)).optional().map_err(db_error)? {
                    p.position = pos + 1;
                    tx.execute("UPDATE phrases SET position=position+1 WHERE root_id=?1 AND coalesce(group_id,'')=coalesce(?2,'') AND position>=?3", params![p.root_id,p.group_id,p.position]).map_err(db_error)?;
                }
            }
        } else {
            let old: Option<(String, Option<String>, i64)> = tx
                .query_row(
                    "SELECT root_id,group_id,position FROM phrases WHERE id=?1",
                    [&p.id],
                    |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
                )
                .optional()
                .map_err(db_error)?;
            let (root, group, position) = old.ok_or("话术已被删除，无法保存。")?;
            // 顺序由数据库维护。编辑旧窗口不会覆盖后来排序，跨分类移动追加到目标分类末尾。
            p.position = if root == p.root_id && group == p.group_id {
                position
            } else {
                tx.query_row("SELECT coalesce(max(position),-1)+1 FROM phrases WHERE root_id=?1 AND coalesce(group_id,'')=coalesce(?2,'')",params![p.root_id,p.group_id],|r|r.get(0)).map_err(db_error)?
            };
        }
        write_phrase(&tx, &p)?;
        for (source, path) in copies {
            tx.execute("INSERT INTO company_copies VALUES(?1,?2) ON CONFLICT(source) DO UPDATE SET path=excluded.path",params![source,path]).map_err(db_error)?;
        }
        tx.commit().map_err(db_error)?;
        materials.keep();
        Ok(p.id)
    }
    pub(crate) fn validate_images(&self, p: &Phrase) -> Result<()> {
        let root = self.dir.join("images").canonicalize().map_err(db_error)?;
        for b in p.segments.iter().flat_map(|s| &s.blocks) {
            if let Block::Image { path, .. } = b {
                if crate::private_local::bound(self) && !Path::new(path).exists() {
                    let known:bool=self.conn.query_row("SELECT EXISTS(SELECT 1 FROM sync_assets WHERE path=?1 AND kind='image')",[path],|r|r.get(0)).map_err(db_error)?;
                    if known {
                        continue;
                    }
                }
                let file = Path::new(path)
                    .canonicalize()
                    .map_err(|_| "图片素材已失效，请重新添加。".to_string())?;
                if !file.starts_with(&root) || !file.is_file() {
                    return Err("图片必须来自闪语的素材目录。".into());
                }
            }
            if let Block::File { path, .. } = b {
                if !Path::new(path).is_absolute() {
                    return Err("文件关联必须使用完整路径。".into());
                }
            }
        }
        Ok(())
    }
    pub fn save_category(&self, mut c: Category) -> Result<String> {
        writable_ids([c.id.as_str(), c.parent_id.as_deref().unwrap_or("")])?;
        c.name = c.name.trim().into();
        if c.name.is_empty()
            || c.name.chars().count() > 40
            || !["phrase", "file"].contains(&c.kind.as_str())
        {
            return Err("分类名称应为1—40个字。".into());
        }
        if !valid_category_background(&c.background) {
            return Err("请选择有效的分类背景色。".into());
        }
        if let Some(parent) = &c.parent_id {
            let valid = self.conn.query_row("SELECT count(*) FROM categories WHERE id=?1 AND parent_id IS NULL AND kind='phrase'", [parent], |r| r.get::<_,i64>(0)).map_err(db_error)?;
            if valid == 0 || c.kind != "phrase" || parent == &c.id {
                return Err("二级分类必须属于话术一级分类。".into());
            }
        }
        if c.id.is_empty() {
            c.id = id();
            c.position = self
                .conn
                .query_row(
                    "SELECT coalesce(max(position),-1)+1 FROM categories",
                    [],
                    |r| r.get(0),
                )
                .map_err(db_error)?;
            self.conn
                .execute(
                    "INSERT INTO categories VALUES(?1,?2,?3,?4,?5,?6)",
                    params![c.id, c.name, c.kind, c.parent_id, c.position, c.background],
                )
                .map_err(|e| format!("无法新增分类：{e}"))?;
        } else {
            // 分类编辑不改变父级，移动话术通过明确的移动接口完成。
            let changed = self
                .conn
                .execute(
                    "UPDATE categories SET name=?2,background=?3 WHERE id=?1",
                    params![c.id, c.name, c.background],
                )
                .map_err(|e| format!("无法修改分类：{e}"))?;
            if changed == 0 {
                return Err("分类已不存在，请刷新后重试。".into());
            }
        }
        Ok(c.id)
    }
    /// 保留话术时先在同一事务中解除分类外键，再删除分类，避免级联删除内容。
    /// 二级分类的话术归入原一级分类；一级分类的话术平移到用户明确选择的目标，不保留分组。
    pub fn delete_category(
        &mut self,
        category_id: &str,
        keep_phrases: bool,
        target_id: Option<&str>,
        recycle: bool,
    ) -> Result<()> {
        writable_ids([category_id, target_id.unwrap_or("")])?;
        let c = self
            .categories()?
            .into_iter()
            .find(|c| c.id == category_id)
            .ok_or("分类已不存在。")?;
        let mut phrases = if keep_phrases && c.kind == "phrase" {
            self.library()?
                .phrases
                .into_iter()
                .filter(|p| {
                    if c.parent_id.is_some() {
                        p.group_id.as_deref() == Some(category_id)
                    } else {
                        p.root_id == category_id
                    }
                })
                .collect::<Vec<_>>()
        } else {
            Vec::new()
        };
        if c.parent_id.is_none() {
            let groups = self
                .categories()?
                .into_iter()
                .filter(|group| group.parent_id.as_deref() == Some(category_id))
                .map(|group| group.id)
                .collect::<Vec<_>>();
            phrases.sort_by_key(|p| {
                p.group_id.as_ref().map_or(0, |group| {
                    groups
                        .iter()
                        .position(|id| id == group)
                        .map_or(usize::MAX, |i| i + 1)
                })
            });
        }
        let recycled = if recycle && !keep_phrases && c.kind == "phrase" {
            Some(self.recycle_snapshot("category", category_id)?)
        } else {
            None
        };
        let tx = self.conn.transaction().map_err(db_error)?;
        if let Some(record) = recycled {
            crate::recycle::write_record(&tx, &record)?;
        }
        if keep_phrases {
            if c.kind != "phrase" {
                return Err("文件分类不支持保留话术删除。".into());
            }
            let target = c
                .parent_id
                .as_deref()
                .or(target_id)
                .ok_or("请选择保留话术的目标一级分类。")?;
            if target == category_id {
                return Err("目标分类不能是正在删除的分类。".into());
            }
            let valid: i64 = tx.query_row("SELECT count(*) FROM categories WHERE id=?1 AND kind='phrase' AND parent_id IS NULL", [target], |r| r.get(0)).map_err(db_error)?;
            if valid == 0 {
                return Err("请选择有效的目标一级分类。".into());
            }
            for p in phrases {
                let position: i64 = tx.query_row("SELECT coalesce(max(position),-1)+1 FROM phrases WHERE root_id=?1 AND group_id IS NULL", [target], |r| r.get(0)).map_err(db_error)?;
                tx.execute(
                    "UPDATE phrases SET root_id=?2,group_id=NULL,position=?3 WHERE id=?1",
                    params![p.id, target, position],
                )
                .map_err(db_error)?;
            }
        }
        tx.execute("DELETE FROM categories WHERE id=?1", [category_id])
            .map_err(db_error)?;
        tx.commit().map_err(db_error)
    }
    /// 分类复制/移动仅在个人话术库内进行。同名二级分类合并，内容追加到目标末尾。
    /// 目标有多个同名分组时选择列表中最靠前的一组，不改动其余同名分组。
    /// 移动保留话术主键和快捷键，复制生成新主键并清空快捷键；整批失败时全部回滚。
    /// 图片仍引用用户素材目录，外部附件仅复制关联，既不删除也不改写源文件。
    pub fn move_copy_category(
        &mut self,
        category_id: &str,
        target_id: &str,
        copy: bool,
    ) -> Result<usize> {
        writable_ids([category_id, target_id])?;
        let library = self.library()?;
        let source = library
            .categories
            .iter()
            .find(|c| c.id == category_id && c.kind == "phrase")
            .ok_or("来源话术分类已不存在。")?;
        if !library
            .categories
            .iter()
            .any(|c| c.id == target_id && c.kind == "phrase" && c.parent_id.is_none())
        {
            return Err("请选择有效的目标一级分类。".into());
        }
        if target_id == source.id || source.parent_id.as_deref() == Some(target_id) {
            return Err("请选择其他一级分类。".into());
        }
        let groups = library
            .categories
            .iter()
            .filter(|c| {
                c.parent_id.as_deref() == Some(category_id)
                    || (source.parent_id.is_some() && c.id == category_id)
            })
            .collect::<Vec<_>>();
        let phrases = library
            .phrases
            .into_iter()
            .filter(|p| {
                if source.parent_id.is_some() {
                    p.group_id.as_deref() == Some(category_id)
                } else {
                    p.root_id == category_id
                }
            })
            .collect::<Vec<_>>();
        let count = phrases.len();
        let tx = self.conn.transaction().map_err(db_error)?;
        let mut map = HashMap::new();
        for group in groups {
            let existing: Option<String> = tx.query_row("SELECT id FROM categories WHERE kind='phrase' AND parent_id=?1 AND name=?2 COLLATE NOCASE ORDER BY position,rowid LIMIT 1", params![target_id, group.name], |r| r.get(0)).optional().map_err(db_error)?;
            let target_group = if let Some(existing) = existing {
                existing
            } else {
                let position: i64 = tx
                    .query_row(
                        "SELECT coalesce(max(position),-1)+1 FROM categories WHERE parent_id=?1",
                        [target_id],
                        |r| r.get(0),
                    )
                    .map_err(db_error)?;
                if copy {
                    let new_id = id();
                    tx.execute(
                        "INSERT INTO categories VALUES(?1,?2,'phrase',?3,?4,?5)",
                        params![new_id, group.name, target_id, position, group.background],
                    )
                    .map_err(db_error)?;
                    new_id
                } else {
                    tx.execute(
                        "UPDATE categories SET parent_id=?2,position=?3 WHERE id=?1",
                        params![group.id, target_id, position],
                    )
                    .map_err(db_error)?;
                    group.id.clone()
                }
            };
            map.insert(group.id.clone(), target_group);
        }
        for mut p in phrases {
            p.root_id = target_id.into();
            p.group_id = p
                .group_id
                .as_ref()
                .map(|group| map.get(group).cloned().ok_or("来源话术的分组无效。"))
                .transpose()?;
            p.position = tx.query_row("SELECT coalesce(max(position),-1)+1 FROM phrases WHERE root_id=?1 AND coalesce(group_id,'')=coalesce(?2,'')", params![p.root_id, p.group_id], |r| r.get(0)).map_err(db_error)?;
            check_categories(&tx, &p)?;
            if copy {
                p.id = id();
                p.shortcut.clear();
                write_phrase(&tx, &p)?;
            } else {
                tx.execute(
                    "UPDATE phrases SET root_id=?2,group_id=?3,position=?4 WHERE id=?1",
                    params![p.id, p.root_id, p.group_id, p.position],
                )
                .map_err(db_error)?;
            }
        }
        if !copy {
            // 未合并的分组已迁移，合并的分组已清空话术，可安全删除来源分类。
            for (old, new) in &map {
                if old != new {
                    tx.execute("DELETE FROM categories WHERE id=?1", [old])
                        .map_err(db_error)?;
                }
            }
            if source.parent_id.is_none() {
                tx.execute("DELETE FROM categories WHERE id=?1", [category_id])
                    .map_err(db_error)?;
            }
        }
        tx.commit().map_err(db_error)?;
        Ok(count)
    }
    /// 快速排序只接受当前完整的同级分类集合，防止旧窗口漏掉新分类或跨层级写入。
    pub fn reorder_categories(
        &mut self,
        ids: &[String],
        kind: &str,
        parent_id: Option<&str>,
    ) -> Result<()> {
        writable_ids(ids.iter().map(String::as_str).chain(parent_id))?;
        if let Some(parent) = parent_id {
            let valid: i64 = self.conn.query_row("SELECT count(*) FROM categories WHERE id=?1 AND kind='phrase' AND parent_id IS NULL", [parent], |r| r.get(0)).map_err(db_error)?;
            if valid == 0 || kind != "phrase" {
                return Err("原一级分类已不存在，请关闭排序窗口。".into());
            }
        }
        let current: std::collections::HashSet<String> = self
            .categories()?
            .into_iter()
            .filter(|c| c.kind == kind && c.parent_id.as_deref() == parent_id)
            .map(|c| c.id)
            .collect();
        let unique: std::collections::HashSet<&String> = ids.iter().collect();
        if !["phrase", "file"].contains(&kind)
            || ids.len() != current.len()
            || unique.len() != ids.len()
            || ids.iter().any(|id| !current.contains(id))
        {
            return Err("分类列表已变化，请关闭排序窗口后重新打开。".into());
        }
        let tx = self.conn.transaction().map_err(db_error)?;
        for (position, id) in ids.iter().enumerate() {
            tx.execute(
                "UPDATE categories SET position=?2 WHERE id=?1",
                params![id, position as i64],
            )
            .map_err(db_error)?;
        }
        tx.commit().map_err(db_error)
    }
    pub fn delete_item(&mut self, item_id: &str, kind: &str, recycle: bool) -> Result<()> {
        writable_ids([item_id])?;
        let table = match kind {
            "phrase" => "phrases",
            "file" => "files",
            "common" => "common_phrases",
            _ => return Err("内容类型无效。".into()),
        };
        let recycled = if recycle && kind == "phrase" {
            Some(self.recycle_snapshot("phrase", item_id)?)
        } else {
            None
        };
        let tx = self.conn.transaction().map_err(db_error)?;
        if let Some(record) = recycled {
            crate::recycle::write_record(&tx, &record)?;
        }
        let changed = tx
            .execute(&format!("DELETE FROM {table} WHERE id=?1"), [item_id])
            .map_err(db_error)?;
        if changed == 0 {
            return Err("内容已不存在，请刷新后重试。".into());
        }
        tx.commit().map_err(db_error)
    }
    /// 缓存管理仅删除已确认的个人话术或常用短语，不删除分类、素材、文件关联及旧回收记录。
    /// 以当前完整主键集合校验确认范围，跨窗口增删后要求重新确认；回收与删除在同一事务内完成。
    pub fn delete_local_content(
        &mut self,
        kind: &str,
        ids: &[String],
        recycle: bool,
    ) -> Result<usize> {
        writable_ids(ids.iter().map(String::as_str))?;
        let table = match kind {
            "phrase" => "phrases",
            "common" => "common_phrases",
            _ => return Err("只能删除个人话术或常用短语。".into()),
        };
        let records = if kind == "phrase" && recycle {
            let library = self.library()?;
            library
                .phrases
                .iter()
                .map(|p| crate::recycle::phrase_snapshot(p, &library.categories))
                .collect::<Result<Vec<_>>>()?
        } else {
            Vec::new()
        };
        let tx = self.conn.transaction().map_err(db_error)?;
        let current = {
            let mut stmt = tx
                .prepare(&format!("SELECT id FROM {table}"))
                .map_err(db_error)?;
            let rows = stmt
                .query_map([], |r| r.get::<_, String>(0))
                .map_err(db_error)?;
            rows.collect::<std::result::Result<Vec<_>, _>>()
                .map_err(db_error)?
        };
        let confirmed: HashSet<_> = ids.iter().collect();
        if confirmed.len() != ids.len()
            || current.len() != ids.len()
            || current.iter().any(|id| !confirmed.contains(id))
        {
            return Err("内容列表已变化，请关闭确认窗口后重新操作。".into());
        }
        for record in records {
            crate::recycle::write_record(&tx, &record)?;
        }
        let count = tx
            .execute(&format!("DELETE FROM {table}"), [])
            .map_err(db_error)?;
        tx.commit().map_err(db_error)?;
        Ok(count)
    }
    pub fn move_copy(
        &mut self,
        item_id: &str,
        root_id: String,
        group_id: Option<String>,
        copy: bool,
    ) -> Result<String> {
        writable_ids([item_id, root_id.as_str(), group_id.as_deref().unwrap_or("")])?;
        let mut p = self.phrase(item_id)?;
        p.root_id = root_id;
        p.group_id = group_id;
        if copy {
            p.id.clear();
            p.shortcut.clear();
        }
        self.save_phrase(p, None)
    }
    pub fn sort(&mut self, item_id: &str, kind: &str, position: usize) -> Result<()> {
        writable_ids([item_id])?;
        let library = self.library()?;
        let (table, mut ids): (&str, Vec<String>) = match kind {
            "common" => {
                if !library.common_phrases.iter().any(|p| p.id == item_id) {
                    return Err("常用短语已不存在。".into());
                }
                (
                    "common_phrases",
                    library
                        .common_phrases
                        .iter()
                        .map(|p| p.id.clone())
                        .collect(),
                )
            }
            "phrase" => {
                let p = library
                    .phrases
                    .iter()
                    .find(|p| p.id == item_id)
                    .ok_or("话术不存在。")?;
                (
                    "phrases",
                    library
                        .phrases
                        .iter()
                        .filter(|v| v.root_id == p.root_id && v.group_id == p.group_id)
                        .map(|v| v.id.clone())
                        .collect(),
                )
            }
            "file" => {
                let f = library
                    .files
                    .iter()
                    .find(|f| f.id == item_id)
                    .ok_or("文件关联不存在。")?;
                (
                    "files",
                    library
                        .files
                        .iter()
                        .filter(|v| v.category_id == f.category_id)
                        .map(|v| v.id.clone())
                        .collect(),
                )
            }
            "category" => {
                let c = library
                    .categories
                    .iter()
                    .find(|c| c.id == item_id)
                    .ok_or("分类不存在。")?;
                (
                    "categories",
                    library
                        .categories
                        .iter()
                        .filter(|v| v.kind == c.kind && v.parent_id == c.parent_id)
                        .map(|v| v.id.clone())
                        .collect(),
                )
            }
            _ => return Err("排序类型无效。".into()),
        };
        ids.retain(|i| i != item_id);
        ids.insert(
            position.saturating_sub(1).min(ids.len()),
            item_id.to_string(),
        );
        let tx = self.conn.transaction().map_err(db_error)?;
        for (i, id) in ids.iter().enumerate() {
            tx.execute(
                &format!("UPDATE {table} SET position=?2 WHERE id=?1"),
                params![id, i as i64],
            )
            .map_err(db_error)?;
        }
        tx.commit().map_err(db_error)
    }
    pub fn add_files(&mut self, category: &str, paths: Vec<String>) -> Result<()> {
        writable_ids([category])?;
        let tx = self.conn.transaction().map_err(db_error)?;
        check_file_category(&tx, category)?;
        let position: i64 = tx
            .query_row(
                "SELECT coalesce(max(position),-1)+1 FROM files WHERE category_id=?1",
                [category],
                |r| r.get(0),
            )
            .map_err(db_error)?;
        for (offset, path) in paths.into_iter().enumerate() {
            let path = Path::new(&path)
                .canonicalize()
                .map_err(|e| format!("文件无法关联：{e}"))?;
            if !path.is_file() {
                return Err("请选择文件，不能关联文件夹。".into());
            }
            let name = path.file_name().unwrap_or_default().to_string_lossy();
            tx.execute(
                "INSERT INTO files VALUES(?1,?2,?3,?4,?5)",
                params![
                    id(),
                    category,
                    external_path(&path),
                    name,
                    position + offset as i64
                ],
            )
            .map_err(db_error)?;
        }
        tx.commit().map_err(db_error)
    }
    pub fn relink(&self, item_id: &str, path: &str, category: &str) -> Result<()> {
        writable_ids([item_id, category])?;
        check_file_category(&self.conn, category)?;
        let path = Path::new(path)
            .canonicalize()
            .map_err(|e| format!("文件无法关联：{e}"))?;
        if !path.is_file() {
            return Err("请选择有效的文件。".into());
        }
        self.conn
            .execute(
                "UPDATE files SET path=?2,name=?3,category_id=?4 WHERE id=?1",
                params![
                    item_id,
                    external_path(&path),
                    path.file_name().unwrap_or_default().to_string_lossy(),
                    category
                ],
            )
            .map_err(db_error)?;
        Ok(())
    }
    /// 导入时重新分配主键；不合并时保留原名称与独立层级，合并时匹配同级同名的第一项。
    /// 任意一条无效都会回滚整个批次，包括重复分类编号，避免内容映射到错误分类。
    pub fn import(
        &mut self,
        data: &Library,
        merge: bool,
        restore_settings: bool,
        recycled: &[RecycleRecord],
    ) -> Result<()> {
        self.import_inner(data, merge, restore_settings, recycled, None, false)
    }
    /// 已预览的导入使用固定副本编号；批次回执和正文、待同步键同事务保存。
    pub fn import_prepared(
        &mut self,
        data: &Library,
        merge: bool,
        restore_settings: bool,
        recycled: &[RecycleRecord],
        batch: &str,
        device_settings_only: bool,
    ) -> Result<()> {
        self.import_inner(
            data,
            merge,
            restore_settings,
            recycled,
            Some(batch),
            device_settings_only,
        )
    }
    pub fn imported(&self, batch: &str) -> Result<bool> {
        self.conn
            .query_row(
                "SELECT EXISTS(SELECT 1 FROM import_batches WHERE id=?1)",
                [batch],
                |r| r.get(0),
            )
            .map_err(db_error)
    }
    fn import_inner(
        &mut self,
        data: &Library,
        merge: bool,
        restore_settings: bool,
        recycled: &[RecycleRecord],
        batch: Option<&str>,
        device_settings_only: bool,
    ) -> Result<()> {
        if let Some(batch) = batch {
            if self.imported(batch)? {
                return Ok(());
            }
        }
        for p in data
            .phrases
            .iter()
            .chain(recycled.iter().flat_map(|r| &r.phrases))
        {
            self.validate_images(p)?;
        }
        let settings = if restore_settings {
            data.settings.clone()
        } else {
            self.settings()?
        };
        let previous_settings = self.settings()?;
        // 恢复便携配置按库内期望值比较；即使导入值恰好等于已应用的旧值，也要替换待确认的新值。
        let previous_desired = if batch.is_some() && restore_settings {
            let mut value = serde_json::to_value(&previous_settings).map_err(db_error)?;
            for ((kind, key), next) in crate::private_local::local_records(self)? {
                if kind == shanyu_team_model::private::Kind::Setting {
                    if let Some(next) = next {
                        value[key] = next;
                    }
                }
            }
            serde_json::from_value(value).map_err(db_error)?
        } else {
            previous_settings.clone()
        };
        let tx = self.conn.transaction().map_err(db_error)?;
        let mut map = HashMap::new();
        for c in data
            .categories
            .iter()
            .filter(|c| c.parent_id.is_none())
            .chain(data.categories.iter().filter(|c| c.parent_id.is_some()))
        {
            let parent = match &c.parent_id {
                Some(p) => Some(map.get(p).cloned().ok_or("导入文件中的分类层级无效。")?),
                None => None,
            };
            if !["phrase", "file"].contains(&c.kind.as_str())
                || c.name.trim().is_empty()
                || c.name.chars().count() > 40
                || (parent.is_some() && c.kind == "file")
                || !valid_category_background(&c.background)
            {
                return Err("导入文件中的分类无效。".into());
            }
            let existing: Option<String> = if merge {
                let matches = tx.prepare("SELECT id FROM categories WHERE kind=?1 AND coalesce(parent_id,'')=coalesce(?2,'') AND name=?3 COLLATE NOCASE ORDER BY position,rowid LIMIT 2").map_err(db_error)?
                    .query_map(params![c.kind,parent,c.name], |r| r.get::<_, String>(0)).map_err(db_error)?.collect::<std::result::Result<Vec<_>, _>>().map_err(db_error)?;
                if matches.len() == 1 || (batch.is_none() && !matches.is_empty()) {
                    matches.first().cloned()
                } else {
                    None
                }
            } else {
                None
            };
            let new_id = if let Some(id) = existing {
                if restore_settings {
                    tx.execute(
                        "UPDATE categories SET background=?2 WHERE id=?1",
                        params![id, c.background],
                    )
                    .map_err(db_error)?;
                }
                id
            } else {
                let new_id = if batch.is_some() { c.id.clone() } else { id() };
                let pos: i64 = tx
                    .query_row(
                        "SELECT coalesce(max(position),-1)+1 FROM categories",
                        [],
                        |r| r.get(0),
                    )
                    .map_err(db_error)?;
                tx.execute(
                    "INSERT INTO categories VALUES(?1,?2,?3,?4,?5,?6)",
                    params![new_id, c.name, c.kind, parent, pos, c.background],
                )
                .map_err(db_error)?;
                new_id
            };
            if map.insert(c.id.clone(), new_id).is_some() {
                return Err("导入文件包含重复分类编号。".into());
            }
        }
        for original in &data.phrases {
            let mut p = original.clone();
            if batch.is_none() {
                p.id = id();
            }
            p.root_id = map
                .get(&p.root_id)
                .cloned()
                .ok_or("导入话术缺少一级分类。")?;
            p.group_id = match &p.group_id {
                Some(g) => Some(map.get(g).cloned().ok_or("导入话术的二级分类无效。")?),
                None => None,
            };
            validate_phrase(&p)?;
            if !p.shortcut.is_empty() {
                p.shortcut = crate::native::parse_shortcut(&p.shortcut)?.canonical();
            }
            check_categories(&tx, &p)?;
            if !p.shortcut.is_empty() {
                let shortcut = crate::native::parse_shortcut(&p.shortcut)?;
                if settings.shortcut_reserved(&shortcut) {
                    p.shortcut.clear();
                }
            }
            // 导入保留内容，冲突快捷键清空，由预览提醒用户重新设置。
            if !p.shortcut.is_empty()
                && shortcut_collision(&tx, &p.id, &crate::native::parse_shortcut(&p.shortcut)?)?
                    .is_some()
            {
                p.shortcut.clear();
            }
            p.position = tx
                .query_row(
                    "SELECT coalesce(max(position),-1)+1 FROM phrases",
                    [],
                    |r| r.get(0),
                )
                .map_err(db_error)?;
            write_phrase(&tx, &p)?;
        }
        for common in &data.common_phrases {
            validate_common_text(&common.text)?;
            let key = if batch.is_some() {
                common.id.clone()
            } else {
                id()
            };
            tx.execute("INSERT INTO common_phrases VALUES(?1,?2,(SELECT coalesce(max(position),-1)+1 FROM common_phrases))", params![key, common.text]).map_err(db_error)?;
        }
        for f in &data.files {
            let category = map.get(&f.category_id).ok_or("导入文件关联缺少分类。")?;
            check_file_category(&tx, category)?;
            if !Path::new(&f.path).is_absolute() {
                return Err("导入文件关联使用了无效路径。".into());
            }
            let key = if batch.is_some() { f.id.clone() } else { id() };
            tx.execute("INSERT INTO files VALUES(?1,?2,?3,?4,(SELECT coalesce(max(position),-1)+1 FROM files))",params![key,category,f.path,f.name]).map_err(db_error)?;
        }
        if restore_settings {
            data.settings.validate()?;
            // 设备部分不触碰个人设置的待确认值，否则恢复设备设置可能取消尚未应用的发送偏好。
            if !device_settings_only {
                crate::private_local::save_setting_changes(&tx, &previous_desired, &data.settings)?;
            }
            let mut applied = serde_json::to_value(&data.settings).map_err(db_error)?;
            if batch.is_some() {
                let old = serde_json::to_value(&previous_settings).map_err(db_error)?;
                for key in shanyu_team_model::private::SETTINGS
                    .iter()
                    .filter(|key| shanyu_team_model::private::requires_confirmation(key))
                {
                    applied[*key] = old[*key].clone();
                }
            }
            tx.execute("INSERT INTO settings VALUES(1,?1) ON CONFLICT(id) DO UPDATE SET content=excluded.content",[applied.to_string()]).map_err(db_error)?;
        }
        crate::recycle::import_records(&tx, recycled, &map, batch.is_some())?;
        if let Some(batch) = batch {
            tx.execute("INSERT INTO import_batches VALUES(?1)", [batch])
                .map_err(db_error)?;
        }
        tx.commit().map_err(db_error)
    }
}
pub(crate) fn valid_category_background(color: &str) -> bool {
    color.is_empty()
        || (color.len() == 7
            && color.starts_with('#')
            && color[1..].bytes().all(|b| b.is_ascii_hexdigit()))
}
fn read_phrase(r: &rusqlite::Row<'_>) -> rusqlite::Result<Phrase> {
    Ok(Phrase {
        id: r.get(0)?,
        root_id: r.get(1)?,
        group_id: r.get(2)?,
        title: r.get(3)?,
        color: r.get(4)?,
        background: r.get(5)?,
        shortcut: r.get(6)?,
        position: r.get(7)?,
        segments: vec![],
    })
}
pub(crate) fn check_categories(conn: &Connection, p: &Phrase) -> Result<()> {
    let root: i64 = conn
        .query_row(
            "SELECT count(*) FROM categories WHERE id=?1 AND kind='phrase' AND parent_id IS NULL",
            [&p.root_id],
            |r| r.get(0),
        )
        .map_err(db_error)?;
    if root == 0 {
        return Err("请选择有效的一级分类。".into());
    }
    if let Some(group) = &p.group_id {
        if conn
            .query_row(
                "SELECT count(*) FROM categories WHERE id=?1 AND parent_id=?2 AND kind='phrase'",
                params![group, p.root_id],
                |r| r.get::<_, i64>(0),
            )
            .map_err(db_error)?
            == 0
        {
            return Err("二级分类不属于所选一级分类。".into());
        }
    }
    Ok(())
}
fn check_file_category(conn: &Connection, id: &str) -> Result<()> {
    if conn
        .query_row(
            "SELECT count(*) FROM categories WHERE id=?1 AND kind='file' AND parent_id IS NULL",
            [id],
            |r| r.get::<_, i64>(0),
        )
        .map_err(db_error)?
        == 0
    {
        return Err("请选择有效的文件分类。".into());
    }
    Ok(())
}
/// 同键形成候选组；此处只检查单键与双键序列的前缀歧义。
/// 保存、还原与导入均在事务中使用相同规则，不依赖编辑器的提前校验。
pub(crate) fn shortcut_collision(
    conn: &Connection,
    id: &str,
    shortcut: &crate::native::Shortcut,
) -> Result<Option<String>> {
    let mut stmt = conn.prepare("SELECT title,shortcut FROM phrases WHERE id<>?1 AND shortcut<>'' ORDER BY position,rowid").map_err(db_error)?;
    let rows = stmt
        .query_map([id], |r| {
            Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?))
        })
        .map_err(db_error)?;
    for row in rows {
        let (title, existing) = row.map_err(db_error)?;
        if shortcut.prefix_conflicts(&crate::native::parse_shortcut(&existing)?) {
            return Ok(Some(if title.is_empty() { existing } else { title }));
        }
    }
    Ok(None)
}
pub(crate) fn write_phrase(tx: &Transaction<'_>, p: &Phrase) -> Result<()> {
    tx.execute("INSERT INTO phrases VALUES(?1,?2,?3,?4,?5,?6,?7,?8) ON CONFLICT(id) DO UPDATE SET root_id=excluded.root_id,group_id=excluded.group_id,title=excluded.title,color=excluded.color,background=excluded.background,shortcut=excluded.shortcut,position=excluded.position",params![p.id,p.root_id,p.group_id,p.title,p.color,p.background,p.shortcut,p.position]).map_err(|e|format!("话术保存失败，快捷键可能重复：{e}"))?;
    tx.execute("DELETE FROM segments WHERE phrase_id=?1", [&p.id])
        .map_err(db_error)?;
    for (si, s) in p.segments.iter().enumerate() {
        let sid = id();
        tx.execute(
            "INSERT INTO segments VALUES(?1,?2,?3)",
            params![sid, p.id, si as i64],
        )
        .map_err(db_error)?;
        for (bi, b) in s.blocks.iter().enumerate() {
            tx.execute(
                "INSERT INTO blocks VALUES(?1,?2,?3,?4)",
                params![
                    id(),
                    sid,
                    bi as i64,
                    serde_json::to_string(b).map_err(db_error)?
                ],
            )
            .map_err(db_error)?;
        }
    }
    Ok(())
}

#[derive(serde::Serialize)]
pub struct SearchHit {
    pub kind: String,
    pub id: String,
    pub phrase: Option<Phrase>,
    pub file: Option<LinkedFile>,
}

#[cfg(test)]
/// 既有业务测试显式准备分类夹具，避免把测试数据当成产品首次启动的默认内容。
pub(crate) fn test_store(dir: &Path) -> Result<Store> {
    let store = Store::open(dir)?;
    store.conn.execute("INSERT INTO categories(id,name,kind,parent_id,position) VALUES('common','常用','phrase',NULL,0),('local-files','默认','file',NULL,0)", []).map_err(db_error)?;
    Ok(store)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn new_library_stays_empty_across_reopens() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        let library = store.library().unwrap();
        assert!(library.categories.is_empty());
        assert!(library.phrases.is_empty());
        assert!(library.files.is_empty());
        drop(store);
        assert!(Store::open(dir.path())
            .unwrap()
            .categories()
            .unwrap()
            .is_empty());
    }

    #[test]
    fn last_categories_can_be_deleted_and_recycled_phrases_restored() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = Store::open(dir.path()).unwrap();
        let root = category(&store, "自己创建", None);
        let mut phrase = sample();
        phrase.root_id = root.clone();
        let phrase_id = store.save_phrase(phrase, None).unwrap();
        let source = dir.path().join("源文件.txt");
        std::fs::write(&source, "保留源文件").unwrap();
        let files = store
            .save_category(Category {
                id: String::new(),
                name: "我的文件".into(),
                kind: "file".into(),
                parent_id: None,
                position: 0,
                background: String::new(),
            })
            .unwrap();
        store
            .add_files(&files, vec![source.to_string_lossy().into()])
            .unwrap();
        assert!(store.delete_category(&root, true, None, false).is_err());
        assert!(store.phrase(&phrase_id).is_ok());
        store.delete_category(&root, false, None, true).unwrap();
        store.delete_category(&files, false, None, false).unwrap();
        assert!(store.categories().unwrap().is_empty());
        assert_eq!(std::fs::read_to_string(&source).unwrap(), "保留源文件");
        let record = store.recycle_records().unwrap()[0].id.clone();
        drop(store);
        let mut reopened = Store::open(dir.path()).unwrap();
        assert!(reopened.categories().unwrap().is_empty());
        reopened.restore_recycled(&[record], false).unwrap();
        assert_eq!(reopened.phrase(&phrase_id).unwrap().root_id, root);
        assert_eq!(reopened.categories().unwrap().len(), 1);
        reopened.delete_category(&root, false, None, false).unwrap();
        drop(reopened);
        assert!(Store::open(dir.path())
            .unwrap()
            .categories()
            .unwrap()
            .is_empty());
    }

    pub fn sample() -> Phrase {
        Phrase {
            id: String::new(),
            root_id: "common".into(),
            group_id: None,
            title: "欢迎".into(),
            color: "#ff5100".into(),
            background: String::new(),
            shortcut: String::new(),
            position: 0,
            segments: vec![Segment {
                blocks: vec![Block::Text {
                    text: "您好".into(),
                }],
            }],
        }
    }
    #[test]
    fn member_category_namespace_is_rejected_before_personal_side_effects() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = test_store(dir.path()).unwrap();
        let own = store.save_phrase(sample(), None).unwrap();
        let before = serde_json::to_value(store.library().unwrap()).unwrap();
        let followed = "member-category:grant:common";
        let reference = "member:grant:1:1:generation:phrase:1";
        let mut phrase = sample();
        phrase.id = reference.into();
        assert!(store
            .save_phrase(phrase, None)
            .unwrap_err()
            .contains("只读"));
        let mut phrase = sample();
        phrase.root_id = followed.into();
        assert!(store
            .save_phrase(phrase, None)
            .unwrap_err()
            .contains("只读"));
        let mut phrase = sample();
        phrase.group_id = Some(followed.into());
        assert!(store.save_phrase(phrase, None).is_err());
        assert!(store.save_phrase(sample(), Some(reference)).is_err());
        let mut category = store.categories().unwrap()[0].clone();
        category.id = followed.into();
        assert!(store.save_category(category).is_err());
        assert!(store.move_copy(&own, followed.into(), None, false).is_err());
        assert!(store
            .move_copy(reference, "common".into(), None, true)
            .is_err());
        assert!(store.move_copy_category("common", followed, true).is_err());
        assert!(store.delete_category(followed, false, None, true).is_err());
        assert!(store.delete_item(reference, "phrase", true).is_err());
        assert!(store.sort(reference, "phrase", 1).is_err());
        assert!(store
            .reorder_categories(&[followed.into()], "phrase", None)
            .is_err());
        assert!(store
            .delete_local_content("phrase", &[reference.into()], true)
            .is_err());
        assert_eq!(
            serde_json::to_value(store.library().unwrap()).unwrap(),
            before
        );
        assert!(store.recycle_records().unwrap().is_empty());
    }
    #[test]
    fn persistence_and_unicode_limit() {
        let dir = tempfile::tempdir().unwrap();
        let mut s = crate::store::test_store(dir.path()).unwrap();
        assert_eq!(
            s.conn
                .pragma_query_value(None, "user_version", |r| r.get::<_, u32>(0))
                .unwrap(),
            1
        );
        let mut p = sample();
        p.title = "😀".repeat(200);
        let id = s.save_phrase(p.clone(), None).unwrap();
        p.title.push('字');
        assert!(s.save_phrase(p, None).is_err());
        drop(s);
        let s = Store::open(dir.path()).unwrap();
        assert_eq!(s.phrase(&id).unwrap().title.chars().count(), 200);
    }
    #[test]
    fn bulk_delete_preserves_local_assets_and_supports_phrase_restore() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let old = store.save_phrase(sample(), None).unwrap();
        store.delete_item(&old, "phrase", true).unwrap();
        let old_record = store.recycle_records().unwrap()[0].id.clone();
        let image = dir.path().join("images/保留.png");
        image::RgbaImage::new(2, 2).save(&image).unwrap();
        let source = dir.path().join("源文件.txt");
        std::fs::write(&source, "保留源文件").unwrap();
        store
            .add_files("local-files", vec![source.to_string_lossy().into()])
            .unwrap();
        let group = store
            .save_category(Category {
                id: String::new(),
                name: "分组".into(),
                kind: "phrase".into(),
                parent_id: Some("common".into()),
                position: 0,
                background: "#84c394".into(),
            })
            .unwrap();
        let mut p = sample();
        p.segments[0].blocks.push(Block::Image {
            path: image.to_string_lossy().into(),
            name: "保留.png".into(),
        });
        p.segments[0].blocks.push(Block::File {
            path: source.to_string_lossy().into(),
            name: "源文件.txt".into(),
        });
        let first = store.save_phrase(p.clone(), None).unwrap();
        p.group_id = Some(group);
        let second = store.save_phrase(p, None).unwrap();
        let common = store
            .save_common_phrase(CommonPhrase {
                id: String::new(),
                text: "常用短语".into(),
                position: 0,
            })
            .unwrap();
        let before = store.library().unwrap();
        assert_eq!(
            store
                .delete_local_content("phrase", &[first.clone(), second.clone()], true)
                .unwrap(),
            2
        );
        let after = store.library().unwrap();
        assert!(after.phrases.is_empty());
        assert_eq!(
            serde_json::to_value(&after.categories).unwrap(),
            serde_json::to_value(&before.categories).unwrap()
        );
        assert_eq!(
            serde_json::to_value(&after.files).unwrap(),
            serde_json::to_value(&before.files).unwrap()
        );
        assert_eq!(
            serde_json::to_value(&after.common_phrases).unwrap(),
            serde_json::to_value(&before.common_phrases).unwrap()
        );
        assert_eq!(
            serde_json::to_value(&after.settings).unwrap(),
            serde_json::to_value(&before.settings).unwrap()
        );
        let records = store.recycle_records().unwrap();
        assert_eq!(records.len(), 3);
        let new_records: Vec<_> = records
            .iter()
            .filter(|r| r.id != old_record)
            .map(|r| r.id.clone())
            .collect();
        assert_eq!(store.restore_recycled(&new_records, false).unwrap(), 2);
        assert_eq!(
            serde_json::to_value(store.phrase(&first).unwrap()).unwrap(),
            serde_json::to_value(&before.phrases[0]).unwrap()
        );
        assert_eq!(
            store
                .delete_local_content("common", &[common], false)
                .unwrap(),
            1
        );
        assert_eq!(store.library().unwrap().phrases.len(), 2);
        assert_eq!(
            store
                .delete_local_content("phrase", &[first, second], false)
                .unwrap(),
            2
        );
        assert_eq!(store.recycle_records().unwrap().len(), 1);
        assert_eq!(store.recycle_records().unwrap()[0].id, old_record);
        assert!(image.is_file());
        assert_eq!(std::fs::read_to_string(source).unwrap(), "保留源文件");
        drop(store);
        let store = Store::open(dir.path()).unwrap();
        assert!(store.library().unwrap().phrases.is_empty());
        assert!(store.common_phrases().unwrap().is_empty());
        assert_eq!(store.library().unwrap().files.len(), 1);
    }
    #[test]
    fn bulk_delete_rejects_stale_duplicate_missing_or_wrong_scope_ids() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let first = store.save_phrase(sample(), None).unwrap();
        let second = store.save_phrase(sample(), None).unwrap();
        for ids in [
            vec![first.clone()],
            vec![first.clone(), first.clone()],
            vec![first.clone(), "不存在".into()],
        ] {
            assert!(store
                .delete_local_content("phrase", &ids, true)
                .unwrap_err()
                .contains("列表已变化"));
            assert_eq!(store.library().unwrap().phrases.len(), 2);
            assert!(store.recycle_records().unwrap().is_empty());
        }
        assert!(store
            .delete_local_content("file", &[first.clone(), second.clone()], false)
            .is_err());
        assert_eq!(store.library().unwrap().phrases.len(), 2);
        assert_eq!(
            store
                .delete_local_content("phrase", &[second, first], true)
                .unwrap(),
            2
        );
    }
    #[test]
    fn bulk_phrase_restore_preserves_order_for_reversed_selection() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        for title in ["第一", "第二", "第三"] {
            let mut p = sample();
            p.title = title.into();
            store.save_phrase(p, None).unwrap();
        }
        let before = store.library().unwrap();
        let ids: Vec<_> = before.phrases.iter().map(|p| p.id.clone()).collect();
        store.delete_local_content("phrase", &ids, true).unwrap();
        let mut records = store.recycle_records().unwrap();
        records.sort_by_key(|r| std::cmp::Reverse(r.phrases[0].position));
        store
            .restore_recycled(
                &records.iter().map(|r| r.id.clone()).collect::<Vec<_>>(),
                false,
            )
            .unwrap();
        assert_eq!(
            serde_json::to_value(&store.library().unwrap().phrases).unwrap(),
            serde_json::to_value(&before.phrases).unwrap()
        );
        assert!(store.recycle_records().unwrap().is_empty());
    }
    #[test]
    fn bulk_delete_failure_rolls_back_all_content_and_recycle_records() {
        for kind in ["phrase", "common"] {
            let dir = tempfile::tempdir().unwrap();
            let mut store = crate::store::test_store(dir.path()).unwrap();
            let (ids, table) = if kind == "phrase" {
                (
                    vec![
                        store.save_phrase(sample(), None).unwrap(),
                        store.save_phrase(sample(), None).unwrap(),
                    ],
                    "phrases",
                )
            } else {
                (
                    vec![
                        store
                            .save_common_phrase(CommonPhrase {
                                id: String::new(),
                                text: "一".into(),
                                position: 0,
                            })
                            .unwrap(),
                        store
                            .save_common_phrase(CommonPhrase {
                                id: String::new(),
                                text: "二".into(),
                                position: 0,
                            })
                            .unwrap(),
                    ],
                    "common_phrases",
                )
            };
            store.conn.execute_batch(&format!("CREATE TRIGGER simulate_delete_failure BEFORE DELETE ON {table} WHEN OLD.id='{}' BEGIN SELECT RAISE(ABORT,'模拟删除失败'); END;", ids[1])).unwrap();
            assert!(store
                .delete_local_content(kind, &ids, true)
                .unwrap_err()
                .contains("模拟删除失败"));
            let library = store.library().unwrap();
            assert_eq!(
                if kind == "phrase" {
                    library.phrases.len()
                } else {
                    library.common_phrases.len()
                },
                2
            );
            assert!(store.recycle_records().unwrap().is_empty());
            if kind == "phrase" {
                assert_eq!(library.phrases[0].text(), "您好");
            }
        }
    }
    #[test]
    fn common_phrases_preserve_data_and_reorder_atomically() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        store.save_phrase(sample(), None).unwrap();
        let make = |text: &str| CommonPhrase {
            id: String::new(),
            text: text.into(),
            position: 0,
        };
        let first = store.save_common_phrase(make("您好")).unwrap();
        let second = store.save_common_phrase(make(&"😀".repeat(16))).unwrap();
        for invalid in [" ".into(), "字".repeat(17), "第一行\n第二行".into()] {
            assert!(store.save_common_phrase(make(&invalid)).is_err());
        }
        store
            .reorder_common(&[second.clone(), first.clone()])
            .unwrap();
        assert!(store
            .reorder_common(&[first.clone(), first.clone()])
            .is_err());
        assert!(store.reorder_common(std::slice::from_ref(&first)).is_err());
        let current = store.common_phrases().unwrap();
        assert_eq!(current[0].id, second);
        store
            .save_common_phrase(CommonPhrase {
                id: first.clone(),
                text: "谢谢".into(),
                position: 0,
            })
            .unwrap();
        assert_eq!(store.common_phrases().unwrap()[1].text, "谢谢");
        let before = store.library().unwrap();
        let mut invalid = before.clone();
        invalid.common_phrases.push(make(&"字".repeat(17)));
        assert!(store.import(&invalid, false, false, &[]).is_err());
        assert_eq!(
            store.library().unwrap().categories.len(),
            before.categories.len()
        );
        assert_eq!(store.library().unwrap().phrases.len(), before.phrases.len());
        assert_eq!(store.common_phrases().unwrap().len(), 2);
        store.delete_item(&second, "common", false).unwrap();
        drop(store);
        let reopened = Store::open(dir.path()).unwrap();
        assert_eq!(reopened.common_phrases().unwrap()[0].text, "谢谢");
        assert_eq!(reopened.library().unwrap().phrases.len(), 1);
    }
    #[test]
    fn old_library_and_settings_receive_new_panel_defaults() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        assert_eq!(store.settings().unwrap().font_size, 14);
        let mut legacy = serde_json::to_value(store.library().unwrap()).unwrap();
        // 新库采用14号字体，旧备份中已保存的12号字体仍然保留。
        legacy["settings"]["fontSize"] = serde_json::json!(12);
        legacy.as_object_mut().unwrap().remove("commonPhrases");
        for key in [
            "showSearch",
            "showCommon",
            "dockCommon",
            "showTitle",
            "categoryHeight",
            "phraseDoubleClick",
            "commonSingleClick",
            "dockSearch",
            "freeResize",
            "rememberDock",
            "theme",
            "titleColor",
            "phraseCategoryColor",
            "fileCategoryColor",
            "filesFirst",
            "fileCategoryHeight",
            "commonHeight",
            "dockCommonRows",
            "sequenceHotkeys",
            "sequenceDirectSend",
            "sequenceBindings",
        ] {
            legacy["settings"].as_object_mut().unwrap().remove(key);
        }
        for category in legacy["categories"].as_array_mut().unwrap() {
            category.as_object_mut().unwrap().remove("background");
        }
        let library: Library = serde_json::from_value(legacy).unwrap();
        assert_eq!(library.settings.font_size, 12);
        assert!(library.common_phrases.is_empty());
        assert!(library.settings.show_title);
        assert_eq!(library.settings.category_height, 31);
        assert_eq!(library.settings.phrase_double_click, "insert");
        assert_eq!(library.settings.common_single_click, "insert");
        assert!(!library.settings.dock_search);
        assert!(library.settings.free_resize && library.settings.remember_dock);
        assert_eq!(library.settings.theme, "default");
        assert_eq!(library.settings.common_height, 144);
        assert_eq!(library.settings.dock_common_rows, 0);
        assert!(!library.settings.sequence_hotkeys && !library.settings.sequence_direct_send);
        assert_eq!(
            library.settings.sequence_bindings,
            std::array::from_fn::<_, 10, _>(|i| format!("Ctrl+{}", (i + 1) % 10))
        );
        assert!(library.categories.iter().all(|c| c.background.is_empty()));
        assert!(
            library.settings.show_search
                && library.settings.show_common
                && library.settings.dock_common
        );
    }
    #[test]
    fn settings_persist_reject_invalid_values_and_only_reset_unremembered_dock_on_reopen() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        let settings = Settings {
            phrase_double_click: "send".into(),
            common_single_click: "send".into(),
            theme: "green".into(),
            title_color: "#0000ff".into(),
            dock_search: true,
            file_category_height: 87,
            common_height: 100,
            dock_common_rows: 2,
            files_first: true,
            free_resize: false,
            remember_dock: false,
            ..Settings::default()
        };
        store.save_settings(&settings).unwrap();
        assert!(store.settings().unwrap().dock); // 当前会话不关闭吸附。
        for patch in [
            serde_json::json!({"commonSingleClick":"invalid"}),
            serde_json::json!({"phraseDoubleClick":"select"}),
            serde_json::json!({"theme":"invalid"}),
            serde_json::json!({"titleColor":"red"}),
            serde_json::json!({"dockCommonRows":4}),
            serde_json::json!({"commonHeight":0}),
            serde_json::json!({"fileCategoryHeight":201}),
        ] {
            let mut value = serde_json::to_value(&settings).unwrap();
            for (key, invalid) in patch.as_object().unwrap() {
                value[key] = invalid.clone();
            }
            let invalid: Settings = serde_json::from_value(value).unwrap();
            assert!(store.save_settings(&invalid).is_err());
            assert_eq!(store.settings().unwrap().theme, "green");
            assert_eq!(store.settings().unwrap().common_single_click, "send");
        }
        drop(store);
        let reopened = Store::open(dir.path()).unwrap();
        let saved = reopened.settings().unwrap();
        assert!(!saved.dock);
        assert!(saved.dock_search);
        assert_eq!(saved.phrase_double_click, "send");
        assert_eq!(saved.dock_common_rows, 2);
        assert_eq!(saved.title_color, "#0000ff");
        assert!(saved.files_first);
        assert!(!saved.free_resize);
        reopened
            .save_settings(&Settings {
                remember_dock: true,
                dock: true,
                ..saved
            })
            .unwrap();
        drop(reopened);
        assert!(Store::open(dir.path()).unwrap().settings().unwrap().dock);
    }
    #[test]
    fn invalid_batch_rolls_back_and_copy_does_not_duplicate_shortcut() {
        let dir = tempfile::tempdir().unwrap();
        let mut s = crate::store::test_store(dir.path()).unwrap();
        let mut p = sample();
        p.shortcut = "Ctrl+Alt+1".into();
        let id = s.save_phrase(p, None).unwrap();
        s.move_copy(&id, "common".into(), None, true).unwrap();
        let before = s.library().unwrap();
        let mut incoming = before.clone();
        incoming.phrases[1].segments[0].blocks = vec![Block::Text {
            text: "文".repeat(5001),
        }];
        assert!(s.import(&incoming, true, false, &[]).is_err());
        assert_eq!(s.library().unwrap().phrases.len(), 2);
        assert_eq!(
            s.library()
                .unwrap()
                .phrases
                .iter()
                .filter(|p| !p.shortcut.is_empty())
                .count(),
            1
        );
    }
    #[test]
    fn deleting_association_preserves_original_and_missing_record() {
        let dir = tempfile::tempdir().unwrap();
        let mut s = crate::store::test_store(dir.path()).unwrap();
        let source = dir.path().join("源文件.txt");
        std::fs::write(&source, "保留").unwrap();
        s.add_files("local-files", vec![source.to_string_lossy().into()])
            .unwrap();
        let id = s.library().unwrap().files[0].id.clone();
        s.delete_item(&id, "file", false).unwrap();
        assert!(source.exists());
        s.add_files("local-files", vec![source.to_string_lossy().into()])
            .unwrap();
        std::fs::remove_file(source).unwrap();
        assert!(s.library().unwrap().files[0].missing);
    }
    #[test]
    fn search_includes_attachment_names_and_rejects_wrong_group() {
        let dir = tempfile::tempdir().unwrap();
        let mut s = crate::store::test_store(dir.path()).unwrap();
        let mut p = sample();
        p.segments[0].blocks.push(Block::File {
            path: dir.path().join("abc.pdf").to_string_lossy().into(),
            name: "技术手册.pdf".into(),
        });
        s.save_phrase(p.clone(), None).unwrap();
        for query in ["技术手册", "JISHUSHOUCE", "jssc", "jishu PDF"] {
            assert_eq!(s.search(query).unwrap().len(), 1, "{query}");
        }
        let file = dir.path().join("UPS技术2026.pdf");
        std::fs::write(&file, "测试").unwrap();
        s.add_files("local-files", vec![file.to_string_lossy().into()])
            .unwrap();
        for query in ["UPS技术2026", "UPSjishu2026", "UPSjs2026"] {
            assert_eq!(
                s.search(query)
                    .unwrap()
                    .iter()
                    .filter(|h| h.kind == "file")
                    .count(),
                1
            );
        }
        let mut settings = s.settings().unwrap();
        settings.search_mode = "exact".into();
        s.save_settings(&settings).unwrap();
        assert!(s.search("jishu PDF").unwrap().is_empty());
        assert_eq!(s.search("JISHUSHOUCE.pdf").unwrap().len(), 1);
        assert_eq!(s.search("UPSjs2026").unwrap().len(), 1);
        p.group_id = Some("local-files".into());
        assert!(s.save_phrase(p, None).is_err());
    }
    #[test]
    fn development_shortcut_index_normalizes_and_shared_bindings_save_import_and_restore() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let mut phrase = sample();
        phrase.shortcut = "Ctrl+1".into();
        let first = store.save_phrase(phrase.clone(), None).unwrap();
        let before = serde_json::to_value(store.library().unwrap()).unwrap();
        // 模拟早期开发库及此前错误累加的版本标记；整理结构时必须保留全部现有内容。
        store.conn.execute_batch("DROP INDEX phrase_shortcuts; CREATE UNIQUE INDEX phrase_shortcuts ON phrases(shortcut COLLATE NOCASE) WHERE shortcut<>''; PRAGMA user_version=5;").unwrap();
        drop(store);
        let mut store = Store::open(dir.path()).unwrap();
        assert_eq!(
            serde_json::to_value(store.library().unwrap()).unwrap(),
            before
        );
        assert_eq!(
            store
                .conn
                .pragma_query_value(None, "user_version", |r| r.get::<_, u32>(0))
                .unwrap(),
            1
        );
        let unique: bool = store
            .conn
            .query_row(
                "SELECT \"unique\" FROM pragma_index_list('phrases') WHERE name='phrase_shortcuts'",
                [],
                |r| r.get(0),
            )
            .unwrap();
        assert!(!unique);
        let second = store.save_phrase(phrase, None).unwrap();
        let library = store.library().unwrap();
        store.import(&library, false, false, &[]).unwrap();
        assert_eq!(
            store
                .library()
                .unwrap()
                .phrases
                .iter()
                .filter(|p| p.shortcut == "Ctrl+1")
                .count(),
            4
        );
        store.delete_item(&first, "phrase", true).unwrap();
        let record = store.recycle_records().unwrap()[0].id.clone();
        store.restore_recycled(&[record], false).unwrap();
        assert_eq!(
            store.phrase(&second).unwrap().shortcut,
            store.phrase(&first).unwrap().shortcut
        );
        drop(store);
        assert_eq!(
            Store::open(dir.path())
                .unwrap()
                .library()
                .unwrap()
                .phrases
                .len(),
            4
        );
    }
    #[test]
    fn sequence_settings_validate_conflicts_without_changing_saved_configuration() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let mut phrase = sample();
        phrase.shortcut = "Ctrl+1".into();
        store.save_phrase(phrase, None).unwrap();
        let old = store.settings().unwrap();
        let mut next = old.clone();
        next.sequence_hotkeys = true;
        assert!(store.save_settings(&next).unwrap_err().contains("序号"));
        assert_eq!(
            serde_json::to_value(store.settings().unwrap()).unwrap(),
            serde_json::to_value(&old).unwrap()
        );
        next.sequence_bindings[0] = "Ctrl+F1".into();
        next.sequence_bindings[1] = String::new();
        store.save_settings(&next).unwrap();
        for binding in ["Ctrl+F1", "Alt+Q", "Q+W", "Ctrl+1"] {
            let mut invalid = next.clone();
            invalid.sequence_bindings[2] = binding.into();
            assert!(store.save_settings(&invalid).is_err(), "{binding}");
        }
        let mut conflicting = sample();
        conflicting.shortcut = "Ctrl+F1+C".into();
        assert!(store.save_phrase(conflicting, None).is_err());
        drop(store);
        let saved = Store::open(dir.path()).unwrap().settings().unwrap();
        assert!(saved.sequence_hotkeys);
        assert!(!saved.sequence_direct_send);
        assert_eq!(saved.sequence_bindings[0], "Ctrl+F1");
        assert_eq!(saved.sequence_bindings[1], "");
    }
    #[test]
    fn equivalent_shortcuts_share_candidates_and_stale_editor_preserves_sort_order() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let mut first = sample();
        first.shortcut = "Alt+Control+1".into();
        let a = store.save_phrase(first.clone(), None).unwrap();
        first.shortcut = "Ctrl+Alt+1".into();
        let shared = store.save_phrase(first, None).unwrap();
        assert_eq!(store.phrase(&shared).unwrap().shortcut, "Ctrl+Alt+1");
        let b = store.save_phrase(sample(), None).unwrap();
        let stale = store.phrase(&b).unwrap();
        store.sort(&b, "phrase", 1).unwrap();
        store.save_phrase(stale, None).unwrap();
        assert_eq!(store.library().unwrap().phrases[0].id, b);
        let category = store
            .save_category(Category {
                id: String::new(),
                name: "另一分类".into(),
                kind: "phrase".into(),
                parent_id: None,
                position: 0,
                background: String::new(),
            })
            .unwrap();
        let mut target = sample();
        target.root_id = category.clone();
        let last = store.save_phrase(target, None).unwrap();
        store.move_copy(&a, category.clone(), None, false).unwrap();
        let ids = store
            .library()
            .unwrap()
            .phrases
            .into_iter()
            .filter(|p| p.root_id == category)
            .map(|p| p.id)
            .collect::<Vec<_>>();
        assert_eq!(ids, vec![last, a]);
    }
    #[test]
    fn sequence_prefix_conflicts_are_transactional_in_save_import_and_restore() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let mut p = sample();
        p.shortcut = "Ctrl+Y".into();
        let a = store.save_phrase(p.clone(), None).unwrap();
        p.shortcut = "Ctrl+Y+C".into();
        assert!(store
            .save_phrase(p.clone(), None)
            .unwrap_err()
            .contains("冲突"));
        assert_eq!(store.library().unwrap().phrases.len(), 1);
        p.id = a.clone();
        store.save_phrase(p.clone(), None).unwrap();
        p.id.clear();
        p.shortcut = "Ctrl+Y+D".into();
        let b = store.save_phrase(p.clone(), None).unwrap();
        p.id.clear();
        p.shortcut = "1+1".into();
        store.save_phrase(p, None).unwrap();
        let mut imported = store.library().unwrap();
        imported.phrases = vec![store.phrase(&a).unwrap()];
        imported.phrases[0].title = "导入冲突内容".into();
        imported.phrases[0].shortcut = "Ctrl+Y".into();
        store.import(&imported, true, false, &[]).unwrap();
        assert_eq!(
            store
                .library()
                .unwrap()
                .phrases
                .iter()
                .find(|p| p.title == "导入冲突内容")
                .unwrap()
                .shortcut,
            ""
        );
        store.delete_item(&a, "phrase", true).unwrap();
        store.delete_item(&b, "phrase", false).unwrap();
        let mut single = sample();
        single.shortcut = "Ctrl+Y".into();
        store.save_phrase(single, None).unwrap();
        let record = store.recycle_records().unwrap()[0].id.clone();
        assert!(store
            .restore_recycled(std::slice::from_ref(&record), false)
            .unwrap_err()
            .contains("已被占用"));
        assert!(store.phrase(&a).is_err());
        assert_eq!(store.recycle_records().unwrap().len(), 1);
        store.restore_recycled(&[record], true).unwrap();
        assert_eq!(store.phrase(&a).unwrap().shortcut, "");
        drop(store);
        let reopened = Store::open(dir.path()).unwrap();
        assert!(reopened
            .library()
            .unwrap()
            .phrases
            .iter()
            .any(|p| p.shortcut == "1+1"));
    }
    fn category(store: &Store, name: &str, parent: Option<&str>) -> String {
        store
            .save_category(Category {
                id: String::new(),
                name: name.into(),
                kind: "phrase".into(),
                parent_id: parent.map(str::to_owned),
                position: 0,
                background: String::new(),
            })
            .unwrap()
    }
    #[test]
    fn duplicate_category_names_keep_independent_ids_and_contents() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let first = category(&store, "重复", None);
        let second = category(&store, "重复", None);
        let a = category(&store, "重复", Some(&first));
        let b = category(&store, "待重命名", Some(&first));
        let mut renamed = store
            .categories()
            .unwrap()
            .into_iter()
            .find(|c| c.id == b)
            .unwrap();
        renamed.name = "重复".into();
        renamed.background = "#84c394".into();
        store.save_category(renamed).unwrap();
        assert_ne!(first, second);
        assert_ne!(a, b);
        let mut ids = Vec::new();
        for (root, group) in [(&first, Some(&a)), (&first, Some(&b)), (&second, None)] {
            let mut p = sample();
            p.root_id = root.clone();
            p.group_id = group.cloned();
            ids.push(store.save_phrase(p, None).unwrap());
        }
        store.delete_category(&a, false, None, false).unwrap();
        assert!(store.phrase(&ids[0]).is_err());
        assert_eq!(
            store.phrase(&ids[1]).unwrap().group_id.as_deref(),
            Some(b.as_str())
        );
        store.delete_category(&first, false, None, false).unwrap();
        assert!(store.phrase(&ids[1]).is_err());
        assert_eq!(store.phrase(&ids[2]).unwrap().root_id, second);
        let file = Category {
            id: String::new(),
            name: "默认".into(),
            kind: "file".into(),
            parent_id: None,
            position: 0,
            background: String::new(),
        };
        assert_ne!(store.save_category(file).unwrap(), "local-files");
        drop(store);
        let reopened = Store::open(dir.path()).unwrap();
        assert_eq!(reopened.phrase(&ids[2]).unwrap().root_id, second);
    }
    #[test]
    fn old_name_index_is_removed_without_changing_existing_data() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let group = category(&store, "重复", Some("common"));
        let mut root = store
            .categories()
            .unwrap()
            .into_iter()
            .find(|c| c.id == "common")
            .unwrap();
        root.background = "#356fa1".into();
        store.save_category(root).unwrap();
        let mut p = sample();
        p.group_id = Some(group);
        store.save_phrase(p, None).unwrap();
        store.conn.execute_batch("CREATE UNIQUE INDEX category_names ON categories(kind,coalesce(parent_id,''),name COLLATE NOCASE);").unwrap();
        let before = serde_json::to_value(store.library().unwrap()).unwrap();
        drop(store);
        let upgraded = Store::open(dir.path()).unwrap();
        assert_eq!(
            serde_json::to_value(upgraded.library().unwrap()).unwrap(),
            before
        );
        assert_eq!(
            upgraded
                .conn
                .pragma_query_value(None, "user_version", |r| r.get::<_, i64>(0))
                .unwrap(),
            1
        );
        category(&upgraded, "常用", None);
        category(&upgraded, "重复", Some("common"));
        let after = serde_json::to_value(upgraded.library().unwrap()).unwrap();
        drop(upgraded);
        let reopened = Store::open(dir.path()).unwrap();
        assert_eq!(
            serde_json::to_value(reopened.library().unwrap()).unwrap(),
            after
        );
        assert_eq!(reopened.conn.query_row("SELECT count(*) FROM sqlite_master WHERE type='index' AND name='category_names'", [], |r| r.get::<_, i64>(0)).unwrap(), 0);
        assert_eq!(
            reopened
                .conn
                .query_row("SELECT count(*) FROM pragma_foreign_key_check", [], |r| r
                    .get::<_, i64>(
                    0
                ))
                .unwrap(),
            0
        );
    }
    #[test]
    fn category_transfer_merges_into_first_duplicate_group_in_list_order() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let target = category(&store, "常用", None);
        let a = category(&store, "PRO", Some("common"));
        let b = category(&store, "pro", Some("common"));
        let old_first = category(&store, "PRO", Some(&target));
        let first = category(&store, "pro", Some(&target));
        store
            .reorder_categories(&[first.clone(), old_first.clone()], "phrase", Some(&target))
            .unwrap();
        let mut originals = Vec::new();
        for group in [&a, &b] {
            let mut p = sample();
            p.group_id = Some(group.clone());
            originals.push(store.save_phrase(p, None).unwrap());
        }
        let mut resident = sample();
        resident.root_id = target.clone();
        resident.group_id = Some(old_first.clone());
        let resident = store.save_phrase(resident, None).unwrap();
        assert_eq!(
            store.move_copy_category("common", &target, true).unwrap(),
            2
        );
        assert_eq!(
            store
                .library()
                .unwrap()
                .phrases
                .iter()
                .filter(|p| p.group_id.as_deref() == Some(&first))
                .count(),
            2
        );
        assert_eq!(
            store.phrase(&resident).unwrap().group_id.as_deref(),
            Some(old_first.as_str())
        );
        assert_eq!(
            store.move_copy_category("common", &target, false).unwrap(),
            2
        );
        for original in originals {
            assert_eq!(
                store.phrase(&original).unwrap().group_id.as_deref(),
                Some(first.as_str())
            );
        }
        assert_eq!(
            store
                .categories()
                .unwrap()
                .iter()
                .filter(|c| c.parent_id.as_deref() == Some(&target))
                .count(),
            2
        );
    }
    #[test]
    fn backup_without_merge_preserves_duplicate_names_and_distinct_groups() {
        let dir = tempfile::tempdir().unwrap();
        let mut source = crate::store::test_store(&dir.path().join("source")).unwrap();
        let first = category(&source, "重复", None);
        let second = category(&source, "重复", None);
        let a = category(&source, "重复", Some(&first));
        let b = category(&source, "重复", Some(&first));
        let c = category(&source, "重复", Some(&second));
        for (title, root, group) in [("甲", &first, &a), ("乙", &first, &b), ("丙", &second, &c)]
        {
            let mut p = sample();
            p.title = title.into();
            p.root_id = root.clone();
            p.group_id = Some(group.clone());
            source.save_phrase(p, None).unwrap();
        }
        let backup = dir.path().join("重名备份.zip");
        crate::backup::export(&source, &backup, "backup", None).unwrap();
        let pending = crate::backup::preview(&backup).unwrap();
        let mut restored = crate::store::test_store(&dir.path().join("restored")).unwrap();
        crate::backup::apply(&mut restored, &pending, false, false).unwrap();
        let library = restored.library().unwrap();
        assert_eq!(
            library
                .categories
                .iter()
                .filter(|c| c.name == "重复" && c.parent_id.is_none())
                .count(),
            2
        );
        assert_eq!(
            library
                .categories
                .iter()
                .filter(|c| c.name == "重复" && c.parent_id.is_some())
                .count(),
            3
        );
        assert_eq!(
            library
                .categories
                .iter()
                .filter(|c| c.name == "常用")
                .count(),
            2
        );
        let find = |title| library.phrases.iter().find(|p| p.title == title).unwrap();
        let (a, b, c) = (find("甲"), find("乙"), find("丙"));
        assert_eq!(a.root_id, b.root_id);
        assert_ne!(a.root_id, c.root_id);
        assert_ne!(a.group_id, b.group_id);
        assert_ne!(b.group_id, c.group_id);
        assert!(!source
            .library()
            .unwrap()
            .phrases
            .iter()
            .any(|p| p.id == a.id));
        drop(restored);
        let reopened = Store::open(&dir.path().join("restored")).unwrap();
        assert_eq!(
            serde_json::to_value(reopened.library().unwrap()).unwrap(),
            serde_json::to_value(library).unwrap()
        );
    }
    #[test]
    fn merged_import_uses_list_order_and_duplicate_ids_still_roll_back() {
        let dir = tempfile::tempdir().unwrap();
        let mut source = crate::store::test_store(&dir.path().join("source")).unwrap();
        let source_root = category(&source, "目标", None);
        let source_group = category(&source, "PRO", Some(&source_root));
        let mut p = sample();
        p.root_id = source_root;
        p.group_id = Some(source_group);
        source.save_phrase(p, None).unwrap();
        let mut target = crate::store::test_store(&dir.path().join("target")).unwrap();
        let old_first = category(&target, "目标", None);
        let first = category(&target, "目标", None);
        let old_group = category(&target, "pro", Some(&first));
        let first_group = category(&target, "PRO", Some(&first));
        target
            .reorder_categories(
                &[first.clone(), old_first.clone(), "common".into()],
                "phrase",
                None,
            )
            .unwrap();
        target
            .reorder_categories(&[first_group.clone(), old_group], "phrase", Some(&first))
            .unwrap();
        let data = source.library().unwrap();
        target.import(&data, true, false, &[]).unwrap();
        let imported = &target.library().unwrap().phrases[0];
        assert_eq!(imported.root_id, first);
        assert_eq!(imported.group_id.as_deref(), Some(first_group.as_str()));
        let before = serde_json::to_value(target.library().unwrap()).unwrap();
        let mut invalid = data;
        invalid.categories.push(invalid.categories[0].clone());
        for merge in [false, true] {
            assert!(target
                .import(&invalid, merge, true, &[])
                .unwrap_err()
                .contains("重复分类编号"));
            assert_eq!(
                serde_json::to_value(target.library().unwrap()).unwrap(),
                before
            );
        }
    }
    #[test]
    fn category_copy_and_move_merge_groups_preserve_media_and_shortcuts() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let target = category(&store, "目标", None);
        let group = category(&store, "PRO", Some("common"));
        let merged = category(&store, "pro", Some(&target));
        let empty = category(&store, "空分组", Some("common"));
        let mut resident = sample();
        resident.root_id = target.clone();
        resident.group_id = Some(merged.clone());
        resident.title = "已有话术".into();
        let resident_id = store.save_phrase(resident, None).unwrap();
        let image = dir.path().join("images").join("截图.png");
        image::RgbaImage::new(2, 2).save(&image).unwrap();
        let attachment = dir.path().join("附件.txt");
        std::fs::write(&attachment, "源文件").unwrap();
        let mut source = sample();
        source.group_id = Some(group.clone());
        source.shortcut = "Ctrl+Alt+1".into();
        source.segments[0].blocks.extend([
            Block::Image {
                path: image.to_string_lossy().into(),
                name: "截图.png".into(),
            },
            Block::File {
                path: attachment.to_string_lossy().into(),
                name: "附件.txt".into(),
            },
        ]);
        source.segments.push(Segment {
            blocks: vec![Block::Text {
                text: "第二段".into(),
            }],
        });
        let original = store.save_phrase(source, None).unwrap();
        // 失效附件关联也必须随分类保留，分类操作不依赖源文件当前存在。
        std::fs::remove_file(&attachment).unwrap();
        assert_eq!(
            store.move_copy_category("common", &target, true).unwrap(),
            1
        );
        let copied = store
            .library()
            .unwrap()
            .phrases
            .into_iter()
            .find(|p| p.root_id == target && p.id != resident_id)
            .unwrap();
        assert_ne!(copied.id, original);
        assert_eq!(copied.group_id.as_deref(), Some(merged.as_str()));
        assert!(copied.shortcut.is_empty());
        assert_eq!(
            serde_json::to_value(&copied.segments).unwrap(),
            serde_json::to_value(&store.phrase(&original).unwrap().segments).unwrap()
        );
        assert!(store
            .categories()
            .unwrap()
            .iter()
            .any(|c| c.id == empty && c.parent_id.as_deref() == Some("common")));
        assert_eq!(
            store.move_copy_category("common", &target, false).unwrap(),
            1
        );
        assert!(store
            .categories()
            .unwrap()
            .iter()
            .all(|c| c.id != "common" && c.id != group));
        let moved = store.phrase(&original).unwrap();
        assert_eq!(moved.shortcut, "Ctrl+Alt+1");
        assert_eq!(moved.root_id, target);
        assert_eq!(moved.group_id.as_deref(), Some(merged.as_str()));
        assert!(moved.position > copied.position);
        assert_eq!(
            store
                .categories()
                .unwrap()
                .iter()
                .filter(|c| c.name.eq_ignore_ascii_case("pro"))
                .count(),
            1
        );
        assert!(image.exists());
        assert_eq!(
            store
                .conn
                .query_row("SELECT count(*) FROM pragma_foreign_key_check", [], |r| r
                    .get::<_, i64>(
                    0
                ))
                .unwrap(),
            0
        );
    }
    #[test]
    fn category_transfer_rolls_back_partial_group_and_phrase_updates() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let target = category(&store, "目标", None);
        let group = category(&store, "分组", Some("common"));
        for title in ["先处理", "故障"] {
            let mut p = sample();
            p.title = title.into();
            p.group_id = Some(group.clone());
            store.save_phrase(p, None).unwrap();
        }
        let before = serde_json::to_value(store.library().unwrap()).unwrap();
        store.conn.execute_batch("CREATE TRIGGER fail_move BEFORE UPDATE OF root_id ON phrases WHEN OLD.title='故障' BEGIN SELECT RAISE(ABORT,'模拟写入失败'); END; CREATE TRIGGER fail_copy BEFORE INSERT ON phrases WHEN NEW.title='故障' BEGIN SELECT RAISE(ABORT,'模拟写入失败'); END;").unwrap();
        assert!(store.move_copy_category("common", &target, false).is_err());
        assert_eq!(
            serde_json::to_value(store.library().unwrap()).unwrap(),
            before
        );
        assert!(store.move_copy_category("common", &target, true).is_err());
        assert_eq!(
            serde_json::to_value(store.library().unwrap()).unwrap(),
            before
        );
        assert!(store
            .delete_category("common", true, Some(&target), false)
            .is_err());
        assert_eq!(
            serde_json::to_value(store.library().unwrap()).unwrap(),
            before
        );
        assert!(store.move_copy_category(&group, "common", false).is_err());
        assert!(store
            .move_copy_category("common", "local-files", false)
            .is_err());
        assert_eq!(
            serde_json::to_value(store.library().unwrap()).unwrap(),
            before
        );
    }
    #[test]
    fn deleting_categories_can_retain_phrases_without_cascade_loss() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let target = category(&store, "目标", None);
        let group = category(&store, "分组", Some("common"));
        let root_phrase = store.save_phrase(sample(), None).unwrap();
        let mut p = sample();
        p.group_id = Some(group.clone());
        p.shortcut = "Ctrl+Alt+2".into();
        let grouped = store.save_phrase(p, None).unwrap();
        store.delete_category(&group, true, None, false).unwrap();
        assert_eq!(store.phrase(&grouped).unwrap().group_id, None);
        assert_eq!(
            store
                .library()
                .unwrap()
                .phrases
                .iter()
                .map(|p| p.id.as_str())
                .collect::<Vec<_>>(),
            vec![root_phrase.as_str(), grouped.as_str()]
        );
        let before = serde_json::to_value(store.library().unwrap()).unwrap();
        assert!(store
            .delete_category("common", true, Some("local-files"), false)
            .is_err());
        assert_eq!(
            serde_json::to_value(store.library().unwrap()).unwrap(),
            before
        );
        store
            .delete_category("common", true, Some(&target), false)
            .unwrap();
        assert_eq!(store.phrase(&grouped).unwrap().root_id, target);
        assert_eq!(store.phrase(&grouped).unwrap().shortcut, "Ctrl+Alt+2");
        assert_eq!(store.library().unwrap().phrases.len(), 2);
        store.delete_category(&target, false, None, false).unwrap();
        assert!(store.library().unwrap().phrases.is_empty());
    }
    #[test]
    fn category_sort_rejects_stale_or_cross_scope_lists_atomically() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let target = category(&store, "目标", None);
        let a = category(&store, "甲", Some("common"));
        let b = category(&store, "乙", Some("common"));
        store
            .reorder_categories(&[target.clone(), "common".into()], "phrase", None)
            .unwrap();
        store
            .reorder_categories(&[b.clone(), a.clone()], "phrase", Some("common"))
            .unwrap();
        let before = serde_json::to_value(store.library().unwrap()).unwrap();
        assert!(store
            .reorder_categories(&[a.clone(), target], "phrase", Some("common"))
            .is_err());
        assert!(store
            .reorder_categories(&[a.clone(), a], "phrase", Some("common"))
            .is_err());
        assert!(store
            .reorder_categories(&[b], "phrase", Some("common"))
            .is_err());
        assert_eq!(
            serde_json::to_value(store.library().unwrap()).unwrap(),
            before
        );
        drop(store);
        let reopened = Store::open(dir.path()).unwrap();
        assert_eq!(
            reopened
                .categories()
                .unwrap()
                .into_iter()
                .filter(|c| c.parent_id.as_deref() == Some("common"))
                .map(|c| c.name)
                .collect::<Vec<_>>(),
            vec!["乙", "甲"]
        );
    }
    #[test]
    fn moving_one_group_keeps_siblings_and_reparents_original_group() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let target = category(&store, "目标", None);
        let group = category(&store, "迁移分组", Some("common"));
        let sibling = category(&store, "保留分组", Some("common"));
        let mut p = sample();
        p.group_id = Some(group.clone());
        let id = store.save_phrase(p, None).unwrap();
        store.move_copy_category(&group, &target, false).unwrap();
        assert_eq!(
            store.phrase(&id).unwrap().group_id.as_deref(),
            Some(group.as_str())
        );
        let categories = store.categories().unwrap();
        assert_eq!(
            categories
                .iter()
                .find(|c| c.id == group)
                .unwrap()
                .parent_id
                .as_deref(),
            Some(target.as_str())
        );
        assert_eq!(
            categories
                .iter()
                .find(|c| c.id == sibling)
                .unwrap()
                .parent_id
                .as_deref(),
            Some("common")
        );
        assert!(categories.iter().any(|c| c.id == "common"));
    }
    #[test]
    fn development_categories_retain_existing_data_and_reopen() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(dir.path()).unwrap();
        let id = store.save_phrase(sample(), None).unwrap();
        let before = serde_json::to_value(store.phrase(&id).unwrap()).unwrap();
        store
            .conn
            .execute_batch("ALTER TABLE categories DROP COLUMN background; CREATE UNIQUE INDEX category_names ON categories(kind,coalesce(parent_id,''),name COLLATE NOCASE);")
            .unwrap();
        drop(store);
        let upgraded = Store::open(dir.path()).unwrap();
        assert_eq!(
            serde_json::to_value(upgraded.phrase(&id).unwrap()).unwrap(),
            before
        );
        assert!(upgraded
            .categories()
            .unwrap()
            .iter()
            .all(|c| c.background.is_empty()));
        let mut selected = upgraded
            .categories()
            .unwrap()
            .into_iter()
            .find(|c| c.id == "common")
            .unwrap();
        selected.background = "#356fa1".into();
        upgraded.save_category(selected.clone()).unwrap();
        selected.background = "invalid".into();
        assert!(upgraded.save_category(selected).is_err());
        drop(upgraded);
        let reopened = Store::open(dir.path()).unwrap();
        assert_eq!(
            reopened
                .categories()
                .unwrap()
                .iter()
                .find(|c| c.id == "common")
                .unwrap()
                .background,
            "#356fa1"
        );
        assert_eq!(
            serde_json::to_value(reopened.phrase(&id).unwrap()).unwrap(),
            before
        );
    }
}
