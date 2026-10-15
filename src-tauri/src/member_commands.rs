//! 成员分享入口只接受账号、一级分类和版本；授权范围由来源本人在 Hub 事务中确认。
use crate::{model::Result, team, AppState};
use reqwest::Method;
use shanyu_team_model::member as wire;
use tauri::{Emitter, State};

#[tauri::command]
pub fn get_member_view(state: State<AppState>) -> Result<team::member::View> {
    state.teams.member_view()
}

#[tauri::command]
pub async fn get_member_overview(
    state: State<'_, AppState>,
    session_epoch: u64,
) -> Result<team::member::Overview> {
    state.teams.member_overview(session_epoch).await
}

#[tauri::command]
pub async fn save_member_share(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    request: wire::ShareWrite,
) -> Result<wire::Grant> {
    let grant = state
        .teams
        .member_request(
            session_epoch,
            Method::PUT,
            "me/phrase-sharing",
            Some(serde_json::to_value(request).map_err(|e| e.to_string())?),
        )
        .await?;
    let _ = app.emit("member-content-changed", ());
    Ok(grant)
}

#[tauri::command]
pub async fn set_member_subscription(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    request: wire::Subscribe,
) -> Result<wire::Subscription> {
    state
        .teams
        .member_subscribe(&app, session_epoch, request)
        .await
}

#[tauri::command]
pub async fn download_member_materials(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    ids: Vec<String>,
    attachments: bool,
) -> Result<()> {
    state.teams.prepare_member(&app, ids, attachments).await
}
