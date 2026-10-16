//! 设置增量保存只修改调用方明确提交的字段；设备偏好不进入个人同步。
use crate::{model::*, private_commands, private_local, store::db_error, AppState};
use serde::{Deserialize, Serialize};
use std::sync::atomic::{AtomicU64, Ordering};
use tauri::{Emitter, State};
static REVISION: AtomicU64 = AtomicU64::new(0);
pub fn revision() -> u64 {
    REVISION.load(Ordering::Acquire)
}

/// 可选字段表达“未修改”，禁止未知字段；最终仍执行 Settings 的整体约束。
#[derive(Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SettingsPatch {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub startup: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tray: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub show_floating_on_minimize: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub enter: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub interval: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub exit_search: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub auto_hide: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub hide_delay: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub edge_auto_hide: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub edge_reveal_delay: Option<u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub show_numbers: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub show_preview: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub show_title: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub show_search: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub show_common: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dock_common: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub font_size: Option<u8>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub expand: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dock: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dock_side: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub search_mode: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub search_hotkey: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub window_hotkey: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub common_hotkey: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub custom_hotkeys: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub global_phrase_hotkeys: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sequence_hotkeys: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sequence_direct_send: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sequence_bindings: Option<[String; 10]>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pinned: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub category_height: Option<u16>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub phrase_double_click: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub common_single_click: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dock_search: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub free_resize: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub remember_dock: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub theme: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub title_color: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub phrase_category_color: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub file_category_color: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub files_first: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub teams_first: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub file_category_height: Option<u16>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub common_height: Option<u16>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub dock_common_rows: Option<u8>,
}
#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsSnapshot {
    pub library_id: String,
    pub settings: Settings,
    pub revision: u64,
}
impl SettingsPatch {
    pub fn merge(self, current: &Settings) -> Result<Settings> {
        let mut value = serde_json::to_value(current).map_err(db_error)?;
        for (key, field) in serde_json::to_value(self)
            .map_err(db_error)?
            .as_object()
            .unwrap()
        {
            value[key] = field.clone();
        }
        let next: Settings = serde_json::from_value(value).map_err(db_error)?;
        next.validate()?;
        Ok(next)
    }
}
#[tauri::command]
pub fn patch_settings(
    app: tauri::AppHandle,
    state: State<AppState>,
    patch: SettingsPatch,
    library_id: Option<String>,
) -> Result<SettingsSnapshot> {
    let _activity = crate::update_activity::Activity::enter()?;
    let store = private_commands::active_store(&state, library_id.as_deref())?;
    let device = state.device_store.lock().unwrap();
    let old = private_commands::combined_settings(&store, &device)?;
    let next = patch.merge(&old)?;
    let library_id = private_local::identity(&store)?;
    if next == old {
        return Ok(SettingsSnapshot {
            library_id,
            settings: old,
            revision: revision(),
        });
    }
    let shortcut_changed = next.search_hotkey != old.search_hotkey
        || next.window_hotkey != old.window_hotkey
        || next.common_hotkey != old.common_hotkey
        || next.sequence_bindings != old.sequence_bindings
        || next.sequence_hotkeys != old.sequence_hotkeys;
    if shortcut_changed {
        let mut reserved = next.clone();
        if next.sequence_bindings != old.sequence_bindings {
            reserved.sequence_hotkeys = true;
        }
        crate::shortcut_conflict(&reserved, &store.library()?.phrases)?;
        crate::shortcut_conflict(
            &reserved,
            &state
                .teams
                .member_view()?
                .phrases
                .into_iter()
                .map(|p| p.phrase)
                .collect::<Vec<_>>(),
        )?;
    }
    let old_personal = store.settings()?;
    let old_device = device.settings()?;
    let (personal, local) = if store.dir == device.dir {
        (next.clone(), next.clone())
    } else {
        let incoming = serde_json::to_value(&next).map_err(db_error)?;
        let mut personal = serde_json::to_value(&old_personal).map_err(db_error)?;
        let mut local = serde_json::to_value(&old_device).map_err(db_error)?;
        for (key, value) in incoming.as_object().unwrap() {
            if shanyu_team_model::private::SETTINGS.contains(&key.as_str()) {
                personal[key] = value.clone();
            } else {
                local[key] = value.clone();
            }
        }
        (
            serde_json::from_value(personal).map_err(db_error)?,
            serde_json::from_value(local).map_err(db_error)?,
        )
    };
    state.native.apply_settings(&next)?;
    let saved = (|| {
        if store.dir != device.dir && local != old_device {
            device.save_settings(&local)?;
        }
        if personal != old_personal {
            store.save_settings(&personal)?;
        }
        Ok::<_, String>(())
    })();
    if let Err(error) = saved {
        if store.dir != device.dir && local != old_device {
            let _ = device.save_settings(&old_device);
        }
        let _ = state.native.apply_settings(&old);
        return Err(error);
    }
    // 设置事件在保存锁内发布，所有窗口只替换 settings，不重读话术或重建控件。
    let snapshot = SettingsSnapshot {
        library_id,
        settings: next,
        revision: REVISION.fetch_add(1, Ordering::AcqRel) + 1,
    };
    let _ = app.emit("settings-changed", &snapshot);
    drop(device);
    drop(store);
    state.private_sync.wake();
    let _ = app.emit("private-sync-changed", ());
    Ok(snapshot)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn patch_preserves_unrelated_latest_fields_and_validates_linked_fields() {
        let current = Settings {
            theme: "green".into(),
            interval: 1000,
            ..Default::default()
        };
        let next = SettingsPatch {
            show_search: Some(false),
            ..Default::default()
        }
        .merge(&current)
        .unwrap();
        assert_eq!(next.theme, "green");
        assert_eq!(next.interval, 1000);
        assert!(!next.show_search);
        assert!(SettingsPatch {
            show_title: Some(false),
            show_preview: Some(false),
            ..Default::default()
        }
        .merge(&current)
        .is_err());
        assert!(serde_json::from_str::<SettingsPatch>(r#"{"unknown":true}"#).is_err());
    }
}
