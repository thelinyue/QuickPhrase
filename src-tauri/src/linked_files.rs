//! 文件关联统一保存在设备库；云端引用逐次核验账号，不把缓存路径当作永久关联。
use crate::{model::*, store::db_error, team::CompanySelection, AppState};
use rusqlite::params;
use tauri::State;

pub fn visible(file: &LinkedFile, status: &crate::team::Status) -> bool {
    file.cloud.as_ref().is_none_or(|cloud| {
        cloud.server == status.server
            && status
                .user
                .as_ref()
                .is_some_and(|user| user.id == cloud.user_id)
    })
}

pub fn check_visible(state: &AppState, ids: &[String]) -> Result<()> {
    let status = state.teams.status();
    let files = state.device_store.lock().unwrap().linked_files(ids)?;
    if files.iter().any(|file| !visible(file, &status)) {
        return Err("不能操作其他账号的云文件关联。".into());
    }
    Ok(())
}

pub fn check_category_removal(state: &AppState, category: &str) -> Result<()> {
    let status = state.teams.status();
    let files = state.device_store.lock().unwrap().library()?.files;
    if files
        .iter()
        .any(|file| file.category_id == category && !visible(file, &status))
    {
        return Err("该分类包含其他账号的云文件关联，请先在对应账号中移除或移动关联。".into());
    }
    Ok(())
}

pub async fn view(state: &AppState) -> Result<Vec<LinkedFile>> {
    let status = state.teams.status();
    let mut files = state.device_store.lock().unwrap().library()?.files;
    files.retain(|file| visible(file, &status));
    if !files.iter().any(|f| f.cloud.is_some()) {
        return Ok(files);
    }
    let catalog = state
        .teams
        .company_view(status.session_epoch, false, String::new())
        .await;
    let mut current = state.teams.status();
    if current.session_epoch != status.session_epoch {
        return Err("账号已变化，旧文件列表已丢弃。".into());
    }
    current.offline |= catalog.as_ref().is_ok_and(|view| view.offline);
    decorate(
        &mut files,
        &current,
        catalog
            .as_ref()
            .ok()
            .map(|catalog| catalog.files.as_slice()),
    );
    retain_cached_files(state, &mut files, &current);
    Ok(files)
}

pub fn cached_view(state: &AppState) -> Result<Vec<LinkedFile>> {
    let status = state.teams.status();
    let mut files = state.device_store.lock().unwrap().library()?.files;
    files.retain(|file| visible(file, &status));
    let catalog = state.teams.cached_company_files(status.session_epoch);
    decorate(&mut files, &status, catalog.as_deref());
    retain_cached_files(state, &mut files, &status);
    Ok(files)
}

/// 已移出目录的文件仍可使用当前账号的完整缓存；缓存不作为可备份、可迁移的源路径。
fn retain_cached_files(state: &AppState, files: &mut [LinkedFile], status: &crate::team::Status) {
    for file in files.iter_mut().filter(|file| file.missing) {
        if let Some(cloud) = &file.cloud {
            if state
                .teams
                .linked_company_existing_cache(status.session_epoch, &cloud.asset_id, &file.name)
                .ok()
                .flatten()
                .is_some()
            {
                file.missing = false;
                file.status = if status.offline {
                    "Hub已断开 · 本机可用".into()
                } else {
                    "仅本机缓存".into()
                };
            }
        }
    }
}

