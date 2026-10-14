use crate::{model::*, store::Store};
use calamine::{open_workbook_auto, Reader};
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    fs,
    io::{Read, Write},
    path::{Path, PathBuf},
};
use zip::{write::SimpleFileOptions, ZipArchive, ZipWriter};

const MAX_IMPORT_BYTES: u64 = 128 * 1024 * 1024;
fn io_error(e: impl std::fmt::Display) -> String {
    format!("文件处理失败：{e}")
}

#[derive(Serialize, Deserialize)]
struct Backup {
    format: String,
    version: u32,
    library: Library,
    #[serde(default, rename = "recycleBin")]
    recycle_bin: Vec<RecycleRecord>,
}
#[derive(Clone, Serialize, Deserialize)]
pub struct PendingImport {
    pub preview: ImportPreview,
    pub library: Library,
    pub images: HashMap<String, Vec<u8>>,
    pub recycle_bin: Vec<RecycleRecord>,
    pub materials: HashMap<String, PathBuf>,
    #[serde(skip)]
    pub material_dir: Option<std::sync::Arc<tempfile::TempDir>>,
    #[serde(default)]
    pub settings_scope: SettingsScope,
}
/// 账号包只携带便携设置；分类和共享库包没有可恢复的个人设置。
#[derive(Clone, Copy, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum SettingsScope {
    #[default]
    Device,
    Portable,
    None,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportPreview {
    pub id: String,
    pub kind: String,
    pub filename: String,
    pub categories: usize,
    pub phrases: usize,
    pub common_phrases: usize,
    pub files: usize,
    pub images: usize,
    pub recycled: usize,
    pub rows: Vec<PreviewRow>,
    pub notes: Vec<String>,
}
#[derive(Clone, Serialize, Deserialize)]
pub struct PreviewRow {
    pub category: String,
    pub title: String,
    pub text: String,
}

/// 备份采用可移植 JSON 与实际素材的 ZIP。读取时不解压到任意路径，素材只写入受控目录。
/// 预览保存解析后的内容，确认时再事务导入，避免“预览一个文件、实际导入另一个文件”。
pub fn preview(path: &Path) -> Result<PendingImport> {
    if !path
        .extension()
        .is_some_and(|s| s.eq_ignore_ascii_case("xlsx"))
    {
        if let Some(backup) = preview_v2(path)? {
            return Ok(backup);
        }
        if let Some(private) = preview_private(path)? {
            return Ok(private);
        }
    }
    let size = fs::metadata(path).map_err(io_error)?.len();
    if size > MAX_IMPORT_BYTES {
        return Err("导入文件超过128MB，请拆分后导入。".into());
    }
    let kind = if path
        .extension()
        .is_some_and(|s| s.eq_ignore_ascii_case("xlsx"))
    {
        "excel"
    } else {
        "backup"
    };
    let (mut library, images, recycle_bin) = if kind == "excel" {
        (read_excel(path)?, HashMap::new(), vec![])
    } else {
        read_backup(path)?
    };
    library.data_dir.clear();
    let rows = library
        .phrases
        .iter()
        .take(20)
        .map(|p| PreviewRow {
            category: library
                .categories
                .iter()
                .find(|c| c.id == p.root_id)
                .map(|c| c.name.clone())
                .unwrap_or_default(),
            title: p.title.clone(),
            text: p.text().chars().take(120).collect(),
        })
        .collect();
    let mut notes = vec![
        "外部文件只导入路径关联，源文件需单独备份；失效记录会保留。".into(),
        "同键话术保留为候选；与导航、已启用序号或双键前缀冲突的快捷键会清空，请导入后重新设置。"
            .into(),
    ];
    if kind == "excel" {
        notes.push(
            "Excel仅导入文字，每行生成一条单段话术。图片、附件和分段使用完整备份保存。".into(),
        );
    }
    if !recycle_bin.is_empty() {
        notes.push(format!(
            "同时导入{}条回收站记录，记录中的内容保持在回收站中。",
            recycle_bin.len()
        ));
    }
    let preview = ImportPreview {
        id: id(),
        kind: kind.into(),
        filename: path
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .into(),
        categories: library.categories.len(),
        phrases: library.phrases.len(),
        common_phrases: library.common_phrases.len(),
        files: library.files.len(),
        images: images.len(),
        recycled: recycle_bin.len(),
        rows,
        notes,
    };
    Ok(PendingImport {
        preview,
        library,
        images,
        recycle_bin,
        materials: HashMap::new(),
        material_dir: None,
        settings_scope: SettingsScope::Device,
    })
}
type BackupContents = (Library, HashMap<String, Vec<u8>>, Vec<RecycleRecord>);

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct BackupV2 {
    format: String,
    version: u32,
    source: String,
    scope: String,
    settings_scope: SettingsScope,
    library: Library,
    recycle_bin: Vec<RecycleRecord>,
    assets: Vec<ArchiveAsset>,
}
#[derive(Serialize, Deserialize)]
struct ArchiveAsset {
    path: String,
    kind: String,
    size: u64,
}

/// 新包只读取清单内的素材，逐项落盘；旧 library.json 和 private.json 仍走原解析器。
fn preview_v2(path: &Path) -> Result<Option<PendingImport>> {
    let mut zip = match ZipArchive::new(fs::File::open(path).map_err(io_error)?) {
        Ok(z) => z,
        Err(_) => return Ok(None),
    };
    let (value, metadata_size): (serde_json::Value, u64) = {
        let entry = match zip.by_name("library.json") {
            Ok(entry) => entry,
            Err(_) => return Ok(None),
        };
        let size = entry.size();
        if size > 32 * 1024 * 1024 {
            return Err("备份元数据超过32MB。".into());
        }
        let value = serde_json::from_reader(entry.take(32 * 1024 * 1024))
            .map_err(|_| "备份内容无效。".to_owned())?;
        (value, size)
    };
    if value["version"] == 1 {
        return Ok(None);
    }
    if value["version"] != 2 || value["format"] != "shanyu-backup" {
        return Err("此备份版本不受支持，请升级闪语后导入。".into());
    }
    if metadata_size > 32 * 1024 * 1024 {
        return Err("备份元数据超过32MB。".into());
    }
    let mut package: BackupV2 =
        serde_json::from_value(value).map_err(|_| "备份内容无效。".to_owned())?;
    if !["private", "team"].contains(&package.source.as_str())
        || !["full", "category"].contains(&package.scope.as_str())
        || ((package.source == "team" || package.scope == "category")
            && (package.settings_scope != SettingsScope::None
                || !package.recycle_bin.is_empty()
                || !package.library.common_phrases.is_empty()))
    {
        return Err("备份的数据范围无效。".into());
    }
    let mut allowed = HashSet::from(["library.json".to_owned()]);
    for asset in &package.assets {
        if !asset
            .path
            .strip_prefix("assets/")
            .is_some_and(|v| uuid::Uuid::parse_str(v).is_ok())
            || !["image", "file"].contains(&asset.kind.as_str())
            || asset.size == 0
            || asset.size > MAX_IMPORT_BYTES
            || !allowed.insert(asset.path.clone())
        {
            return Err("备份素材清单无效或重复。".into());
        }
    }
    let mut names = HashSet::new();
    for index in 0..zip.len() {
        let entry = zip.by_index(index).map_err(io_error)?;
        let name = entry.name().map_err(io_error)?.into_owned();
        if entry.enclosed_name().is_none()
            || name.contains('\\')
            || entry.unix_mode().is_some_and(|m| m & 0o170000 == 0o120000)
            || !allowed.contains(&name)
            || !names.insert(name)
            || entry.size() > MAX_IMPORT_BYTES
        {
            return Err("备份包包含不安全、重复或超限的项目。".into());
        }
    }
    let dir = std::sync::Arc::new(tempfile::tempdir().map_err(io_error)?);
    let mut materials = HashMap::new();
    for block in package
        .library
        .phrases
        .iter()
        .chain(package.recycle_bin.iter().flat_map(|r| &r.phrases))
        .flat_map(|p| &p.segments)
        .flat_map(|s| &s.blocks)
    {
        let (path, kind) = match block {
            Block::Image { path, .. } => (path, "image"),
            Block::File { path, .. } => (path, "file"),
            _ => continue,
        };
        let asset = package
            .assets
            .iter()
            .find(|a| &a.path == path && a.kind == kind)
            .ok_or("备份缺少话术素材清单。")?;
        if materials.contains_key(path) {
            continue;
        }
        let entry = zip.by_name(path).map_err(|_| "备份素材缺失。".to_owned())?;
        if entry.size() != asset.size {
            return Err("备份素材大小不符。".into());
        }
        let target = dir.path().join(id());
        let mut file = fs::File::create(&target).map_err(io_error)?;
        let copied =
            std::io::copy(&mut entry.take(MAX_IMPORT_BYTES + 1), &mut file).map_err(io_error)?;
        if copied != asset.size {
            return Err("备份素材不完整。".into());
        }
        if kind == "image" {
            decode_image(&fs::read(&target).map_err(io_error)?)?;
        }
        materials.insert(path.clone(), target);
    }
    package.library.data_dir.clear();
    let mut pending = PendingImport {
        preview: ImportPreview {
            id: id(),
            kind: "backup".into(),
            filename: path
                .file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .into(),
            categories: 0,
            phrases: 0,
            common_phrases: 0,
            files: 0,
            images: 0,
            recycled: 0,
            rows: vec![],
            notes: vec![],
        },
        library: package.library,
        recycle_bin: package.recycle_bin,
        images: HashMap::new(),
        materials,
        material_dir: Some(dir),
        settings_scope: package.settings_scope,
    };
    recount(&mut pending);
    Ok(Some(pending))
}

