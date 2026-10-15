//! 共享话术 IPC 接口。这里只负责本地类型转换，权限最终由服务端逐次决定。
use crate::{model::*, team, AppState};
use reqwest::Method;
use shanyu_team_model as wire;
use tauri::{Manager, State};

#[tauri::command]
pub fn get_team_status(state: State<AppState>) -> team::Status {
    state.teams.status()
}
#[tauri::command]
pub fn get_team_view(state: State<AppState>, team_id: String) -> Result<team::View> {
    state.teams.view(&team_id)
}
#[tauri::command]
pub async fn team_login(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    server: String,
    username: String,
    password: String,
) -> Result<team::Status> {
    let result = state.teams.login(&app, server, username, password).await?;
    state.private_sync.account_changed(&app)?;
    Ok(result)
}
#[tauri::command]
pub async fn team_logout(app: tauri::AppHandle, state: State<'_, AppState>) -> Result<()> {
    state.teams.logout(&app).await
}
#[tauri::command]
pub async fn team_refresh(app: tauri::AppHandle, state: State<'_, AppState>) -> Result<()> {
    state.teams.refresh_authorization(&app).await
}
#[tauri::command]
pub async fn team_change_password(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    request: wire::PasswordChange,
) -> Result<()> {
    state.teams.change_password(&app, request).await
}
#[tauri::command]
pub async fn save_team_phrase(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    draft: team::Draft,
) -> Result<()> {
    state.teams.save_phrase(&app, draft).await
}
#[tauri::command]
pub async fn save_team_category(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    team_id: String,
    expected_version: u64,
    expected_lifecycle_version: u64,
    category: Category,
) -> Result<()> {
    state
        .teams
        .save_category(
            &app,
            &team_id,
            expected_version,
            expected_lifecycle_version,
            category,
        )
        .await
}
#[tauri::command]
pub async fn delete_team_content(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    team_id: String,
    request: wire::DeleteRequest,
) -> Result<()> {
    state.teams.delete(&app, &team_id, request).await
}
#[tauri::command]
pub async fn get_team_recycle(
    state: State<'_, AppState>,
    team_id: String,
    query: Option<String>,
    session_epoch: Option<u64>,
    expected_lifecycle_version: Option<u64>,
) -> Result<Vec<wire::RecycleRecord>> {
    let mut rows = state
        .teams
        .recycle(&team_id, session_epoch, expected_lifecycle_version)
        .await?;
    rows.retain(|r| {
        crate::recycle::matches_query(
            &format!(
                "{} {} {}",
                r.label,
                r.categories
                    .iter()
                    .map(|c| c.name.as_str())
                    .collect::<Vec<_>>()
                    .join(" "),
                r.phrases
                    .iter()
                    .map(|p| format!(
                        "{} {}",
                        p.title,
                        p.segments
                            .iter()
                            .flat_map(|s| &s.blocks)
                            .map(|b| match b {
                                wire::Block::Text { text } => text.as_str(),
                                wire::Block::Image { name, .. }
                                | wire::Block::File { name, .. } => name.as_str(),
                            })
                            .collect::<Vec<_>>()
                            .join(" ")
                    ))
                    .collect::<Vec<_>>()
                    .join(" ")
            ),
            query.as_deref().unwrap_or(""),
        )
    });
    rows.sort_by_key(|r| std::cmp::Reverse(r.deleted_at));
    Ok(rows)
}
#[tauri::command]
pub async fn restore_team_record(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    team_id: String,
    id: String,
    expected_version: u64,
    expected_lifecycle_version: u64,
    session_epoch: Option<u64>,
) -> Result<()> {
    state
        .teams
        .restore(
            &app,
            &team_id,
            &id,
            expected_version,
            expected_lifecycle_version,
            session_epoch,
        )
        .await
}
#[tauri::command]
pub async fn purge_team_record(
    state: State<'_, AppState>,
    team_id: String,
    id: String,
    expected_lifecycle_version: u64,
    session_epoch: Option<u64>,
) -> Result<()> {
    state
        .teams
        .purge(&team_id, &id, expected_lifecycle_version, session_epoch)
        .await
}
#[tauri::command]
pub async fn empty_team_recycle(
    state: State<'_, AppState>,
    team_id: String,
    ids: Vec<String>,
    expected_lifecycle_version: u64,
    session_epoch: Option<u64>,
) -> Result<usize> {
    state
        .teams
        .empty_recycle(
            &team_id,
            wire::ClearRecycleRequest {
                ids,
                expected_lifecycle_version,
            },
            session_epoch,
        )
        .await
}
#[tauri::command]
pub async fn prepare_team_phrase(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    id: String,
) -> Result<()> {
    state.teams.prepare(&app, &id).await
}
#[tauri::command]
pub fn preload_team_images(
    app: tauri::AppHandle,
    state: State<AppState>,
    team_id: String,
    ids: Vec<String>,
) {
    state.teams.preload(app, team_id, ids);
}
#[tauri::command]
pub async fn copy_team_to_private(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    id: String,
) -> Result<()> {
    state.teams.copy_to_private(&app, &id).await
}
#[tauri::command]
pub async fn publish_private_to_team(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    team_id: String,
    id: String,
    kind: String,
) -> Result<()> {
    let mut library = state.store.lock().unwrap().library()?;
    library.phrases.retain(|p| {
        if kind == "phrase" {
            p.id == id
        } else {
            p.root_id == id
        }
    });
    if kind != "phrase" && kind != "category" {
        return Err("复制类型无效。".into());
    }
    library.categories.retain(|c| {
        if kind == "category" {
            c.id == id || c.parent_id.as_ref() == Some(&id)
        } else {
            library
                .phrases
                .iter()
                .any(|p| p.root_id == c.id || p.group_id.as_ref() == Some(&c.id))
        }
    });
    if library.categories.is_empty() {
        return Err("来源已不存在。".into());
    }
    library.files.clear();
    library.common_phrases.clear();
    state.teams.copy_from_private(&app, &team_id, library).await
}
#[tauri::command]
pub async fn team_admin(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    action: String,
    id: Option<String>,
    team_id: Option<String>,
    request: Option<serde_json::Value>,
    session_epoch: Option<u64>,
) -> Result<serde_json::Value> {
    let id = id.unwrap_or_default();
    let team = team_id.unwrap_or_default();
    for value in [&id, &team] {
        if !value.is_empty() && uuid::Uuid::parse_str(value).is_err() {
            return Err("账号或共享话术ID无效。".into());
        }
    }
    let (method, path) = match action.as_str() {
        "users" => (Method::GET, "users".into()),
        "create-user" => (Method::POST, "users".into()),
        "update-user" => (Method::PUT, format!("users/{id}")),
        "create-team" => (Method::POST, "teams".into()),
        "update-team" => (Method::PUT, format!("teams/{team}")),
        "members" => (Method::GET, format!("teams/{team}/members")),
        "set-members" => (Method::PUT, format!("teams/{team}/members")),
        "lookup-member" => {
            let username = request
                .as_ref()
                .and_then(|r| r.get("username"))
                .and_then(|v| v.as_str())
                .ok_or("请填写完整用户名。")?;
            let mut url = reqwest::Url::parse("http://localhost/").unwrap();
            url.query_pairs_mut().append_pair("username", username);
            (
                Method::GET,
                format!("teams/{team}/member-lookup?{}", url.query().unwrap()),
            )
        }
        "transfer-manager" => (Method::POST, format!("teams/{team}/transfer-manager")),
        "leave-team" => (Method::POST, format!("teams/{team}/leave")),
        "delete-team" => (Method::POST, format!("teams/{team}/delete")),
        "deleted-teams" => (Method::GET, "deleted-teams".into()),
        "restore-team" => (Method::POST, format!("deleted-teams/{team}/restore")),
        "purge-team" => (Method::POST, format!("deleted-teams/{team}/purge")),
        "asset-cleanup" => (Method::GET, "asset-cleanup".into()),
        _ => return Err("共享话术管理操作无效。".into()),
    };
    let mutating = method != Method::GET;
    let (result, epoch) = state
        .teams
        .admin_request(method, &path, request, session_epoch)
        .await?;
    if action == "leave-team" || action == "delete-team" {
        state
            .teams
            .forget_team(&app, &team, epoch)
            .await
            .map_err(|e| format!("操作已提交，共享话术授权已撤下；本地缓存处理暂未完成：{e}"))?;
    }
    if mutating {
        // 操作已由服务端提交；授权刷新失败不应把成功的移交误报为失败、诱使用户重试。
        let _ = state.teams.refresh_authorization(&app).await;
    }
    Ok(result)
}
#[tauri::command]
pub async fn backup_team_server(state: State<'_, AppState>, path: String) -> Result<()> {
    state.teams.backup(std::path::Path::new(&path)).await
}