fn decorate(
    files: &mut [LinkedFile],
    status: &crate::team::Status,
    catalog: Option<&[crate::team::CompanyEntry]>,
) {
    for file in files {
        if let Some(cloud) = &file.cloud {
            file.path.clear();
            file.missing = true;
            file.status = if status.offline {
                "Hub已断开 · 未缓存"
            } else {
                "不可用"
            }
            .into();
            if let Some(catalog) = catalog {
                if let Some(entry) = catalog.iter().find(|f| f.file.asset_id == cloud.asset_id) {
                    file.source_name = entry.file.name.clone();
                    if !file.custom_name {
                        file.name = entry.file.name.clone();
                    }
                    // 目录快照只提供名称，是否可用由本机完整缓存决定，不借此开放云端操作。
                    if status.offline {
                        file.missing = !entry.cached;
                        file.can_rename = false;
                        if entry.cached {
                            file.status = "Hub已断开 · 本机可用".into();
                        }
                        continue;
                    }
                    file.missing = false;
                    file.can_rename = status.user.as_ref().is_some_and(|u| {
                        u.admin
                            || (u.id == entry.file.uploaded_by
                                && u.company_file_permissions.can_upload)
                    });
                    file.status = if entry.cached {
                        "已下载"
                    } else {
                        "未下载"
                    }
                    .into();
                }
            }
        }
    }
}

/// 所有文件使用入口共用解析。下载只准备素材，本次动作结束，用户再次触发才使用剪贴板或发送。
pub async fn resolve(
    app: &tauri::AppHandle,
    state: &AppState,
    ids: &[String],
) -> Result<Vec<LinkedFile>> {
    let mut files = state.device_store.lock().unwrap().linked_files(ids)?;
    let status = state.teams.status();
    let mut downloaded = false;
    for file in &mut files {
        if !visible(file, &status) {
            return Err("云文件属于其他账号，请重新选择。".into());
        }
        if let Some(cloud) = &file.cloud {
            if let Some(path) = state.teams.linked_company_existing_cache(
                status.session_epoch,
                &cloud.asset_id,
                &file.name,
            )? {
                file.path = external_path(&path);
                continue;
            }
            let metadata = state
                .teams
                .company_selection(&CompanySelection {
                    segment: 0,
                    block: 0,
                    asset_id: cloud.asset_id.clone(),
                    session_epoch: status.session_epoch,
                })
                .await?;
            let path = match state
                .teams
                .linked_company_cached(status.session_epoch, &metadata)?
            {
                Some(path) => path,
                None => {
                    downloaded = true;
                    state
                        .teams
                        .company_download(app, status.session_epoch, &cloud.asset_id)
                        .await?
                        .1
                }
            };
            file.path = external_path(&path);
            file.name = metadata.name;
        } else if !std::path::Path::new(&file.path).is_file() {
            return Err("本地文件已失效，请重新关联。".into());
        }
    }
    if state.teams.status().session_epoch != status.session_epoch {
        return Err("账号已变化，请重新触发。".into());
    }
    // 加载期间关联可能被另一个窗口替换；不能使用已不属于屏幕记录的旧文件。
    let latest = state.device_store.lock().unwrap().linked_files(ids)?;
    for file in &files {
        if !latest.iter().any(|f| {
            f.id == file.id && f.cloud == file.cloud && (f.cloud.is_some() || f.path == file.path)
        }) {
            return Err("文件关联已变化，请重新触发。".into());
        }
    }
    if downloaded {
        return Err("文件已下载完成，请重新触发操作；不会自动发送或复制。".into());
    }
    Ok(files)
}