pub fn recount(pending: &mut PendingImport) {
    let lib = &pending.library;
    pending.preview.categories = lib.categories.len();
    pending.preview.phrases = lib.phrases.len();
    pending.preview.common_phrases = lib.common_phrases.len();
    pending.preview.files = lib.files.len();
    pending.preview.recycled = pending.recycle_bin.len();
    pending.preview.images = lib
        .phrases
        .iter()
        .chain(pending.recycle_bin.iter().flat_map(|r| &r.phrases))
        .flat_map(|p| &p.segments)
        .flat_map(|s| &s.blocks)
        .filter_map(|b| {
            if let Block::Image { path, .. } = b {
                Some(path.as_str())
            } else {
                None
            }
        })
        .collect::<HashSet<_>>()
        .len();
    pending.preview.rows = lib
        .phrases
        .iter()
        .take(20)
        .map(|p| PreviewRow {
            category: lib
                .categories
                .iter()
                .find(|c| c.id == p.root_id)
                .map(|c| c.name.clone())
                .unwrap_or_default(),
            title: p.title.clone(),
            text: p.text().chars().take(120).collect(),
        })
        .collect();
}

/// 预览按提交时的层级映射说明歧义；同级多项同名不随意选择第一项。
pub fn note_merge_ambiguity(pending: &mut PendingImport, existing: &[Category], merge: bool) {
    const NOTE: &str = "个分类存在多个同级同名项，将独立导入。";
    pending.preview.notes.retain(|n| !n.ends_with(NOTE));
    if !merge {
        return;
    }
    let mut categories = existing.to_vec();
    let mut map = HashMap::new();
    let mut ambiguous = 0;
    for c in pending
        .library
        .categories
        .iter()
        .filter(|c| c.parent_id.is_none())
        .chain(
            pending
                .library
                .categories
                .iter()
                .filter(|c| c.parent_id.is_some()),
        )
    {
        let parent = c.parent_id.as_ref().and_then(|p| map.get(p).cloned());
        let matches: Vec<_> = categories
            .iter()
            .filter(|v| {
                v.kind == c.kind && v.parent_id == parent && v.name.eq_ignore_ascii_case(&c.name)
            })
            .collect();
        if matches.len() == 1 {
            map.insert(c.id.clone(), matches[0].id.clone());
        } else {
            if matches.len() > 1 {
                ambiguous += 1;
            }
            map.insert(c.id.clone(), c.id.clone());
            let mut inserted = c.clone();
            inserted.parent_id = parent;
            categories.push(inserted);
        }
    }
    if ambiguous > 0 {
        pending.preview.notes.push(format!("{ambiguous} {NOTE}"));
    }
}

/// 普通导入的副本编号只在预览时分配，批次持久化后重试不能再生成另一套编号。
pub fn prepare_ids(pending: &mut PendingImport) -> Result<()> {
    let mut categories = HashMap::new();
    let mut phrases = HashMap::new();
    let mut seen = HashSet::new();
    for c in &pending.library.categories {
        if !seen.insert(c.id.clone()) {
            return Err("导入文件包含重复分类编号。".into());
        }
    }
    for c in pending
        .library
        .categories
        .iter()
        .chain(pending.recycle_bin.iter().flat_map(|r| &r.categories))
    {
        categories.entry(c.id.clone()).or_insert_with(id);
    }
    seen.clear();
    for p in &pending.library.phrases {
        if !seen.insert(p.id.clone()) {
            return Err("导入文件包含重复话术编号。".into());
        }
    }
    for p in pending
        .library
        .phrases
        .iter()
        .chain(pending.recycle_bin.iter().flat_map(|r| &r.phrases))
    {
        phrases.entry(p.id.clone()).or_insert_with(id);
    }
    for c in pending.library.categories.iter_mut().chain(
        pending
            .recycle_bin
            .iter_mut()
            .flat_map(|r| &mut r.categories),
    ) {
        c.id = categories[&c.id].clone();
        c.parent_id = c
            .parent_id
            .as_ref()
            .map(|p| {
                categories
                    .get(p)
                    .cloned()
                    .ok_or("导入分类缺少上级。".to_owned())
            })
            .transpose()?;
    }
    for p in pending
        .library
        .phrases
        .iter_mut()
        .chain(pending.recycle_bin.iter_mut().flat_map(|r| &mut r.phrases))
    {
        p.id = phrases[&p.id].clone();
        p.root_id = categories
            .get(&p.root_id)
            .cloned()
            .ok_or("导入话术缺少一级分类。")?;
        p.group_id = p
            .group_id
            .as_ref()
            .map(|g| {
                categories
                    .get(g)
                    .cloned()
                    .ok_or("导入话术缺少二级分类。".to_owned())
            })
            .transpose()?;
    }
    for f in &mut pending.library.files {
        f.id = id();
        f.category_id = categories
            .get(&f.category_id)
            .cloned()
            .ok_or("导入文件关联缺少分类。")?;
    }
    for c in &mut pending.library.common_phrases {
        c.id = id();
    }
    for r in &mut pending.recycle_bin {
        r.id = id();
    }
    Ok(())
}

/// 将预览依赖的实际内容复制到批次目录；确认时不再读取原ZIP或可能被改写的外部附件。
pub fn freeze_materials(
    pending: &mut PendingImport,
    dir: &Path,
    check: &impl Fn() -> Result<()>,
) -> Result<()> {
    fs::create_dir_all(dir).map_err(io_error)?;
    let mut frozen = HashMap::new();
    for block in pending
        .library
        .phrases
        .iter_mut()
        .chain(pending.recycle_bin.iter_mut().flat_map(|r| &mut r.phrases))
        .flat_map(|p| &mut p.segments)
        .flat_map(|s| &mut s.blocks)
    {
        let (path, image) = match block {
            Block::Image { path, .. } => (path, true),
            Block::File { path, .. } => (path, false),
            _ => continue,
        };
        check()?;
        if frozen.contains_key(path) {
            continue;
        }
        let target = dir.join(id());
        if let Some(bytes) = pending.images.get(path) {
            fs::write(&target, bytes).map_err(io_error)?;
        } else {
            let source = pending
                .materials
                .get(path)
                .cloned()
                .unwrap_or_else(|| PathBuf::from(&*path));
            copy_material(&source, &target, check)?;
        }
        if image {
            decode_image(&fs::read(&target).map_err(io_error)?)?;
        }
        frozen.insert(path.clone(), target);
    }
    pending.materials = frozen;
    pending.images.clear();
    pending.material_dir = None;
    Ok(())
}