/// 主搜索限定当前来源；底部 dock 搜索才合并全部授权共享话术和本地文件。
#[tauri::command]
pub fn search_scoped_content(
    state: State<AppState>,
    query: String,
    scope: String,
    team_id: Option<String>,
) -> Result<Vec<serde_json::Value>> {
    let store = state.store.lock().unwrap();
    let exact = store.settings()?.search_mode == "exact";
    let mut hits = if scope == "team" {
        vec![]
    } else {
        store.search(&query)?.into_iter().filter(|h|h.kind=="phrase"&&(scope=="all"||scope=="private")).map(|h|{let content_key=crate::private_local::content_key(&store,"phrase",&h.id).ok();serde_json::json!({"id":h.id,"kind":h.kind,"phrase":h.phrase,"file":h.file,"privateKey":content_key,"sourceLabel":"个人话术"})}).collect::<Vec<_>>()
    };
    drop(store);
    if scope == "all" || scope == "private" {
        hits.extend(state.teams.search_member(&query, exact));
    }
    if scope == "all" || scope == "file" {
        hits.extend(state.device_store.lock().unwrap().search(&query)?.into_iter().filter(|h|h.kind=="file").map(|h|serde_json::json!({"id":h.id,"kind":h.kind,"phrase":h.phrase,"file":h.file,"sourceLabel":"本地文件"})));
    }
    if scope == "all" || scope == "team" {
        hits.extend(state.teams.search(
            &query,
            exact,
            if scope == "team" {
                Some(team_id.as_deref().ok_or("请选择共享话术。")?)
            } else {
                None
            },
        ));
    }
    Ok(hits)
}

pub fn start(app: &tauri::AppHandle) {
    app.state::<AppState>().teams.start(app.clone());
}