#[tauri::command]
pub async fn add_cloud_files(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    category_id: String,
    asset_ids: Vec<String>,
    replace_id: Option<String>,
) -> Result<()> {
    let _activity = crate::update_activity::Activity::enter()?;
    if asset_ids.is_empty() || (replace_id.is_some() && asset_ids.len() != 1) {
        return Err("请选择有效的云文件。".into());
    }
    let ctx = state
        .teams
        .company_context(session_epoch, shanyu_team_model::CompanyOperation::Read)?;
    let mut files = vec![];
    for asset_id in asset_ids {
        if files
            .iter()
            .any(|f: &shanyu_team_model::CompanyFile| f.asset_id == asset_id)
        {
            return Err("不能重复关联同一云文件。".into());
        }
        files.push(
            state
                .teams
                .company_selection(&CompanySelection {
                    segment: 0,
                    block: 0,
                    asset_id,
                    session_epoch,
                })
                .await?,
        );
    }
    state.teams.with_account(&ctx, || {
        let mut store = state.device_store.lock().unwrap();
        if let Some(id) = &replace_id {
            let previous = store.linked_files(std::slice::from_ref(id))?;
            if previous[0].cloud.as_ref().is_some_and(|c| c.server != ctx.server || c.user_id != ctx.user_id) { return Err("不能更改其他账号的文件关联。".into()); }
        }
        let tx = store.conn.transaction().map_err(db_error)?;
        let valid: bool = tx.query_row("SELECT EXISTS(SELECT 1 FROM categories WHERE id=?1 AND kind='file' AND parent_id IS NULL)", [&category_id], |r| r.get(0)).map_err(db_error)?;
        if !valid { return Err("文件分类已不存在，请重新选择。".into()); }
        for file in files {
            let cloud = serde_json::to_string(&CloudFileReference { server: ctx.server.clone(), user_id: ctx.user_id.clone(), asset_id: file.asset_id }).map_err(db_error)?;
            if let Some(id) = &replace_id {
                tx.execute("UPDATE files SET category_id=?2,path='',name=?3,cloud=?4,custom_name=0 WHERE id=?1", params![id,category_id,file.name,cloud]).map_err(db_error)?;
            } else {
                tx.execute("INSERT INTO files(id,category_id,path,name,position,cloud) VALUES(?1,?2,'',?3,(SELECT coalesce(max(position),-1)+1 FROM files WHERE category_id=?2),?4)", params![id(),category_id,file.name,cloud]).map_err(db_error)?;
            }
        }
        tx.commit().map_err(db_error)
    })?;
    crate::changed(&app, &state)
}

#[tauri::command]
pub async fn rename_company_file(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    id: String,
    expected_name: String,
    name: String,
) -> Result<()> {
    let _activity = crate::update_activity::Activity::enter()?;
    state
        .teams
        .rename_company(&app, session_epoch, &id, &expected_name, &name)
        .await
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn offline_linked_rows_keep_names_and_only_complete_cache_is_usable() {
        let user: shanyu_team_model::User = serde_json::from_value(serde_json::json!({
            "id":"user", "username":"employee", "admin":false, "disabled":false,
            "mustChangePassword":false
        }))
        .unwrap();
        let status = crate::team::Status {
            session_epoch: 1,
            server: "server".into(),
            username: "employee".into(),
            user: Some(user),
            teams: vec![],
            team_sync_times: Default::default(),
            offline: true,
            private_sync: false,
            member_phrase_sync: false,
            company_files: true,
            company_file_permissions: false,
        };
        let file: LinkedFile = serde_json::from_value(serde_json::json!({
            "id":"linked", "categoryId":"category", "path":"", "name":"我的显示名.pdf", "position":0,
            "customName":true, "cloud":{"server":"server","userId":"user","assetId":"asset"}
        })).unwrap();
        let metadata: shanyu_team_model::CompanyFile = serde_json::from_value(serde_json::json!({
            "assetId":"asset", "name":"云端名称.pdf", "size":4, "uploadedBy":"user",
            "uploaderName":"employee", "createdAt":1, "deletedAt":null
        }))
        .unwrap();
        for cached in [true, false] {
            let mut files = vec![file.clone()];
            decorate(
                &mut files,
                &status,
                Some(&[crate::team::CompanyEntry {
                    file: metadata.clone(),
                    cached,
                }]),
            );
            assert_eq!(files[0].missing, !cached);
            assert_eq!(files[0].name, "我的显示名.pdf");
            assert_eq!(files[0].source_name, "云端名称.pdf");
            assert!(!files[0].can_rename);
            assert!(files[0]
                .status
                .contains(if cached { "本机可用" } else { "未缓存" }));
            assert!(visible(&files[0], &status));
        }
        let mut other = status;
        other.server = "other".into();
        assert!(!visible(&file, &other));
    }
}