pub fn copy_material(source: &Path, target: &Path, check: &impl Fn() -> Result<()>) -> Result<()> {
    let mut input = fs::File::open(source).map_err(|_| "源文件缺失或素材未缓存。".to_owned())?;
    let before = input.metadata().map_err(io_error)?;
    if !before.is_file() || before.len() == 0 || before.len() > MAX_IMPORT_BYTES {
        return Err("单个素材需为1字节至128MB。".into());
    }
    let mut output = fs::File::create(target).map_err(io_error)?;
    let mut size = 0u64;
    let mut buf = [0u8; 64 * 1024];
    loop {
        check()?;
        let n = input.read(&mut buf).map_err(io_error)?;
        if n == 0 {
            break;
        }
        size += n as u64;
        if size > MAX_IMPORT_BYTES {
            return Err("单个素材超过128MB。".into());
        }
        output.write_all(&buf[..n]).map_err(io_error)?;
    }
    let after = input.metadata().map_err(io_error)?;
    if size != before.len()
        || after.len() != before.len()
        || after.modified().ok() != before.modified().ok()
    {
        return Err("素材在处理期间发生变化，请重试。".into());
    }
    Ok(())
}
/// 个人备份按素材逐个落临时文件，整个库不受旧ZIP总计128MB限制；每项仍限制128MB。
/// 只接收固定清单中的UUID路径，临时目录由PendingImport持有，取消预览会自动清理。
fn preview_private(path: &Path) -> Result<Option<PendingImport>> {
    use shanyu_team_model::private as wire;
    let mut zip = match ZipArchive::new(fs::File::open(path).map_err(io_error)?) {
        Ok(z) => z,
        Err(_) => return Ok(None),
    };
    let snapshot: wire::Snapshot = {
        let entry = match zip.by_name("private.json") {
            Ok(e) => e,
            Err(_) => return Ok(None),
        };
        if entry.size() > 32 * 1024 * 1024 {
            return Err("个人备份元数据超过32MB。".into());
        }
        serde_json::from_reader(entry.take(32 * 1024 * 1024))
            .map_err(|_| "个人备份内容无效。".to_owned())?
    };
    let mut names = HashSet::new();
    for i in 0..zip.len() {
        let entry = zip.by_index(i).map_err(io_error)?;
        let name = entry.name().map_err(io_error)?.into_owned();
        if entry.enclosed_name().is_none()
            || entry.unix_mode().is_some_and(|m| m & 0o170000 == 0o120000)
            || !names.insert(name.clone())
            || !(name == "private.json"
                || name
                    .strip_prefix("assets/")
                    .is_some_and(|id| uuid::Uuid::parse_str(id).is_ok()))
        {
            return Err("个人备份包含不安全或重复路径。".into());
        }
        if entry.size() > MAX_IMPORT_BYTES {
            return Err("个人备份单个素材超过128MB。".into());
        }
    }
    let material_dir = std::sync::Arc::new(tempfile::tempdir().map_err(io_error)?);
    let mut materials = HashMap::new();
    let mut library = Library {
        categories: vec![],
        phrases: vec![],
        files: vec![],
        common_phrases: vec![],
        settings: Settings::default(),
        data_dir: String::new(),
    };
    let mut recycled = vec![];
    let mut records = HashSet::new();
    let mut images = 0;
    for record in snapshot.records {
        if !records.insert((record.kind, record.id.clone())) {
            return Err("个人备份包含重复记录。".into());
        }
        let Some(value) = record.value else { continue };
        match record.kind {
            wire::Kind::Category => {
                let c: shanyu_team_model::Category =
                    serde_json::from_value(value).map_err(|_| "个人备份分类无效。".to_owned())?;
                library.categories.push(Category {
                    id: c.id,
                    name: c.name,
                    kind: "phrase".into(),
                    parent_id: c.parent_id,
                    position: c.position,
                    background: c.background,
                });
            }
            wire::Kind::Phrase => {
                let p: wire::Phrase =
                    serde_json::from_value(value).map_err(|_| "个人备份话术无效。".to_owned())?;
                library.phrases.push(private_phrase(
                    p,
                    &snapshot.assets,
                    &mut zip,
                    material_dir.path(),
                    &mut materials,
                    &mut images,
                )?);
            }
            wire::Kind::Common => library.common_phrases.push(
                serde_json::from_value(value).map_err(|_| "个人备份常用短语无效。".to_owned())?,
            ),
            wire::Kind::Recycle => {
                let r: wire::Recycle = serde_json::from_value(value)
                    .map_err(|_| "个人备份回收记录无效。".to_owned())?;
                let mut phrases = vec![];
                for p in r.phrases {
                    phrases.push(private_phrase(
                        p,
                        &snapshot.assets,
                        &mut zip,
                        material_dir.path(),
                        &mut materials,
                        &mut images,
                    )?);
                }
                recycled.push(RecycleRecord {
                    id: r.id,
                    kind: r.kind,
                    label: r.label,
                    location: r.location,
                    deleted_at: r.deleted_at,
                    categories: r
                        .categories
                        .into_iter()
                        .map(|c| Category {
                            id: c.id,
                            name: c.name,
                            kind: "phrase".into(),
                            parent_id: c.parent_id,
                            position: c.position,
                            background: c.background,
                        })
                        .collect(),
                    phrases,
                });
            }
            wire::Kind::Setting => {
                if !wire::validate_setting(&record.id, &value) {
                    return Err("个人备份设置无效。".into());
                }
                let mut settings = serde_json::to_value(&library.settings).map_err(io_error)?;
                settings[&record.id] = value;
                library.settings = serde_json::from_value(settings)
                    .map_err(|_| "个人备份设置无效。".to_owned())?;
            }
        }
    }
    library.settings.validate()?;
    let preview = ImportPreview {
        id: id(),
        kind: "backup".into(),
        filename: path
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .into_owned(),
        categories: library.categories.len(),
        phrases: library.phrases.len(),
        common_phrases: library.common_phrases.len(),
        files: 0,
        images,
        recycled: recycled.len(),
        rows: library
            .phrases
            .iter()
            .take(20)
            .map(|p| PreviewRow {
                category: library
                    .categories
                    .iter()
                    .find(|c| c.id == p.root_id)
                    .map(|c| c.name.clone())
                    .unwrap_or_default(),
                title: p.title.clone(),
                text: p.text().chars().take(120).collect(),
            })
            .collect(),
        notes: vec![
            "个人备份包含话术附件；确认后导入当前个人库，生成独立副本。".into(),
            "设备设置和独立文件库不受影响。".into(),
        ],
    };
    Ok(Some(PendingImport {
        preview,
        library,
        images: HashMap::new(),
        recycle_bin: recycled,
        materials,
        material_dir: Some(material_dir),
        settings_scope: SettingsScope::Portable,
    }))
}
fn private_phrase(
    p: shanyu_team_model::private::Phrase,
    assets: &[shanyu_team_model::private::Asset],
    zip: &mut ZipArchive<fs::File>,
    dir: &Path,
    materials: &mut HashMap<String, PathBuf>,
    images: &mut usize,
) -> Result<Phrase> {
    let mut segments = vec![];
    for segment in p.segments {
        let mut blocks = vec![];
        for b in segment.blocks {
            let expected_kind = match &b {
                shanyu_team_model::Block::Image { .. } => "image",
                shanyu_team_model::Block::File { .. } => "file",
                _ => "",
            };
            blocks.push(match b {
                shanyu_team_model::Block::Text { text } => Block::Text { text },
                shanyu_team_model::Block::Image { asset_id, name }
                | shanyu_team_model::Block::File { asset_id, name } => {
                    let asset = assets
                        .iter()
                        .find(|a| a.id == asset_id)
                        .ok_or("个人备份素材清单缺失。")?;
                    if uuid::Uuid::parse_str(&asset.id).is_err()
                        || asset.kind != expected_kind
                        || asset.size > MAX_IMPORT_BYTES
                    {
                        return Err("个人备份素材无效。".into());
                    }
                    let key = format!("private-asset:{}", asset.id);
                    if !materials.contains_key(&key) {
                        let entry = zip
                            .by_name(&format!("assets/{}", asset.id))
                            .map_err(|_| "个人备份缺少素材。".to_owned())?;
                        if entry.size() != asset.size {
                            return Err("个人备份素材大小不符。".into());
                        }
                        let target = dir.join(&asset.id);
                        let mut output = fs::File::create(&target).map_err(io_error)?;
                        let copied =
                            std::io::copy(&mut entry.take(MAX_IMPORT_BYTES + 1), &mut output)
                                .map_err(io_error)?;
                        if copied != asset.size {
                            return Err("个人备份素材不完整。".into());
                        }
                        drop(output);
                        if asset.kind == "image" {
                            decode_image(&fs::read(&target).map_err(io_error)?)?;
                            *images += 1;
                        }
                        materials.insert(key.clone(), target);
                    }
                    if asset.kind == "image" {
                        Block::Image { path: key, name }
                    } else {
                        Block::File { path: key, name }
                    }
                }
            });
        }
        segments.push(Segment { blocks });
    }
    Ok(Phrase {
        id: p.id,
        root_id: p.root_id,
        group_id: p.group_id,
        title: p.title,
        color: p.color,
        background: p.background,
        shortcut: p.shortcut,
        position: p.position,
        segments,
    })
}
fn read_backup(path: &Path) -> Result<BackupContents> {
    let mut zip = ZipArchive::new(fs::File::open(path).map_err(io_error)?)
        .map_err(|e| format!("无法读取完整备份包：{e}"))?;
    let mut total = 0u64;
    let mut names = HashSet::new();
    for i in 0..zip.len() {
        let entry = zip.by_index(i).map_err(io_error)?;
        // ZIP 9 的名称解码会失败；先显式检查名称，再沿用路径、符号链接与重复项边界。
        let name = entry
            .name()
            .map_err(|e| format!("备份文件名无法读取：{e}"))?
            .into_owned();
        if entry.enclosed_name().is_none()
            || name.contains('\\')
            || entry.unix_mode().is_some_and(|m| m & 0o170000 == 0o120000)
            || !names.insert(name)
        {
            return Err("备份包包含不安全或重复的路径。".into());
        }
        total = total.checked_add(entry.size()).ok_or("备份包大小无效。")?;
        if total > MAX_IMPORT_BYTES {
            return Err("备份包解压后超过128MB，请拆分后导入。".into());
        }
    }
    let backup: Backup = {
        let entry = zip
            .by_name("library.json")
            .map_err(|_| "备份包缺少library.json。")?;
        serde_json::from_reader(entry.take(MAX_IMPORT_BYTES))
            .map_err(|_| "备份内容无效。".to_owned())?
    };
    if backup.format != "shanyu-backup" || backup.version != 1 {
        return Err("此备份格式或版本不受支持。".into());
    }
    let mut images = HashMap::new();
    for block in backup
        .library
        .phrases
        .iter()
        .chain(backup.recycle_bin.iter().flat_map(|r| &r.phrases))
        .flat_map(|p| &p.segments)
        .flat_map(|s| &s.blocks)
    {
        if let Block::Image { path, .. } = block {
            let image_path = Path::new(path);
            if image_path.components().count() != 2 || !path.starts_with("images/") {
                return Err("备份中的图片路径无效。".into());
            }
            if !images.contains_key(path) {
                let mut bytes = Vec::new();
                zip.by_name(path)
                    .map_err(|_| format!("备份缺少图片：{path}"))?
                    .take(MAX_IMPORT_BYTES)
                    .read_to_end(&mut bytes)
                    .map_err(io_error)?;
                decode_image(&bytes)?;
                images.insert(path.clone(), bytes);
            }
        }
    }
    Ok((backup.library, images, backup.recycle_bin))
}
fn read_excel(path: &Path) -> Result<Library> {
    // xlsx 也是 ZIP：先限制展开大小，再交给表格解析器，避免小压缩文件耗尽内存。
    let mut archive = ZipArchive::new(fs::File::open(path).map_err(io_error)?).map_err(io_error)?;
    let mut expanded = 0u64;
    for i in 0..archive.len() {
        expanded = expanded
            .checked_add(archive.by_index(i).map_err(io_error)?.size())
            .ok_or("Excel文件大小无效。")?;
        if expanded > MAX_IMPORT_BYTES {
            return Err("Excel解压后超过128MB，请拆分后导入。".into());
        }
    }
    let mut book = open_workbook_auto(path).map_err(|e| format!("Excel文件无法打开：{e}"))?;
    let range = book
        .worksheet_range_at(0)
        .ok_or("Excel文件没有工作表。")?
        .map_err(io_error)?;
    let mut rows = range.rows();
    let headers = rows
        .next()
        .ok_or("Excel文件为空。")?
        .iter()
        .map(ToString::to_string)
        .collect::<Vec<_>>();
    let indices = ["一级分类", "二级分类", "标题", "正文"]
        .map(|name| headers.iter().position(|v| v.trim() == name));
    if indices.iter().any(Option::is_none) {
        return Err(
            "Excel表头必须包含：一级分类、二级分类、标题、正文。请使用闪语文字模板。".into(),
        );
    }
    let indices = indices.map(Option::unwrap);
    let mut categories: Vec<Category> = vec![];
    let mut phrases = vec![];
    for (row_no, row) in rows.enumerate() {
        let values = indices.map(|i| row.get(i).map(ToString::to_string).unwrap_or_default());
        if values.iter().all(|v| v.trim().is_empty()) {
            continue;
        }
        if values[0].trim().is_empty() {
            return Err(format!("Excel第{}行未填写一级分类。", row_no + 2));
        }
        let root = find_excel_category(&mut categories, values[0].trim(), None);
        let group = if values[1].trim().is_empty() {
            None
        } else {
            Some(find_excel_category(
                &mut categories,
                values[1].trim(),
                Some(root.clone()),
            ))
        };
        let p = Phrase {
            id: id(),
            root_id: root,
            group_id: group,
            title: values[2].trim().into(),
            color: "#ff5100".into(),
            background: String::new(),
            shortcut: String::new(),
            position: phrases.len() as i64,
            segments: vec![Segment {
                blocks: vec![Block::Text {
                    text: values[3].clone(),
                }],
            }],
        };
        validate_phrase(&p).map_err(|e| format!("Excel第{}行：{e}", row_no + 2))?;
        phrases.push(p);
    }
    if phrases.is_empty() {
        return Err("Excel文件中没有可导入的话术。".into());
    }
    Ok(Library {
        categories,
        phrases,
        files: vec![],
        common_phrases: vec![],
        settings: Settings::default(),
        data_dir: String::new(),
    })
}
fn find_excel_category(
    categories: &mut Vec<Category>,
    name: &str,
    parent: Option<String>,
) -> String {
    if let Some(c) = categories
        .iter()
        .find(|c| c.name == name && c.parent_id == parent)
    {
        return c.id.clone();
    }
    let id = id();
    categories.push(Category {
        id: id.clone(),
        name: name.into(),
        kind: "phrase".into(),
        parent_id: parent,
        position: categories.len() as i64,
        background: String::new(),
    });
    id
}

