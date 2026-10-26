//! 私人 IPC 不暴露凭据；账号库、文件库和设备设置在此组合成主窗口所需视图。
use crate::{
    model::*,
    private_local as local,
    private_sync::{self, Merge, Preview},
    store::{db_error, Store},
    AppState,
};
use rusqlite::params;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use shanyu_team_model::private as wire;
use std::collections::HashMap;
use tauri::State;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientLibrary {
    pub settings_revision: u64,
    #[serde(flatten)]
    pub library: Library,
    pub library_id: String,
    pub private_revisions: HashMap<String, u64>,
    pub private_ready: HashMap<String, bool>,
    pub owner_label: String,
}
pub fn combined_settings(store: &Store, device: &Store) -> Result<Settings> {
    if store.dir == device.dir {
        return store.settings();
    }
    let mut settings = serde_json::to_value(device.settings()?).map_err(db_error)?;
    let personal = serde_json::to_value(store.settings()?).map_err(db_error)?;
    for key in wire::SETTINGS {
        settings[*key] = personal[*key].clone();
    }
    serde_json::from_value(settings).map_err(db_error)
}
pub fn library(state: &AppState) -> Result<ClientLibrary> {
    let store = state.store.lock().unwrap();
    let mut library = store.library()?;
    let device = state.device_store.lock().unwrap();
    if store.dir != device.dir {
        let files = device.library()?;
        library
            .categories
            .extend(files.categories.into_iter().filter(|c| c.kind == "file"));
        library.files = files.files;
        library.settings = combined_settings(&store, &device)?;
    }
    let ready = library
        .phrases
        .iter()
        .map(|p| (p.id.clone(), local::materials_ready(&store, p)))
        .collect();
    Ok(ClientLibrary {
        settings_revision: crate::settings_commands::revision(),
        library,
        library_id: local::identity(&store)?,
        private_revisions: local::revisions(&store)?,
        private_ready: ready,
        owner_label: local::meta(&store, "username")?,
    })
}
#[tauri::command]
pub fn get_private_status(state: State<AppState>) -> Result<private_sync::Status> {
    state.private_sync.status(&state)
}
#[tauri::command]
pub fn set_private_sync_interval(
    app: tauri::AppHandle,
    state: State<AppState>,
    minutes: u64,
) -> Result<()> {
    private_sync::set_sync_interval(&state.device_store.lock().unwrap(), minutes)?;
    state.private_sync.reschedule();
    use tauri::Emitter;
    let _ = app.emit("private-sync-changed", ());
    Ok(())
}
#[derive(Serialize)]
pub struct SettingChange {
    pub key: String,
    pub current: Value,
    pub next: Value,
}
#[tauri::command]
pub fn get_private_settings(state: State<AppState>) -> Result<Vec<SettingChange>> {
    let store = state.store.lock().unwrap();
    let settings = serde_json::to_value(store.settings()?).map_err(db_error)?;
    let mut changes = vec![];
    for ((kind, key), value) in local::local_records(&store)? {
        if kind == wire::Kind::Setting && key != "commonSingleClick" {
            if let Some(next) = value {
                if settings[&key] != next {
                    changes.push(SettingChange {
                        current: settings[&key].clone(),
                        key,
                        next,
                    });
                }
            }
        }
    }
    Ok(changes)
}
#[tauri::command]
pub async fn preview_private_sync(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> Result<Preview> {
    state.private_sync.preview(&app).await
}
#[tauri::command]
pub fn confirm_private_sync(
    app: tauri::AppHandle,
    state: State<AppState>,
    request: Merge,
) -> Result<()> {
    state.private_sync.confirm(&app, request)
}
#[tauri::command]
pub fn cancel_private_sync_preview(state: State<AppState>) {
    state.private_sync.cancel_preview();
}
#[tauri::command]
pub fn pause_private_sync(app: tauri::AppHandle, state: State<AppState>) -> Result<()> {
    let store = state.store.lock().unwrap();
    local::set_meta(&store, "enabled", "0")?;
    drop(store);
    state.private_sync.wake();
    use tauri::Emitter;
    let _ = app.emit("private-sync-changed", ());
    Ok(())
}
#[tauri::command]
pub fn download_private_materials(
    app: tauri::AppHandle,
    state: State<AppState>,
    ids: Vec<String>,
    attachments: Option<bool>,
) -> Result<()> {
    state
        .private_sync
        .download(app, ids, attachments.unwrap_or(false))
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Conflict {
    pub kind: wire::Kind,
    pub id: String,
    pub label: String,
    pub local: String,
    pub server: String,
    pub server_version: u64,
    pub local_revision: u64,
}
#[tauri::command]
pub fn get_private_conflicts(state: State<AppState>) -> Result<Vec<Conflict>> {
    let store = state.store.lock().unwrap();
    let values = local::local_records(&store)?;
    let base = local::base_records(&store)?;
    let mut result = vec![];
    for ((kind, id), r) in base {
        let flag: bool = store
            .conn
            .query_row(
                "SELECT conflict FROM sync_records WHERE kind=?1 AND id=?2",
                params![kind.as_str(), id],
                |r| r.get(0),
            )
            .map_err(db_error)?;
        if !flag {
            continue;
        }
        let local_value = values.get(&(kind, id.clone())).cloned().flatten();
        result.push(Conflict {
            kind,
            id: id.clone(),
            label: private_sync::record_label(if local_value.is_some() {
                &local_value
            } else {
                &r.value
            }),
            local: private_sync::describe(kind, &local_value),
            server: private_sync::describe(kind, &r.value),
            server_version: r.version,
            local_revision: local::revision(&store, kind.as_str(), &id)?,
        });
    }
    Ok(result)
}
fn assets(store: &Store) -> Result<Vec<wire::Asset>> {
    store
        .conn
        .prepare("SELECT id,kind,name,size FROM sync_assets")
        .map_err(db_error)?
        .query_map([], |r| {
            Ok(wire::Asset {
                id: r.get(0)?,
                kind: r.get(1)?,
                name: r.get(2)?,
                size: r.get::<_, i64>(3)? as u64,
            })
        })
        .map_err(db_error)?
        .collect::<std::result::Result<_, _>>()
        .map_err(db_error)
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolveConflict {
    pub kind: wire::Kind,
    pub id: String,
    pub choice: String,
    pub server_version: u64,
    pub local_revision: u64,
    pub library_id: String,
}
#[tauri::command]
pub fn resolve_private_conflict(
    app: tauri::AppHandle,
    state: State<AppState>,
    request: ResolveConflict,
) -> Result<()> {
    let ResolveConflict {
        kind,
        id,
        choice,
        server_version,
        local_revision,
        library_id,
    } = request;
    let store = state.store.lock().unwrap();
    local::scope(&store, Some(&library_id))?;
    let base = local::base_records(&store)?;
    let r = base
        .get(&(kind, id.clone()))
        .ok_or("冲突已变化，请刷新。")?;
    if r.version != server_version || local::revision(&store, kind.as_str(), &id)? != local_revision
    {
        return Err("版本已变化，请重新核对。".into());
    }
    resolve(&store, kind, &id, &choice, r)?;
    drop(store);
    crate::changed(&app, &state)
}
/// 分类结构的取舍连同关联草稿在同一事务完成，不能先删除父分类再恢复子内容。
fn resolve(
    store: &Store,
    kind: wire::Kind,
    id: &str,
    choice: &str,
    record: &wire::Record,
) -> Result<()> {
    let mut values = local::local_records(store)?;
    let base = local::base_records(store)?;
    let mut remote = std::collections::HashMap::new();
    let assets = assets(store)?;
    for (k, r) in &base {
        remote.insert(k.clone(), private_sync::localize(store, &r.value, &assets)?);
    }
    let k = (kind, id.to_owned());
    let structural = kind == wire::Kind::Category
        && (values.get(&k).and_then(Option::as_ref).is_none()
            || record.value.is_none()
            || values
                .get(&k)
                .and_then(Option::as_ref)
                .map(|v| &v["parentId"])
                != record.value.as_ref().map(|v| &v["parentId"]));
    let mut related = std::collections::HashSet::from([k.clone()]);
    if structural || choice == "both" {
        related.extend(private_sync::related_keys(kind, id, &values));
        related.extend(private_sync::related_keys(kind, id, &remote));
    }
    let mut additional = vec![];
    match choice {
        "local" => {}
        "server" | "both" => {
            if choice == "both" {
                if kind == wire::Kind::Setting {
                    return Err("设置不能保留两份。".into());
                }
                if values.get(&k).and_then(Option::as_ref).is_none() {
                    return Err("本机内容已删除，无法保留两份。".into());
                }
                let mapping: HashMap<_, _> = related
                    .iter()
                    .filter(|k| values.get(*k).and_then(Option::as_ref).is_some())
                    .map(|k| (k.clone(), crate::model::id()))
                    .collect();
                for (key, next) in &mapping {
                    let mut v = values.get(key).cloned().flatten().unwrap();
                    private_sync::remap_record(key.0, &mut v, &mapping);
                    values.insert((key.0, next.clone()), Some(v));
                    additional.push((key.0, next.clone()));
                }
            }
            for key in &related {
                values.insert(key.clone(), remote.get(key).cloned().flatten());
            }
        }
        _ => return Err("请选择有效的冲突处理方式。".into()),
    }
    let tx = store.conn.unchecked_transaction().map_err(db_error)?;
    local::project_in(&tx, store.settings()?, &values, false, false)?;
    for key in related {
        tx.execute(
            "UPDATE sync_records SET conflict=0 WHERE kind=?1 AND id=?2",
            params![key.0.as_str(), key.1],
        )
        .map_err(db_error)?;
        if choice != "local" {
            tx.execute(
                "DELETE FROM sync_dirty WHERE kind=?1 AND id=?2",
                params![key.0.as_str(), key.1],
            )
            .map_err(db_error)?;
        }
        local::bump(&tx, key.0, &key.1, choice == "local")?;
        if key.0 == wire::Kind::Setting {
            if let Some(v) = values.get(&key).and_then(Option::as_ref) {
                tx.execute("INSERT INTO sync_desired_settings VALUES(?1,?2) ON CONFLICT(id) DO UPDATE SET value=excluded.value",params![key.1,v.to_string()]).map_err(db_error)?;
            }
        }
    }
    for (kind, id) in additional {
        local::bump(&tx, kind, &id, true)?;
    }
    tx.commit().map_err(db_error)
}
#[tauri::command]
pub fn apply_private_settings(
    app: tauri::AppHandle,
    state: State<AppState>,
    library_id: String,
) -> Result<()> {
    let store = state.store.lock().unwrap();
    local::scope(&store, Some(&library_id))?;
    let mut settings = serde_json::to_value(store.settings()?).map_err(db_error)?;
    for ((kind, key), value) in local::local_records(&store)? {
        if kind == wire::Kind::Setting && key != "commonSingleClick" {
            if let Some(v) = value {
                settings[&key] = v;
            }
        }
    }
    let settings: Settings = serde_json::from_value(settings).map_err(db_error)?;
    settings.validate()?;
    let runtime = combined_settings(&store, &state.device_store.lock().unwrap())?;
    let mut runtime_value = serde_json::to_value(runtime).map_err(db_error)?;
    let next = serde_json::to_value(&settings).map_err(db_error)?;
    for key in wire::SETTINGS {
        runtime_value[*key] = next[*key].clone();
    }
    let runtime: Settings = serde_json::from_value(runtime_value).map_err(db_error)?;
    state.native.apply_settings(&runtime)?;
    if let Err(e)=store.conn.execute("INSERT INTO settings VALUES(1,?1) ON CONFLICT(id) DO UPDATE SET content=excluded.content",[serde_json::to_string(&settings).map_err(db_error)?]).map_err(db_error){let _=state.native.apply_settings(&store.settings()?);return Err(e);}
    drop(store);
    crate::changed(&app, &state)
}
#[tauri::command]
pub async fn backup_private_server(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    path: String,
) -> Result<()> {
    // ZIP 大小是整个库大小，不复用单素材128MB限制；由服务器流式生成，本地使用临时文件。
    let ctx = state.teams.account_context()?;
    state
        .teams
        .account_backup(&ctx, std::path::Path::new(&path))
        .await?;
    let _ = app;
    Ok(())
}

pub fn active_store<'a>(
    state: &'a AppState,
    library_id: Option<&str>,
) -> Result<std::sync::MutexGuard<'a, Store>> {
    let store = state.store.lock().unwrap();
    local::scope(&store, library_id)?;
    Ok(store)
}
pub fn category_store<'a>(
    state: &'a AppState,
    id: &str,
    library_id: Option<&str>,
) -> Result<std::sync::MutexGuard<'a, Store>> {
    let device = state.device_store.lock().unwrap();
    let file: bool = device
        .conn
        .query_row(
            "SELECT EXISTS(SELECT 1 FROM categories WHERE id=?1 AND kind='file')",
            [id],
            |r| r.get(0),
        )
        .map_err(db_error)?;
    if file {
        Ok(device)
    } else {
        drop(device);
        active_store(state, library_id)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    /// 旧设置仍按原顺序打开；账号库与云端设置不能覆盖本机的个人话术／共享话术入口布局。
    #[test]
    fn scope_order_remains_device_local_when_combining_account_settings() {
        let legacy: Settings = serde_json::from_str(r#"{"filesFirst":true}"#).unwrap();
        assert!(!legacy.teams_first);
        let dir = tempfile::tempdir().unwrap();
        let device = Store::open(&dir.path().join("device")).unwrap();
        let account = Store::open(&dir.path().join("account")).unwrap();
        device
            .save_settings(&Settings {
                teams_first: true,
                ..Settings::default()
            })
            .unwrap();
        account.save_settings(&legacy).unwrap();
        let settings = combined_settings(&account, &device).unwrap();
        assert!(settings.teams_first);
        assert!(settings.files_first);
        let reopened = Store::open(&device.dir).unwrap();
        assert!(combined_settings(&account, &reopened).unwrap().teams_first);
        assert!(combined_settings(&reopened, &reopened).unwrap().teams_first);
    }

    /// 服务端删分类与本机编辑冲突：三种取舍都要整树处理，且不触碰其他分类的待同步草稿。
    #[test]
    fn category_resolution_restores_or_copies_complete_tree_without_losing_unrelated_drafts() {
        for choice in ["local", "server", "both"] {
            let dir = tempfile::tempdir().unwrap();
            let store = Store::open(dir.path()).unwrap();
            let root = crate::model::id();
            let group = crate::model::id();
            let phrase = crate::model::id();
            let common = crate::model::id();
            let values = HashMap::from([
                (
                    (wire::Kind::Category, root.clone()),
                    Some(
                        json!({"id":root,"name":"草稿分类","parentId":null,"position":0,"background":""}),
                    ),
                ),
                (
                    (wire::Kind::Category, group.clone()),
                    Some(
                        json!({"id":group,"name":"子分类","parentId":root,"position":0,"background":""}),
                    ),
                ),
                (
                    (wire::Kind::Phrase, phrase.clone()),
                    Some(
                        json!({"id":phrase,"rootId":root,"groupId":group,"title":"离线草稿","color":"","background":"","shortcut":"","position":0,"segments":[{"blocks":[{"kind":"text","text":"待保留正文"}]}]}),
                    ),
                ),
                (
                    (wire::Kind::Common, common.clone()),
                    Some(json!({"id":common,"text":"其他草稿","position":0})),
                ),
            ]);
            let tx = store.conn.unchecked_transaction().unwrap();
            local::project_in(&tx, store.settings().unwrap(), &values, false, false).unwrap();
            for (kind, id) in values.keys() {
                local::put_base(
                    &tx,
                    &wire::Record {
                        kind: *kind,
                        id: id.clone(),
                        version: 2,
                        value: None,
                    },
                    *kind != wire::Kind::Common,
                )
                .unwrap();
                local::bump(&tx, *kind, id, true).unwrap();
            }
            tx.commit().unwrap();
            let record =
                local::base_records(&store).unwrap()[&(wire::Kind::Category, root.clone())].clone();
            resolve(&store, wire::Kind::Category, &root, choice, &record).unwrap();
            let lib = store.library().unwrap();
            assert_eq!(lib.common_phrases[0].text, "其他草稿");
            assert!(local::dirty(&store)
                .unwrap()
                .contains_key(&(wire::Kind::Common, common)));
            assert!(private_sync::conflict_keys(&store).unwrap().is_empty());
            if choice == "server" {
                assert!(lib.categories.is_empty());
                assert!(lib.phrases.is_empty());
                continue;
            }
            assert_eq!(lib.categories.len(), 2);
            assert_eq!(lib.phrases.len(), 1);
            let p = &lib.phrases[0];
            let root_category = lib
                .categories
                .iter()
                .find(|c| c.parent_id.is_none())
                .unwrap();
            let group_category = lib
                .categories
                .iter()
                .find(|c| c.parent_id.is_some())
                .unwrap();
            assert_eq!(p.root_id, root_category.id);
            assert_eq!(p.group_id.as_deref(), Some(group_category.id.as_str()));
            assert_eq!(
                group_category.parent_id.as_deref(),
                Some(root_category.id.as_str())
            );
            assert_eq!(p.text(), "待保留正文");
            assert_eq!(p.id == phrase, choice == "local");
            assert_eq!(root_category.id == root, choice == "local");
            let dirty = local::dirty(&store).unwrap();
            assert!(dirty.contains_key(&(wire::Kind::Phrase, p.id.clone())));
            assert!(dirty.contains_key(&(wire::Kind::Category, root_category.id.clone())));
        }
    }

    #[test]
    fn accepting_server_restores_new_children_after_local_category_deletion() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        let root = crate::model::id();
        let p = crate::model::id();
        let records = [
            wire::Record {
                kind: wire::Kind::Category,
                id: root.clone(),
                version: 2,
                value: Some(
                    json!({"id":root,"name":"服务器分类","parentId":null,"position":0,"background":""}),
                ),
            },
            wire::Record {
                kind: wire::Kind::Phrase,
                id: p.clone(),
                version: 2,
                value: Some(
                    json!({"id":p,"rootId":root,"groupId":null,"title":"服务器新增","color":"","background":"","shortcut":"","position":0,"segments":[{"blocks":[{"kind":"text","text":"新增正文"}]}]}),
                ),
            },
        ];
        let tx = store.conn.unchecked_transaction().unwrap();
        for r in &records {
            local::put_base(&tx, r, true).unwrap();
            local::bump(&tx, r.kind, &r.id, true).unwrap();
        }
        tx.commit().unwrap();
        resolve(&store, wire::Kind::Category, &root, "server", &records[0]).unwrap();
        assert_eq!(store.categories().unwrap().len(), 1);
        assert_eq!(store.phrase(&p).unwrap().text(), "新增正文");
        assert!(local::dirty(&store).unwrap().is_empty());
        assert!(private_sync::conflict_keys(&store).unwrap().is_empty());
    }
}
