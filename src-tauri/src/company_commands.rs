//! UI 只提交文件 ID 和正文位置。Rust 核验账号、目标库与版本，个人副本在保存事务中登记。
use crate::{
    model::*,
    private_commands, private_local,
    store::{db_error, Store},
    team::{CompanySelection, CompanyTransfer, CompanyUpload, CompanyView},
    AppState,
};
use rusqlite::OptionalExtension;
use shanyu_team_model::CompanyFile;
use std::{
    collections::HashMap,
    fs,
    path::{Path, PathBuf},
};
use tauri::State;

#[tauri::command]
pub async fn get_company_files(
    state: State<'_, AppState>,
    session_epoch: u64,
    recycled: bool,
    query: String,
) -> Result<CompanyView> {
    state
        .teams
        .company_view(session_epoch, recycled, query)
        .await
}
#[tauri::command]
pub async fn prepare_company_upload(
    state: State<'_, AppState>,
    session_epoch: u64,
    path: String,
) -> Result<CompanyUpload> {
    state
        .teams
        .prepare_company_upload_async(session_epoch, path)
        .await
}
#[tauri::command]
pub fn enqueue_company_uploads(
    app: tauri::AppHandle,
    state: State<AppState>,
    session_epoch: u64,
    paths: Vec<String>,
) -> Result<Vec<CompanyTransfer>> {
    state
        .teams
        .enqueue_company_uploads(&app, session_epoch, paths)
}
#[tauri::command]
pub async fn upload_company_file(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    id: String,
) -> Result<CompanyFile> {
    state
        .teams
        .company_upload(&app, session_epoch, id, true)
        .await
}

#[tauri::command]
pub async fn query_company_upload(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    id: String,
) -> Result<CompanyFile> {
    state
        .teams
        .company_upload(&app, session_epoch, id, false)
        .await
}
#[tauri::command]
pub fn get_company_transfers(
    state: State<AppState>,
    session_epoch: u64,
) -> Result<Vec<CompanyTransfer>> {
    state.teams.company_transfer_view(session_epoch)
}
#[tauri::command]
pub fn clear_company_transfers(
    state: State<AppState>,
    session_epoch: u64,
    direction: String,
) -> Result<Vec<CompanyTransfer>> {
    state
        .teams
        .clear_company_transfers(session_epoch, &direction)
}
#[tauri::command]
pub async fn download_company_file(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    id: String,
) -> Result<()> {
    state
        .teams
        .company_download(&app, session_epoch, &id)
        .await
        .map(|_| ())
}
#[tauri::command]
pub fn cancel_company_transfer(
    state: State<AppState>,
    session_epoch: u64,
    id: String,
) -> Result<()> {
    state.teams.cancel_company_transfer(session_epoch, &id)
}
#[tauri::command]
pub async fn manage_company_file(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    id: String,
    operation: String,
    confirm_name: String,
) -> Result<()> {
    state
        .teams
        .company_manage(&app, session_epoch, id, operation, confirm_name)
        .await
}