#[cfg(test)]
pub fn apply(
    store: &mut Store,
    pending: &PendingImport,
    merge: bool,
    restore_settings: bool,
) -> Result<()> {
    apply_inner(store, pending, merge, restore_settings, None)
}
pub fn apply_prepared(
    store: &mut Store,
    pending: &PendingImport,
    merge: bool,
    restore_settings: bool,
) -> Result<()> {
    if store.imported(&pending.preview.id)? {
        return Ok(());
    }
    apply_inner(
        store,
        pending,
        merge,
        restore_settings,
        Some(&pending.preview.id),
    )
}
fn apply_inner(
    store: &mut Store,
    pending: &PendingImport,
    merge: bool,
    restore_settings: bool,
    batch: Option<&str>,
) -> Result<()> {
    let mut library = pending.library.clone();
    let mut recycled = pending.recycle_bin.clone();
    let mut created = vec![];
    let mut remapped = HashMap::new();
    let mut material_batch = crate::private_local::MaterialBatch::default();
    let work = (|| -> Result<()> {
        for (original, bytes) in &pending.images {
            let path = store.dir.join("images").join(format!("{}.png", id()));
            decode_image(bytes)?
                .save_with_format(&path, image::ImageFormat::Png)
                .map_err(io_error)?;
            created.push(path.clone());
            remapped.insert(original.clone(), path.to_string_lossy().into_owned());
        }
        for b in library
            .phrases
            .iter_mut()
            .chain(recycled.iter_mut().flat_map(|r| &mut r.phrases))
            .flat_map(|p| &mut p.segments)
            .flat_map(|s| &mut s.blocks)
        {
            let image = matches!(b, Block::Image { .. });
            if let Block::Image { path, name } | Block::File { path, name } = b {
                if let Some(source) = pending.materials.get(path) {
                    let folder = if image {
                        store.dir.join("images")
                    } else {
                        store.dir.join("assets").join(id())
                    };
                    fs::create_dir_all(&folder).map_err(io_error)?;
                    let safe_name: String = name
                        .chars()
                        .map(|c| {
                            if c.is_control() || "\\/:*?\"<>|".contains(c) {
                                '_'
                            } else {
                                c
                            }
                        })
                        .collect();
                    let target = if image {
                        folder.join(format!("{}.png", id()))
                    } else {
                        folder.join(if safe_name.trim_end_matches(['.', ' ']).is_empty() {
                            "素材"
                        } else {
                            safe_name.trim_end_matches(['.', ' '])
                        })
                    };
                    created.push(target.clone());
                    if image {
                        decode_image(&fs::read(source).map_err(io_error)?)?
                            .save(&target)
                            .map_err(io_error)?;
                    } else {
                        fs::copy(source, &target).map_err(io_error)?;
                    }
                    *path = external_path(&target);
                    continue;
                }
            }
            if let Block::Image { path, .. } = b {
                *path = remapped.get(path).cloned().ok_or("导入图片缺少素材。")?;
            }
        }
        for p in library
            .phrases
            .iter_mut()
            .chain(recycled.iter_mut().flat_map(|r| &mut r.phrases))
        {
            material_batch.append(crate::private_local::safe_materials(store, p)?);
        }
        if let Some(batch) = batch {
            store.import_prepared(&library, merge, restore_settings, &recycled, batch)
        } else {
            store.import(&library, merge, restore_settings, &recycled)
        }
    })();
    if work.is_err() {
        for path in created {
            if let Err(e) = fs::remove_file(&path) {
                log::warn!("导入失败后的临时图片清理失败：{e}");
            }
            if let Some(dir) = path.parent().filter(|dir| {
                dir.file_name()
                    .is_some_and(|name| uuid::Uuid::parse_str(&name.to_string_lossy()).is_ok())
            }) {
                let _ = fs::remove_dir(dir);
            }
        }
    } else {
        material_batch.keep();
    }
    work
}

