//! 只提供命令目录、管理及纯文本复制 IPC，不关联发送目标，也不调用脚本或终端。
use crate::{model::Result, AppState};
use shanyu_team_model::cloud_commands::*;
use tauri::{Manager, State};

#[tauri::command]
pub async fn get_cloud_commands(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    query: CloudCommandQuery,
) -> Result<CloudCommands> {
    state.teams.cloud_commands(&app, session_epoch, query).await
}
#[tauri::command]
pub async fn get_cloud_command(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    id: String,
) -> Result<CloudCommand> {
    state
        .teams
        .cloud_command(&app, session_epoch, &id, None)
        .await
}
#[tauri::command]
pub async fn save_cloud_command(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    id: Option<String>,
    request: CloudCommandWrite,
) -> Result<CloudCommand> {
    state
        .teams
        .save_cloud_command(&app, session_epoch, id, request)
        .await
}
#[tauri::command]
pub async fn manage_cloud_command(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    id: String,
    operation: String,
    request: CloudCommandRevision,
) -> Result<()> {
    state
        .teams
        .manage_cloud_command(&app, session_epoch, &id, &operation, request)
        .await
}
#[tauri::command]
pub async fn copy_cloud_command(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    session_epoch: u64,
    id: String,
    expected_revision: u64,
) -> Result<()> {
    let version = state
        .teams
        .status()
        .user
        .ok_or("请先登录闪云。")?
        .cloud_command_permissions
        .version;
    let command = state
        .teams
        .cloud_command(&app, session_epoch, &id, Some(expected_revision))
        .await?;
    let owner = app
        .get_webview_window("company-files")
        .ok_or("闪云中心已关闭。")?
        .hwnd()
        .map_err(|e| e.to_string())?
        .0 as i64;
    // 最后一次会话核对与写剪贴板共用账号锁，避免切号落在两者之间。
    state.teams.with_cloud_commands(session_epoch, version, || {
        crate::native::copy_text_exact(owner, &command.body)
    })
}