/// 副本只复用目标库内的受控文件；映射不延长源文件生命周期，也不会跨账号共享个人数据。
fn owned_copy(store: &Store, key: &str, size: u64) -> Result<Option<String>> {
    let path = store
        .conn
        .query_row(
            "SELECT path FROM company_copies WHERE source=?1",
            [key],
            |r| r.get::<_, String>(0),
        )
        .optional()
        .map_err(db_error)?;
    let owned = fs::canonicalize(&store.dir)
        .map_err(db_error)?
        .join("assets");
    Ok(path.filter(|path| {
        fs::canonicalize(path).is_ok_and(|path| path.starts_with(&owned))
            && fs::metadata(path).is_ok_and(|m| m.is_file() && m.len() == size)
    }))
}
fn copies(
    store: &Store,
    phrase: &mut Phrase,
    server: &str,
    user: &str,
    selections: &[CompanySelection],
    downloaded: &HashMap<String, (CompanyFile, PathBuf)>,
) -> Result<(private_local::MaterialBatch, Vec<(String, String)>)> {
    let mut batch = private_local::MaterialBatch::default();
    let mut mappings: Vec<(String, String)> = vec![];
    let owned = fs::canonicalize(&store.dir).map_err(db_error)?;
    for selection in selections {
        let block = phrase
            .segments
            .get_mut(selection.segment)
            .and_then(|s| s.blocks.get_mut(selection.block))
            .ok_or("云文件位置无效。")?;
        let Block::File { path, name } = block else {
            return Err("云文件只能添加为文件附件。".into());
        };
        let (file, source) = downloaded
            .get(&selection.asset_id)
            .ok_or("云文件未下载。")?;
        let key = serde_json::to_string(&(server, user, &selection.asset_id)).map_err(db_error)?;
        let usable = mappings
            .iter()
            .find(|(k, _)| k == &key)
            .map(|(_, p)| p.clone())
            .or(owned_copy(store, &key, file.size)?);
        let target = if let Some(path) = usable {
            path
        } else {
            let folder = owned.join("assets").join(id());
            fs::create_dir_all(&folder).map_err(db_error)?;
            let target = folder.join(source.file_name().ok_or("文件名无效。")?);
            batch.track(target.clone());
            if fs::copy(source, &target).map_err(db_error)? != file.size {
                return Err("云文件副本不完整。".into());
            }
            external_path(&target)
        };
        *path = target.clone();
        *name = file.name.clone();
        if !mappings.iter().any(|(k, _)| k == &key) {
            mappings.push((key, target));
        }
    }
    Ok((batch, mappings))
}
#[tauri::command]
pub async fn save_company_private_phrase(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    mut phrase: Phrase,
    after_id: Option<String>,
    expected_revision: Option<u64>,
    library_id: String,
    company_files: Vec<CompanySelection>,
) -> Result<String> {
    let _activity = crate::update_activity::Activity::enter()?;
    CompanySelection::validate(&phrase, &company_files)?;
    let first = company_files.first().ok_or("请先选择云文件。")?;
    let ctx = state.teams.company_context(
        first.session_epoch,
        shanyu_team_model::CompanyOperation::Read,
    )?;
    let mut downloaded = HashMap::new();
    for selection in &company_files {
        if selection.session_epoch != first.session_epoch {
            return Err("云文件来自不同账号会话，请重新选择。".into());
        }
        let file = state.teams.company_selection(selection).await?;
        if !downloaded.contains_key(&selection.asset_id) {
            // 重新登录会清空云文件缓存，但可用个人副本仍属于目标库，复用时无需再次下载本体。
            let key = serde_json::to_string(&(&ctx.server, &ctx.user_id, &selection.asset_id))
                .map_err(db_error)?;
            let existing = state.teams.with_account(&ctx, || {
                let store = private_commands::active_store(&state, Some(&library_id))?;
                owned_copy(&store, &key, file.size)
            })?;
            let material = if let Some(path) = existing {
                (file, PathBuf::from(path))
            } else {
                state
                    .teams
                    .company_download(&app, first.session_epoch, &selection.asset_id)
                    .await?
            };
            downloaded.insert(selection.asset_id.clone(), material);
        }
    }
    for selection in &company_files {
        state.teams.company_selection(selection).await?;
    }
    let saved = state.teams.with_account(&ctx, || {
        crate::shortcut_conflict(
            &state.native.settings.read().unwrap(),
            std::slice::from_ref(&phrase),
        )?;
        let mut store = private_commands::active_store(&state, Some(&library_id))?;
        if expected_revision.is_some_and(|r| {
            private_local::revision(&store, "phrase", &phrase.id).is_ok_and(|v| v != r)
        }) {
            return Err("话术已更新，草稿保留，请核对新版。".into());
        }
        let (batch, mappings) = copies(
            &store,
            &mut phrase,
            &ctx.server,
            &ctx.user_id,
            &company_files,
            &downloaded,
        )?;
        let saved = store.save_phrase_with_company(phrase, after_id.as_deref(), &mappings)?;
        batch.keep();
        Ok(saved)
    })?;
    crate::changed(&app, &state)?;
    Ok(saved)
}
#[tauri::command]
pub async fn save_company_file_as(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    id: String,
    path: String,
) -> Result<()> {
    let _activity = crate::update_activity::Activity::enter()?;
    let ctx = state
        .teams
        .company_context(session_epoch, shanyu_team_model::CompanyOperation::Read)?;
    let (_, source) = state
        .teams
        .company_download(&app, session_epoch, &id)
        .await?;
    let target = Path::new(&path);
    let mut temp = tempfile::NamedTempFile::new_in(target.parent().ok_or("目标目录无效。")?)
        .map_err(db_error)?;
    std::io::copy(&mut fs::File::open(source).map_err(db_error)?, &mut temp).map_err(db_error)?;
    state.teams.with_account(&ctx, || {
        temp.persist_noclobber(target)
            .map_err(|_| "目标文件已存在或无法写入，请选择新的文件名。")?;
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    fn phrase(root: &str) -> Phrase {
        Phrase {
            id: String::new(),
            root_id: root.into(),
            group_id: None,
            title: "资料".into(),
            color: String::new(),
            background: String::new(),
            shortcut: String::new(),
            position: 0,
            segments: vec![Segment {
                blocks: vec![Block::File {
                    path: String::new(),
                    name: "价格表.xlsx".into(),
                }],
            }],
        }
    }
    type Fixture = (
        tempfile::TempDir,
        Store,
        String,
        Vec<CompanySelection>,
        HashMap<String, (CompanyFile, PathBuf)>,
    );
    fn setup() -> Fixture {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(&dir.path().join("private")).unwrap();
        let root = store
            .save_category(Category {
                id: String::new(),
                name: "资料".into(),
                kind: "phrase".into(),
                parent_id: None,
                position: 0,
                background: String::new(),
            })
            .unwrap();
        let cloud = id();
        let source = dir.path().join("价格表.xlsx");
        fs::write(&source, b"actual bytes").unwrap();
        let file = CompanyFile {
            asset_id: cloud.clone(),
            name: "价格表.xlsx".into(),
            size: 12,
            uploaded_by: id(),
            uploader_name: "管理员".into(),
            uploader_username: "admin".into(),
            created_at: 1,
            deleted_at: None,
            deleted_by: None,
            deleter_username: String::new(),
        };
        let selections = vec![CompanySelection {
            segment: 0,
            block: 0,
            asset_id: cloud.clone(),
            session_epoch: 1,
        }];
        (
            dir,
            store,
            root,
            selections,
            HashMap::from([(cloud, (file, source))]),
        )
    }
    #[test]
    fn private_copy_reuses_owned_file_and_survives_company_cache_removal() {
        let (_dir, mut store, root, selections, downloaded) = setup();
        let mut first = phrase(&root);
        let (batch, mapping) = copies(
            &store,
            &mut first,
            "server",
            "employee",
            &selections,
            &downloaded,
        )
        .unwrap();
        store
            .save_phrase_with_company(first.clone(), None, &mapping)
            .unwrap();
        batch.keep();
        let Block::File { path, .. } = &first.segments[0].blocks[0] else {
            panic!()
        };
        assert!(Path::new(path).ends_with("价格表.xlsx"));
        fs::remove_file(&downloaded.values().next().unwrap().1).unwrap();
        let mut second = phrase(&root);
        let (_batch, mapping) = copies(
            &store,
            &mut second,
            "server",
            "employee",
            &selections,
            &downloaded,
        )
        .unwrap();
        store
            .save_phrase_with_company(second.clone(), None, &mapping)
            .unwrap();
        let Block::File {
            path: second_path, ..
        } = &second.segments[0].blocks[0]
        else {
            panic!()
        };
        assert_eq!(path, second_path);
        assert_eq!(fs::read(path).unwrap(), b"actual bytes");
        assert_eq!(
            store
                .conn
                .query_row("SELECT count(*) FROM company_copies", [], |r| r
                    .get::<_, i64>(0))
                .unwrap(),
            1
        );
    }
    #[test]
    fn failed_private_save_removes_only_new_copy_and_accounts_never_share_it() {
        let (_dir, mut store, root, selections, downloaded) = setup();
        let mut first = phrase(&root);
        let (batch, mapping) = copies(
            &store,
            &mut first,
            "server",
            "one",
            &selections,
            &downloaded,
        )
        .unwrap();
        store
            .save_phrase_with_company(first.clone(), None, &mapping)
            .unwrap();
        batch.keep();
        let Block::File { path: kept, .. } = &first.segments[0].blocks[0] else {
            panic!()
        };
        let mut second = phrase("missing-root");
        let (batch, mapping) = copies(
            &store,
            &mut second,
            "server",
            "two",
            &selections,
            &downloaded,
        )
        .unwrap();
        let Block::File { path: new, .. } = &second.segments[0].blocks[0] else {
            panic!()
        };
        let new = new.clone();
        assert_ne!(*kept, new);
        assert!(store
            .save_phrase_with_company(second, None, &mapping)
            .is_err());
        drop(batch);
        assert!(Path::new(kept).exists());
        assert!(!Path::new(&new).exists());
        assert!(downloaded.values().next().unwrap().1.exists());
        assert_eq!(
            store
                .conn
                .query_row("SELECT count(*) FROM company_copies", [], |r| r
                    .get::<_, i64>(0))
                .unwrap(),
            1
        );
    }
}