/// 导出基于一次固定的本机快照。网络补齐由调用方处理，不在数据库锁内传输。
pub struct ExportSnapshot {
    pub library: Library,
    pub recycled: Vec<RecycleRecord>,
    pub source: String,
    pub settings_scope: SettingsScope,
}
impl ExportSnapshot {
    pub fn filter(&mut self, kind: &str, category: Option<&str>) -> Result<()> {
        if let Some(root) = category.filter(|c| !c.is_empty()) {
            if !self
                .library
                .categories
                .iter()
                .any(|c| c.id == root && c.parent_id.is_none())
            {
                return Err("要导出的一级分类已不存在，请刷新后重试。".into());
            }
            self.library
                .categories
                .retain(|c| c.id == root || c.parent_id.as_deref() == Some(root));
            self.library.phrases.retain(|p| p.root_id == root);
            self.library.files.retain(|f| f.category_id == root);
            self.library.common_phrases.clear();
            self.recycled.clear();
            self.settings_scope = SettingsScope::None;
        }
        if kind == "excel" {
            self.recycled.clear();
        }
        if self.settings_scope == SettingsScope::None {
            self.library.settings = Settings::default();
        }
        self.library.data_dir.clear();
        Ok(())
    }
}
#[cfg(test)]
pub fn export(store: &Store, path: &Path, kind: &str, category: Option<&str>) -> Result<()> {
    let mut snapshot = ExportSnapshot {
        library: store.library()?,
        recycled: store.recycle_records()?,
        source: "private".into(),
        settings_scope: SettingsScope::Device,
    };
    snapshot.filter(kind, category)?;
    let temp = write_export(snapshot, path, kind, category.is_some(), &|| Ok(()))?;
    temp.persist(path).map_err(io_error)?;
    Ok(())
}
/// 成品写在目标目录的临时文件中，调用方核验库身份和授权后才替换用户选择的文件。
pub fn write_export(
    mut snapshot: ExportSnapshot,
    path: &Path,
    kind: &str,
    category: bool,
    check: &impl Fn() -> Result<()>,
) -> Result<tempfile::NamedTempFile> {
    let mut temp = tempfile::NamedTempFile::new_in(path.parent().ok_or("导出路径无效。")?)
        .map_err(io_error)?;
    check()?;
    if kind == "excel" {
        if !snapshot
            .library
            .phrases
            .iter()
            .any(|p| !p.text().trim().is_empty())
        {
            return Err("没有可导出的文字话术。图片和附件请使用话术包。".into());
        }
        write_excel(&snapshot.library, temp.path())?;
    } else if kind == "backup" {
        let mut zip = ZipWriter::new(temp.as_file_mut());
        let options =
            SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
        let mut included: HashMap<(String, String), String> = HashMap::new();
        let mut assets = vec![];
        for block in snapshot
            .library
            .phrases
            .iter_mut()
            .chain(snapshot.recycled.iter_mut().flat_map(|r| &mut r.phrases))
            .flat_map(|p| &mut p.segments)
            .flat_map(|s| &mut s.blocks)
        {
            let (path, asset_kind) = match block {
                Block::Image { path, .. } => (path, "image"),
                Block::File { path, .. } => (path, "file"),
                _ => continue,
            };
            check()?;
            let key = (path.clone(), asset_kind.to_owned());
            if let Some(name) = included.get(&key) {
                *path = name.clone();
                continue;
            }
            let mut file = fs::File::open(&*path)
                .map_err(|_| "源文件缺失或素材未缓存，无法完成导出。".to_owned())?;
            let expected = file.metadata().map_err(io_error)?.len();
            if expected == 0 || expected > MAX_IMPORT_BYTES {
                return Err("单个素材需为1字节至128MB。".into());
            }
            if asset_kind == "image" {
                decode_image(&fs::read(&*path).map_err(io_error)?)?;
            }
            let name = format!("assets/{}", id());
            zip.start_file(&name, options).map_err(io_error)?;
            let mut size = 0u64;
            let mut buf = [0u8; 64 * 1024];
            loop {
                check()?;
                let n = file.read(&mut buf).map_err(io_error)?;
                if n == 0 {
                    break;
                }
                size += n as u64;
                if size > MAX_IMPORT_BYTES {
                    return Err("素材超过128MB。".into());
                }
                zip.write_all(&buf[..n]).map_err(io_error)?;
            }
            if size != expected {
                return Err("素材在导出期间发生变化，请重新导出。".into());
            }
            assets.push(ArchiveAsset {
                path: name.clone(),
                kind: asset_kind.into(),
                size,
            });
            included.insert(key, name.clone());
            *path = name;
        }
        let package = BackupV2 {
            format: "shanyu-backup".into(),
            version: 2,
            source: snapshot.source,
            scope: if category { "category" } else { "full" }.into(),
            settings_scope: snapshot.settings_scope,
            library: snapshot.library,
            recycle_bin: snapshot.recycled,
            assets,
        };
        let mut metadata = serde_json::to_value(&package).map_err(io_error)?;
        if let Some(settings) = metadata["library"]["settings"].as_object_mut() {
            match package.settings_scope {
                SettingsScope::Portable => settings
                    .retain(|key, _| shanyu_team_model::private::SETTINGS.contains(&key.as_str())),
                SettingsScope::None => settings.clear(),
                SettingsScope::Device => {}
            }
        }
        let metadata = serde_json::to_vec(&metadata).map_err(io_error)?;
        if metadata.len() > 32 * 1024 * 1024 {
            return Err("备份元数据超过32MB，请按分类导出。".into());
        }
        zip.start_file("library.json", options).map_err(io_error)?;
        zip.write_all(&metadata).map_err(io_error)?;
        zip.finish().map_err(io_error)?;
    } else {
        return Err("导出类型无效。".into());
    }
    check()?;
    temp.as_file().sync_all().map_err(io_error)?;
    Ok(temp)
}
fn write_excel(library: &Library, path: &Path) -> Result<()> {
    let mut book = rust_xlsxwriter::Workbook::new();
    let sheet = book.add_worksheet();
    sheet.set_name("话术").map_err(io_error)?;
    for (i, name) in ["一级分类", "二级分类", "标题", "正文"].iter().enumerate() {
        sheet.write_string(0, i as u16, *name).map_err(io_error)?;
    }
    sheet.set_column_width(0, 16).map_err(io_error)?;
    sheet.set_column_width(1, 16).map_err(io_error)?;
    sheet.set_column_width(2, 28).map_err(io_error)?;
    sheet.set_column_width(3, 80).map_err(io_error)?;
    for (i, p) in library
        .phrases
        .iter()
        .filter(|p| !p.text().trim().is_empty())
        .enumerate()
    {
        let row = i as u32 + 1;
        let category = |id: &str| {
            library
                .categories
                .iter()
                .find(|c| c.id == id)
                .map(|c| c.name.as_str())
                .unwrap_or("")
        };
        for (col, text) in [
            category(&p.root_id).to_owned(),
            p.group_id.as_deref().map(category).unwrap_or("").to_owned(),
            p.title.clone(),
            p.text(),
        ]
        .iter()
        .enumerate()
        {
            sheet
                .write_string(row, col as u16, text)
                .map_err(io_error)?;
        }
    }
    book.save(path).map_err(io_error)
}
pub fn excel_template(path: &Path) -> Result<()> {
    let library = Library {
        categories: vec![Category {
            id: "common".into(),
            name: "常用".into(),
            kind: "phrase".into(),
            parent_id: None,
            position: 0,
            background: String::new(),
        }],
        phrases: vec![Phrase {
            id: id(),
            root_id: "common".into(),
            group_id: None,
            title: "欢迎语".into(),
            color: "#ff5100".into(),
            background: String::new(),
            shortcut: String::new(),
            position: 0,
            segments: vec![Segment {
                blocks: vec![Block::Text {
                    text: "您好，很高兴为您服务。".into(),
                }],
            }],
        }],
        files: vec![],
        common_phrases: vec![],
        settings: Settings::default(),
        data_dir: String::new(),
    };
    write_excel(&library, path)
}
pub fn decode_image(bytes: &[u8]) -> Result<image::DynamicImage> {
    let mut reader = image::ImageReader::new(std::io::Cursor::new(bytes))
        .with_guessed_format()
        .map_err(io_error)?;
    let mut limits = image::Limits::default();
    limits.max_alloc = Some(MAX_IMPORT_BYTES);
    limits.max_image_width = Some(16_384);
    limits.max_image_height = Some(16_384);
    reader.limits(limits);
    reader
        .decode()
        .map_err(|e| format!("图片无法读取或尺寸过大：{e}"))
}
pub fn import_images(dir: &Path, paths: &[String]) -> Result<Vec<Block>> {
    let mut blocks = vec![];
    let mut created = vec![];
    let work = (|| -> Result<()> {
        for path in paths {
            let file = Path::new(path);
            if fs::metadata(file).map_err(io_error)?.len() > MAX_IMPORT_BYTES {
                return Err("图片超过128MB。".into());
            }
            let image = decode_image(&fs::read(file).map_err(io_error)?)?;
            let destination = dir.join("images").join(format!("{}.png", id()));
            created.push(destination.clone());
            image
                .save_with_format(&destination, image::ImageFormat::Png)
                .map_err(io_error)?;
            let thumbnail = dir
                .join("thumbnails")
                .join(destination.file_name().unwrap());
            created.push(thumbnail.clone());
            image
                .thumbnail(256, 256)
                .save_with_format(thumbnail, image::ImageFormat::Png)
                .map_err(io_error)?;
            blocks.push(Block::Image {
                path: destination.to_string_lossy().into(),
                name: file
                    .file_name()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .into(),
            });
        }
        Ok(())
    })();
    if let Err(e) = work {
        for p in created {
            let _ = fs::remove_file(p);
        }
        return Err(e);
    }
    Ok(blocks)
}

