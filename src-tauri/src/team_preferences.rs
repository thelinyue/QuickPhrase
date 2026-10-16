//! 默认库是当前账号在本机的导航偏好，不修改共享库，也不进入个人同步。
use crate::{model::Result, private_local, store::db_error, AppState};
use tauri::{Emitter, State};
fn key(server: &str, user_id: &str) -> String {
    format!("defaultTeam:{}", serde_json::json!([server, user_id]))
}
#[tauri::command]
pub fn get_default_team(state: State<AppState>) -> Result<String> {
    let status = state.teams.status();
    let Some(user) = status.user else {
        return Ok(String::new());
    };
    private_local::meta(
        &state.device_store.lock().unwrap(),
        &key(&status.server, &user.id),
    )
}
#[tauri::command]
pub fn set_default_team(
    app: tauri::AppHandle,
    state: State<AppState>,
    server: String,
    user_id: String,
    team_id: String,
) -> Result<()> {
    let status = state.teams.status();
    if status.server != server || status.user.as_ref().is_none_or(|u| u.id != user_id) {
        return Err("账号已变化，请重新选择默认共享话术。".into());
    }
    if !team_id.is_empty() && !status.teams.iter().any(|t| t.team_id == team_id) {
        return Err("该共享话术已不可访问。".into());
    }
    let store = state.device_store.lock().unwrap();
    let tx = store.conn.unchecked_transaction().map_err(db_error)?;
    tx.execute(
        "INSERT INTO sync_meta VALUES(?1,?2) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
        [key(&server, &user_id), team_id],
    )
    .map_err(db_error)?;
    tx.commit().map_err(db_error)?;
    let _ = app.emit("default-team-changed", ());
    Ok(())
}
