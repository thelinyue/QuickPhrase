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
#[derive(Clone)]
pub struct PendingImport {
    pub preview: ImportPreview,
    pub library: Library,
    pub images: HashMap<String, Vec<u8>>,
    pub recycle_bin: Vec<RecycleRecord>,
}
#[derive(Clone, Serialize)]
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
#[derive(Clone, Serialize)]
pub struct PreviewRow {
    pub category: String,
    pub title: String,
    pub text: String,
}

/// 备份采用可移植 JSON + 图片的 ZIP。读取时不解压到任意路径，图片只写入受控素材目录。
/// 预览保存解析后的内容，确认时再事务导入，避免“预览一个文件、实际导入另一个文件”。
pub fn preview(path: &Path) -> Result<PendingImport> {
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
    })
}
type BackupContents = (Library, HashMap<String, Vec<u8>>, Vec<RecycleRecord>);
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
            .map_err(|e| format!("备份内容无法读取：{e}"))?
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

pub fn apply(
    store: &mut Store,
    pending: &PendingImport,
    merge: bool,
    restore_settings: bool,
) -> Result<()> {
    let mut library = pending.library.clone();
    let mut recycled = pending.recycle_bin.clone();
    let mut created = vec![];
    let mut remapped = HashMap::new();
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
            if let Block::Image { path, .. } = b {
                *path = remapped.get(path).cloned().ok_or("导入图片缺少素材。")?;
            }
        }
        store.import(&library, merge, restore_settings, &recycled)
    })();
    if work.is_err() {
        for path in created {
            if let Err(e) = fs::remove_file(path) {
                log::warn!("导入失败后的临时图片清理失败：{e}");
            }
        }
    }
    work
}

pub fn export(store: &Store, path: &Path, kind: &str, category: Option<&str>) -> Result<()> {
    let mut library = store.library()?;
    // 全库完整备份包含回收站及其图片；按分类导出和 Excel 仍只导出活动内容。
    let mut recycled = if kind == "backup" && category.filter(|c| !c.is_empty()).is_none() {
        store.recycle_records()?
    } else {
        vec![]
    };
    if let Some(root) = category.filter(|c| !c.is_empty()) {
        if !library
            .categories
            .iter()
            .any(|c| c.id == root && c.parent_id.is_none())
        {
            return Err("要导出的一级分类已不存在，请刷新后重试。".into());
        }
        library
            .categories
            .retain(|c| c.id == root || c.parent_id.as_deref() == Some(root));
        library.phrases.retain(|p| p.root_id == root);
        library.files.retain(|f| f.category_id == root);
        library.common_phrases.clear();
    }
    let mut tmp = tempfile::NamedTempFile::new_in(path.parent().ok_or("导出路径无效。")?)
        .map_err(io_error)?;
    if kind == "excel" {
        if !library.phrases.iter().any(|p| !p.text().trim().is_empty()) {
            return Err("没有可导出的文字话术。图片和文件话术请使用完整备份。".into());
        }
        write_excel(&library, tmp.path())?;
    } else if kind == "backup" {
        let mut zip = ZipWriter::new(tmp.as_file_mut());
        let options =
            SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
        let mut included = HashSet::new();
        for b in library
            .phrases
            .iter_mut()
            .chain(recycled.iter_mut().flat_map(|r| &mut r.phrases))
            .flat_map(|p| &mut p.segments)
            .flat_map(|s| &mut s.blocks)
        {
            if let Block::Image { path, .. } = b {
                let source = PathBuf::from(&*path);
                let name = format!(
                    "images/{}",
                    source
                        .file_name()
                        .ok_or("图片素材路径无效。")?
                        .to_string_lossy()
                );
                if included.insert(name.clone()) {
                    let bytes = fs::read(&source)
                        .map_err(|e| format!("图片素材无法备份：{}：{e}", source.display()))?;
                    zip.start_file(&name, options).map_err(io_error)?;
                    zip.write_all(&bytes).map_err(io_error)?;
                }
                *path = name;
            }
        }
        library.data_dir.clear();
        zip.start_file("library.json", options).map_err(io_error)?;
        serde_json::to_writer(
            &mut zip,
            &Backup {
                format: "shanyu-backup".into(),
                version: 1,
                library,
                recycle_bin: recycled,
            },
        )
        .map_err(io_error)?;
        zip.finish().map_err(io_error)?;
    } else {
        return Err("导出类型无效。".into());
    }
    tmp.as_file().sync_all().map_err(io_error)?;
    tmp.persist(path).map_err(io_error)?;
    Ok(())
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
        blocks.push(Block::File {
            path: dir.path().join("原文件.txt").to_string_lossy().into(),
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