/// 剪贴板图片先落在临时目录，再复用文件导入的解码、尺寸限制、PNG归一化和缩略图。
/// 临时文件随作用域释放；整批导入失败时，现有导入流程撤销本批素材及缩略图。
pub fn import_pasted_images(dir: &Path, images: &[Vec<u8>]) -> Result<Vec<Block>> {
    if images.len() > 9 {
        return Err("每条话术最多9张图片。".into());
    }
    let staging = tempfile::tempdir_in(dir.join("temp")).map_err(io_error)?;
    let mut paths = vec![];
    for (index, bytes) in images.iter().enumerate() {
        if bytes.len() as u64 > MAX_IMPORT_BYTES {
            return Err("图片超过128MB。".into());
        }
        let path = staging.path().join(format!("粘贴图片{}.png", index + 1));
        fs::write(&path, bytes).map_err(io_error)?;
        paths.push(path.to_string_lossy().into_owned());
    }
    import_images(dir, &paths)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn personal_archive(path: &Path, missing: bool, unsafe_path: bool) {
        use shanyu_team_model::private as wire;
        let root = id();
        let phrase = id();
        let file = id();
        let image = id();
        let mut png = std::io::Cursor::new(vec![]);
        image::DynamicImage::new_rgba8(2, 3)
            .write_to(&mut png, image::ImageFormat::Png)
            .unwrap();
        let png = png.into_inner();
        let snapshot = wire::Snapshot {
            generation: id(),
            version: 1,
            records: vec![
                wire::Record {
                    kind: wire::Kind::Category,
                    id: root.clone(),
                    version: 1,
                    value: Some(
                        serde_json::json!({"id":root,"name":"私人分类","parentId":null,"position":0,"background":""}),
                    ),
                },
                wire::Record {
                    kind: wire::Kind::Phrase,
                    id: phrase.clone(),
                    version: 1,
                    value: Some(
                        serde_json::json!({"id":phrase,"rootId":root,"groupId":null,"title":"个人备份","color":"","background":"","shortcut":"","position":0,"segments":[{"blocks":[{"kind":"image","assetId":image,"name":"图.png"},{"kind":"file","assetId":file,"name":"原附件.txt"}]}]}),
                    ),
                },
                wire::Record {
                    kind: wire::Kind::Setting,
                    id: "theme".into(),
                    version: 1,
                    value: Some(serde_json::json!("blue")),
                },
            ],
            assets: vec![
                wire::Asset {
                    id: image.clone(),
                    kind: "image".into(),
                    name: "图.png".into(),
                    size: png.len() as u64,
                },
                wire::Asset {
                    id: file.clone(),
                    kind: "file".into(),
                    name: "原附件.txt".into(),
                    size: 6,
                },
            ],
        };
        let mut zip = ZipWriter::new(fs::File::create(path).unwrap());
        let options = SimpleFileOptions::default();
        zip.start_file("private.json", options).unwrap();
        zip.write_all(&serde_json::to_vec(&snapshot).unwrap())
            .unwrap();
        zip.start_file(format!("assets/{image}"), options).unwrap();
        zip.write_all(&png).unwrap();
        if !missing {
            zip.start_file(format!("assets/{file}"), options).unwrap();
            zip.write_all("附件".as_bytes()).unwrap();
        }
        if unsafe_path {
            zip.start_file("../外部.txt", options).unwrap();
            zip.write_all(b"unsafe").unwrap();
        }
        zip.finish().unwrap();
    }
    #[test]
    fn personal_backup_restores_actual_image_and_attachment_without_device_settings() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("personal.zip");
        personal_archive(&path, false, false);
        let pending = preview(&path).unwrap();
        assert!(pending.material_dir.is_some());
        assert_eq!(pending.materials.len(), 2);
        assert_eq!(pending.library.settings.theme, "blue");
        let mut store = Store::open(&dir.path().join("restore")).unwrap();
        let mut settings = store.settings().unwrap();
        settings.pinned = true;
        store.save_settings(&settings).unwrap();
        apply(&mut store, &pending, false, false).unwrap();
        let lib = store.library().unwrap();
        assert!(lib.settings.pinned);
        assert_eq!(lib.phrases.len(), 1);
        let blocks = &lib.phrases[0].segments[0].blocks;
        let Block::Image { path, .. } = &blocks[0] else {
            panic!()
        };
        assert_eq!(image::open(path).unwrap().width(), 2);
        assert!(Path::new(path).starts_with(store.dir.join("images")));
        let Block::File { path, .. } = &blocks[1] else {
            panic!()
        };
        assert_eq!(fs::read_to_string(path).unwrap(), "附件");
        assert_eq!(Path::new(path).file_name().unwrap(), "原附件.txt");
    }
    #[test]
    fn incomplete_or_unsafe_personal_archive_never_modifies_library() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("personal.zip");
        personal_archive(&path, true, false);
        assert!(preview(&path).err().unwrap().contains("缺少素材"));
        personal_archive(&path, false, true);
        assert!(preview(&path).err().unwrap().contains("不安全"));
    }
    #[test]
    fn pasted_images_use_local_png_and_failed_batch_leaves_no_assets() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        let source = dir.path().join("source.png");
        image::RgbaImage::new(2, 3).save(&source).unwrap();
        let bytes = fs::read(source).unwrap();
        let blocks = import_pasted_images(&store.dir, std::slice::from_ref(&bytes)).unwrap();
        let Block::Image { path, name } = &blocks[0] else {
            panic!("粘贴应生成图片块")
        };
        let path = Path::new(path);
        assert_eq!(path.parent().unwrap(), store.dir.join("images"));
        assert_eq!(name, "粘贴图片1.png");
        assert_eq!(image::open(path).unwrap().width(), 2);
        let thumbnail = store.dir.join("thumbnails").join(path.file_name().unwrap());
        assert!(thumbnail.exists());
        fs::remove_file(path).unwrap();
        fs::remove_file(thumbnail).unwrap();
        assert!(import_pasted_images(&store.dir, &[bytes, vec![1, 2, 3]])
            .unwrap_err()
            .contains("图片"));
        assert!(import_pasted_images(&store.dir, &vec![vec![]; 10])
            .unwrap_err()
            .contains("9张"));
        for directory in ["images", "thumbnails", "temp"] {
            assert_eq!(fs::read_dir(store.dir.join(directory)).unwrap().count(), 0);
        }
    }

    #[test]
    fn invalid_recycle_context_rolls_back_import_and_removes_new_images() {
        let dir = tempfile::tempdir().unwrap();
        let mut source = crate::store::test_store(&dir.path().join("source")).unwrap();
        let group = source
            .save_category(Category {
                id: String::new(),
                name: "分组".into(),
                kind: "phrase".into(),
                parent_id: Some("common".into()),
                position: 0,
                background: String::new(),
            })
            .unwrap();
        let image = source.dir.join("images/recycled.png");
        image::RgbaImage::new(2, 2).save(&image).unwrap();
        let phrase = Phrase {
            id: String::new(),
            root_id: "common".into(),
            group_id: Some(group.clone()),
            title: "回收话术".into(),
            color: String::new(),
            background: String::new(),
            shortcut: String::new(),
            position: 0,
            segments: vec![Segment {
                blocks: vec![Block::Image {
                    path: image.to_string_lossy().into(),
                    name: "回收图片".into(),
                }],
            }],
        };
        let phrase_id = source.save_phrase(phrase, None).unwrap();
        source.delete_item(&phrase_id, "phrase", true).unwrap();
        let package = dir.path().join("backup.zip");
        export(&source, &package, "backup", None).unwrap();
        let pending = preview(&package).unwrap();
        let mut target = crate::store::test_store(&dir.path().join("target")).unwrap();
        let before = serde_json::to_value(target.library().unwrap()).unwrap();
        for case in 0..6 {
            let mut invalid = pending.clone();
            let record = &mut invalid.recycle_bin[0];
            match case {
                0 => record.categories[0].background = "错误颜色".into(),
                1 => {
                    record
                        .categories
                        .iter_mut()
                        .find(|c| c.id == group)
                        .unwrap()
                        .parent_id = Some(group.clone())
                }
                2 => record.phrases[0].root_id = group.clone(),
                3 => record.phrases[0].group_id = Some("local-files".into()),
                4 => record.kind = "不支持的类型".into(),
                _ => record.deleted_at = u64::MAX,
            }
            assert!(apply(&mut target, &invalid, false, true)
                .unwrap_err()
                .contains("回收站"));
            assert_eq!(
                serde_json::to_value(target.library().unwrap()).unwrap(),
                before
            );
            assert!(target.recycle_records().unwrap().is_empty());
            assert_eq!(fs::read_dir(target.dir.join("images")).unwrap().count(), 0);
        }
        apply(&mut target, &pending, false, true).unwrap();
        assert_eq!(target.recycle_records().unwrap().len(), 1);
    }
    #[test]
    fn backup_includes_recycled_images_and_keeps_restoration_inactive_until_requested() {
        let dir = tempfile::tempdir().unwrap();
        let mut original = crate::store::test_store(&dir.path().join("original")).unwrap();
        let image = original.dir.join("images/recycled.png");
        image::RgbaImage::new(2, 2).save(&image).unwrap();
        let p = Phrase {
            id: String::new(),
            root_id: "common".into(),
            group_id: None,
            title: "已回收".into(),
            color: "#ff5100".into(),
            background: String::new(),
            shortcut: String::new(),
            position: 0,
            segments: vec![Segment {
                blocks: vec![Block::Image {
                    path: image.to_string_lossy().into(),
                    name: "回收图片".into(),
                }],
            }],
        };
        let id = original.save_phrase(p, None).unwrap();
        original.delete_item(&id, "phrase", true).unwrap();
        let backup = dir.path().join("回收备份.zip");
        export(&original, &backup, "backup", None).unwrap();
        let pending = preview(&backup).unwrap();
        assert_eq!(pending.preview.recycled, 1);
        assert_eq!(pending.preview.images, 1);
        let mut imported = crate::store::test_store(&dir.path().join("imported")).unwrap();
        apply(&mut imported, &pending, true, false).unwrap();
        assert!(imported.library().unwrap().phrases.is_empty());
        let record = imported.recycle_records().unwrap().remove(0);
        assert_eq!(record.phrases[0].root_id, "common");
        imported.restore_recycled(&[record.id], false).unwrap();
        let library = imported.library().unwrap();
        assert_eq!(
            library
                .categories
                .iter()
                .filter(|c| c.kind == "phrase" && c.parent_id.is_none())
                .count(),
            1
        );
        if let Block::Image { path, .. } = &library.phrases[0].segments[0].blocks[0] {
            assert!(Path::new(path).is_file());
            assert!(Path::new(path).starts_with(&imported.dir));
        } else {
            panic!("回收图片丢失");
        }
        assert!(image.exists());
        export(
            &original,
            &dir.path().join("分类备份.zip"),
            "backup",
            Some("common"),
        )
        .unwrap();
        assert_eq!(
            preview(&dir.path().join("分类备份.zip"))
                .unwrap()
                .preview
                .recycled,
            0
        );
    }
    #[test]
    fn full_backup_roundtrip_preserves_order_images_and_external_paths() {
        let dir = tempfile::tempdir().unwrap();
        let mut store = crate::store::test_store(&dir.path().join("original")).unwrap();
        let source = dir.path().join("截图.png");
        image::RgbaImage::from_pixel(2, 2, image::Rgba([40, 80, 120, 255]))
            .save(&source)
            .unwrap();
        let mut blocks = import_images(&store.dir, &[source.to_string_lossy().into()]).unwrap();
        blocks.insert(
            0,
            Block::Text {
                text: "之前".into(),
            },
        );
        let attachment = dir.path().join("原文件.txt");
        fs::write(&attachment, b"actual attachment").unwrap();
        // Windows CI 的 TEMP 可能使用 8.3 短路径；关联按规范路径保存，往返仍精确核对该源文件。
        let attachment = attachment.canonicalize().unwrap();
        store
            .add_files("local-files", vec![external_path(&attachment)])
            .unwrap();
        blocks.push(Block::File {
            path: external_path(&attachment),
            name: "原文件.txt".into(),
        });
        let p = Phrase {
            id: String::new(),
            root_id: "common".into(),
            group_id: None,
            title: "混合".into(),
            color: "#ff5100".into(),
            background: String::new(),
            shortcut: String::new(),
            position: 0,
            segments: vec![
                Segment { blocks },
                Segment {
                    blocks: vec![Block::Text {
                        text: "之后".into(),
                    }],
                },
            ],
        };
        store.save_phrase(p, None).unwrap();
        let mut root = store
            .categories()
            .unwrap()
            .into_iter()
            .find(|c| c.id == "common")
            .unwrap();
        root.background = "#356fa1".into();
        store.save_category(root).unwrap();
        let settings = Settings {
            category_height: 87,
            show_title: false,
            theme: "pink".into(),
            title_color: "#0000ff".into(),
            phrase_double_click: "send".into(),
            common_single_click: "insert".into(),
            dock_search: true,
            dock_common_rows: 3,
            ..Settings::default()
        };
        store.save_settings(&settings).unwrap();
        store
            .save_common_phrase(CommonPhrase {
                id: String::new(),
                text: "谢谢您的反馈".into(),
                position: 0,
            })
            .unwrap();
        let package = dir.path().join("备份.zip");
        export(&store, &package, "backup", None).unwrap();
        let pending = preview(&package).unwrap();
        let mut restored = Store::open(&dir.path().join("restored")).unwrap();
        assert!(restored.categories().unwrap().is_empty());
        apply(&mut restored, &pending, true, true).unwrap();
        assert_eq!(
            restored.categories().unwrap().len(),
            pending.library.categories.len()
        );
        assert_eq!(pending.preview.common_phrases, 1);
        assert_eq!(
            restored
                .categories()
                .unwrap()
                .iter()
                .find(|c| c.name == "常用")
                .unwrap()
                .background,
            "#356fa1"
        );
        assert_eq!(restored.settings().unwrap().category_height, 87);
        assert!(!restored.settings().unwrap().show_title);
        assert_eq!(restored.settings().unwrap().theme, "pink");
        assert_eq!(restored.settings().unwrap().title_color, "#0000ff");
        assert_eq!(restored.settings().unwrap().phrase_double_click, "send");
        assert_eq!(restored.settings().unwrap().common_single_click, "insert");
        assert!(restored.settings().unwrap().dock_search);
        assert_eq!(restored.settings().unwrap().dock_common_rows, 3);
        assert_eq!(restored.common_phrases().unwrap()[0].text, "谢谢您的反馈");
        let p = &restored.library().unwrap().phrases[0];
        assert_eq!(p.segments.len(), 2);
        assert!(matches!(p.segments[0].blocks[1], Block::Image { .. }));
        assert!(matches!(p.segments[0].blocks[2], Block::File { .. }));
        if let Block::File { path, .. } = &p.segments[0].blocks[2] {
            assert_ne!(path, &external_path(&attachment));
            assert_eq!(fs::read(path).unwrap(), b"actual attachment");
        }
        assert_eq!(
            restored.library().unwrap().files[0].path,
            external_path(&attachment)
        );
    }
    #[test]
    fn excel_template_can_be_previewed() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("模板.xlsx");
        excel_template(&path).unwrap();
        let p = preview(&path).unwrap();
        assert_eq!(p.preview.phrases, 1);
        assert_eq!(p.library.phrases[0].title, "欢迎语");
    }
    #[test]
    fn prepared_batch_rolls_back_materials_and_retries_once_after_reopen() {
        let dir = tempfile::tempdir().unwrap();
        let template = dir.path().join("template.xlsx");
        excel_template(&template).unwrap();
        let mut pending = preview(&template).unwrap();
        let attachment = dir.path().join("source.txt");
        fs::write(&attachment, b"frozen attachment").unwrap();
        let image = dir.path().join("source.png");
        image::RgbaImage::new(2, 2).save(&image).unwrap();
        pending.library.phrases[0].segments[0].blocks.extend([
            Block::File {
                path: external_path(&attachment),
                name: "source.txt".into(),
            },
            Block::Image {
                path: external_path(&image),
                name: "source.png".into(),
            },
        ]);
        pending.library.common_phrases.push(CommonPhrase {
            id: "old".into(),
            text: "常用短语".into(),
            position: 0,
        });
        prepare_ids(&mut pending).unwrap();
        let phrase_id = pending.library.phrases[0].id.clone();
        freeze_materials(&mut pending, &dir.path().join("frozen"), &|| Ok(())).unwrap();
        fs::write(&attachment, b"changed after preview").unwrap();
        let pending: PendingImport =
            serde_json::from_slice(&serde_json::to_vec(&pending).unwrap()).unwrap();
        let target = dir.path().join("target");
        let mut store = Store::open(&target).unwrap();
        store.conn.execute_batch("CREATE TRIGGER reject_common BEFORE INSERT ON common_phrases BEGIN SELECT RAISE(ABORT,'模拟失败'); END;").unwrap();
        assert!(apply_prepared(&mut store, &pending, false, false).is_err());
        assert!(store.library().unwrap().phrases.is_empty());
        assert!(store.categories().unwrap().is_empty());
        assert!(!store.imported(&pending.preview.id).unwrap());
        assert!(crate::private_local::dirty(&store).unwrap().is_empty());
        assert_eq!(fs::read_dir(target.join("images")).unwrap().count(), 0);
        assert_eq!(fs::read_dir(target.join("assets")).unwrap().count(), 0);
        assert_eq!(fs::read(&attachment).unwrap(), b"changed after preview");
        store
            .conn
            .execute_batch("DROP TRIGGER reject_common;")
            .unwrap();
        apply_prepared(&mut store, &pending, false, false).unwrap();
        assert!(store.imported(&pending.preview.id).unwrap());
        assert!(!crate::private_local::dirty(&store).unwrap().is_empty());
        let library = store.library().unwrap();
        assert_eq!(library.phrases[0].id, phrase_id);
        let Block::File { path, .. } = &library.phrases[0].segments[0].blocks[1] else {
            panic!()
        };
        assert_eq!(fs::read(path).unwrap(), b"frozen attachment");
        drop(store);
        let mut store = Store::open(&target).unwrap();
        let image_count = fs::read_dir(target.join("images")).unwrap().count();
        apply_prepared(&mut store, &pending, false, false).unwrap();
        assert_eq!(store.library().unwrap().phrases.len(), 1);
        assert_eq!(store.common_phrases().unwrap().len(), 1);
        assert_eq!(
            fs::read_dir(target.join("images")).unwrap().count(),
            image_count
        );
    }
    #[test]
    fn prepared_merge_keeps_ambiguous_categories_independent() {
        let dir = tempfile::tempdir().unwrap();
        let template = dir.path().join("template.xlsx");
        excel_template(&template).unwrap();
        let mut pending = preview(&template).unwrap();
        prepare_ids(&mut pending).unwrap();
        let root = pending.library.categories[0].id.clone();
        let mut store = crate::store::test_store(&dir.path().join("data")).unwrap();
        store
            .save_category(Category {
                id: String::new(),
                name: "常用".into(),
                kind: "phrase".into(),
                parent_id: None,
                position: 0,
                background: String::new(),
            })
            .unwrap();
        note_merge_ambiguity(&mut pending, &store.categories().unwrap(), true);
        assert!(pending
            .preview
            .notes
            .iter()
            .any(|n| n.contains("多个同级同名项")));
        apply_prepared(&mut store, &pending, true, false).unwrap();
        assert_eq!(
            store
                .categories()
                .unwrap()
                .iter()
                .filter(|c| c.name == "常用")
                .count(),
            3
        );
        assert_eq!(store.library().unwrap().phrases[0].root_id, root);
    }
    #[test]
    fn prepared_settings_restore_desired_config_without_applying_send_actions() {
        let dir = tempfile::tempdir().unwrap();
        let template = dir.path().join("template.xlsx");
        excel_template(&template).unwrap();
        let mut pending = preview(&template).unwrap();
        prepare_ids(&mut pending).unwrap();
        pending.library.settings.theme = "blue".into();
        pending.library.settings.enter = "ctrl-enter".into();
        let mut store = Store::open(&dir.path().join("target")).unwrap();
        apply_prepared(&mut store, &pending, false, true).unwrap();
        assert_eq!(store.settings().unwrap().enter, "enter");
        assert_eq!(store.settings().unwrap().theme, "blue");
        let desired = crate::private_local::local_records(&store).unwrap();
        assert_eq!(
            desired[&(shanyu_team_model::private::Kind::Setting, "enter".into())],
            Some(serde_json::json!("ctrl-enter"))
        );
        let mut second = preview(&template).unwrap();
        prepare_ids(&mut second).unwrap();
        // 明确恢复旧发送值，应替换待确认值，而不是因已应用值相同忽略本次恢复。
        apply_prepared(&mut store, &second, false, true).unwrap();
        let desired = crate::private_local::local_records(&store).unwrap();
        assert_eq!(
            desired[&(shanyu_team_model::private::Kind::Setting, "enter".into())],
            Some(serde_json::json!("enter"))
        );
        assert_eq!(store.library().unwrap().phrases.len(), 2);
    }
    #[test]
    fn portable_and_category_packages_omit_device_or_personal_settings() {
        let dir = tempfile::tempdir().unwrap();
        let template = dir.path().join("template.xlsx");
        excel_template(&template).unwrap();
        let library = preview(&template).unwrap().library;
        for scope in [SettingsScope::Portable, SettingsScope::None] {
            let path = dir.path().join("package.zip");
            let snapshot = ExportSnapshot {
                library: library.clone(),
                recycled: vec![],
                source: "private".into(),
                settings_scope: scope,
            };
            write_export(
                snapshot,
                &path,
                "backup",
                scope == SettingsScope::None,
                &|| Ok(()),
            )
            .unwrap()
            .persist(&path)
            .unwrap();
            let mut zip = ZipArchive::new(fs::File::open(&path).unwrap()).unwrap();
            let metadata: serde_json::Value =
                serde_json::from_reader(zip.by_name("library.json").unwrap()).unwrap();
            assert!(metadata["library"]["settings"].get("startup").is_none());
            assert!(metadata["library"]["settings"].get("dock").is_none());
            assert!(metadata["library"]["settings"].get("pinned").is_none());
            if scope == SettingsScope::None {
                assert!(metadata["library"]["settings"]
                    .as_object()
                    .unwrap()
                    .is_empty());
            } else {
                assert_eq!(metadata["library"]["settings"]["enter"], "enter");
            }
            drop(zip);
            assert!(preview(&path).unwrap().settings_scope == scope);
        }
    }
    #[test]
    fn export_missing_material_and_cancellation_preserve_existing_backup() {
        let dir = tempfile::tempdir().unwrap();
        let template = dir.path().join("template.xlsx");
        excel_template(&template).unwrap();
        let mut library = preview(&template).unwrap().library;
        library.phrases[0].segments[0].blocks.push(Block::File {
            path: external_path(&dir.path().join("missing.txt")),
            name: "missing.txt".into(),
        });
        let path = dir.path().join("existing.zip");
        fs::write(&path, b"previous complete backup").unwrap();
        let snapshot = || ExportSnapshot {
            library: library.clone(),
            recycled: vec![],
            source: "private".into(),
            settings_scope: SettingsScope::Device,
        };
        assert!(write_export(snapshot(), &path, "backup", false, &|| Ok(())).is_err());
        assert_eq!(fs::read(&path).unwrap(), b"previous complete backup");
        library.phrases[0].segments[0].blocks.pop();
        let snapshot = ExportSnapshot {
            library,
            recycled: vec![],
            source: "private".into(),
            settings_scope: SettingsScope::Device,
        };
        let calls = std::cell::Cell::new(0);
        assert!(write_export(snapshot, &path, "backup", false, &|| {
            calls.set(calls.get() + 1);
            if calls.get() > 1 {
                Err("操作已取消。".into())
            } else {
                Ok(())
            }
        })
        .is_err());
        assert_eq!(fs::read(&path).unwrap(), b"previous complete backup");
        assert_eq!(fs::read_dir(dir.path()).unwrap().count(), 2);
    }
    #[test]
    fn legacy_backup_remains_readable_and_v2_errors_do_not_echo_content() {
        let dir = tempfile::tempdir().unwrap();
        let template = dir.path().join("template.xlsx");
        excel_template(&template).unwrap();
        let library = preview(&template).unwrap().library;
        let path = dir.path().join("legacy.zip");
        let mut zip = ZipWriter::new(fs::File::create(&path).unwrap());
        zip.start_file("library.json", SimpleFileOptions::default())
            .unwrap();
        zip.write_all(
            &serde_json::to_vec(&Backup {
                format: "shanyu-backup".into(),
                version: 1,
                library: library.clone(),
                recycle_bin: vec![],
            })
            .unwrap(),
        )
        .unwrap();
        zip.finish().unwrap();
        assert_eq!(preview(&path).unwrap().library.phrases.len(), 1);
        let snapshot = ExportSnapshot {
            library,
            recycled: vec![],
            source: "private".into(),
            settings_scope: SettingsScope::Device,
        };
        write_export(snapshot, &path, "backup", false, &|| Ok(()))
            .unwrap()
            .persist(&path)
            .unwrap();
        let mut metadata: serde_json::Value = serde_json::from_reader(
            ZipArchive::new(fs::File::open(&path).unwrap())
                .unwrap()
                .by_name("library.json")
                .unwrap(),
        )
        .unwrap();
        metadata["library"]["settings"]["enter"] =
            serde_json::json!("private-body-must-not-appear");
        // 将类型破坏成含正文的对象，解析错误仅显示固定中文提示。
        metadata["library"]["phrases"][0]["position"] =
            serde_json::json!({"text":"private-body-must-not-appear"});
        let mut zip = ZipWriter::new(fs::File::create(&path).unwrap());
        zip.start_file("library.json", SimpleFileOptions::default())
            .unwrap();
        zip.write_all(&serde_json::to_vec(&metadata).unwrap())
            .unwrap();
        zip.finish().unwrap();
        assert!(preview(&path).err().unwrap().contains("备份内容无效"));
        assert!(!preview(&path).err().unwrap().contains("private-body"));
    }
    #[test]
    fn category_export_excludes_other_roots_and_independent_common_phrases() {
        let dir = tempfile::tempdir().unwrap();
        let template = dir.path().join("模板.xlsx");
        excel_template(&template).unwrap();
        let library = preview(&template).unwrap().library;
        let mut store = crate::store::test_store(&dir.path().join("data")).unwrap();
        store.import(&library, true, false, &[]).unwrap();
        store
            .save_common_phrase(CommonPhrase {
                id: String::new(),
                text: "独立短语".into(),
                position: 0,
            })
            .unwrap();
        let package = dir.path().join("分类.zip");
        export(&store, &package, "backup", Some("common")).unwrap();
        let scoped = preview(&package).unwrap().library;
        assert_eq!(scoped.phrases.len(), 1);
        assert!(scoped.common_phrases.is_empty());
        assert!(scoped
            .categories
            .iter()
            .all(|c| c.id == "common" || c.parent_id.as_deref() == Some("common")));
        assert!(export(&store, &package, "backup", Some("已删除分类")).is_err());
        assert_eq!(preview(&package).unwrap().library.phrases.len(), 1);
    }
    #[test]
    fn unsafe_zip_path_is_rejected() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("坏包.zip");
        let mut zip = ZipWriter::new(fs::File::create(&path).unwrap());
        zip.start_file("../outside.txt", SimpleFileOptions::default())
            .unwrap();
        zip.write_all(b"bad").unwrap();
        zip.finish().unwrap();
        assert!(matches!(preview(&path), Err(error) if error.contains("不安全")));
    }
    #[test]
    fn excel_text_export_roundtrips_when_library_also_has_media_only_phrases() {
        let dir = tempfile::tempdir().unwrap();
        let template = dir.path().join("模板.xlsx");
        excel_template(&template).unwrap();
        let mut library = preview(&template).unwrap().library;
        let mut media = library.phrases[0].clone();
        media.title = "纯文件".into();
        media.segments = vec![Segment {
            blocks: vec![Block::File {
                path: dir.path().join("附件.txt").to_string_lossy().into(),
                name: "附件.txt".into(),
            }],
        }];
        library.phrases.push(media);
        let mut store = crate::store::test_store(&dir.path().join("data")).unwrap();
        store.import(&library, true, false, &[]).unwrap();
        let output = dir.path().join("文字.xlsx");
        export(&store, &output, "excel", None).unwrap();
        let imported = preview(&output).unwrap();
        assert_eq!(imported.library.phrases.len(), 1);
        assert_eq!(imported.library.phrases[0].title, "欢迎语");
    }
}
