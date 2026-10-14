//! 手动启动意图与本机入门状态独立于账号库；开机自启不能主动抢焦点。
use crate::{model::Result, private_local, store::db_error, store::Store, AppState};
use serde::Serialize;
use std::sync::{Arc, Mutex};
use tauri::{Manager, State};

/// 页面与系统服务可能以不同顺序就绪。只合并未完成的唤起请求，不计数或重复创建窗口。
pub struct LaunchIntent(Mutex<LaunchState>);
#[derive(Default)]
struct LaunchState {
    page_ready: bool,
    services_ready: bool,
    pending_show: bool,
    shown: bool,
}
impl LaunchIntent {
    pub fn new(manual: bool) -> Self {
        Self(Mutex::new(LaunchState {
            pending_show: manual,
            ..Default::default()
        }))
    }
    pub fn page_ready(&self) {
        self.0.lock().unwrap().page_ready = true;
    }
    pub fn services_ready(&self) {
        self.0.lock().unwrap().services_ready = true;
    }
    pub fn request_show(&self) {
        self.0.lock().unwrap().pending_show = true;
    }
    fn take_show(&self) -> bool {
        let mut state = self.0.lock().unwrap();
        if !state.page_ready || !state.services_ready {
            return false;
        }
        std::mem::take(&mut state.pending_show)
    }
    pub fn mark_shown(&self) {
        self.0.lock().unwrap().shown = true;
    }
    fn shown(&self) -> bool {
        self.0.lock().unwrap().shown
    }
}

pub fn dispatch(app: &tauri::AppHandle, intent: &LaunchIntent) {
    if intent.take_show() {
        app.state::<AppState>().native.show("launch");
    }
}

/// 既有数据库升级时默认已使用；空分类不能作为新用户判断，删除全部内容也不重置指引。
pub fn initialize_guide(store: &Store, fresh: bool) -> Result<()> {
    let tx = store.conn.unchecked_transaction().map_err(db_error)?;
    tx.execute(
        "INSERT OR IGNORE INTO sync_meta VALUES('usageGuideShown',?1)",
        [if fresh { "0" } else { "1" }],
    )
    .map_err(db_error)?;
    tx.commit().map_err(db_error)
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GuideState {
    pending: bool,
    auto_open: bool,
}

#[tauri::command]
pub fn get_usage_guide_state(
    state: State<AppState>,
    intent: State<Arc<LaunchIntent>>,
) -> Result<GuideState> {
    let pending =
        private_local::meta(&state.device_store.lock().unwrap(), "usageGuideShown")? != "1";
    Ok(GuideState {
        pending,
        auto_open: pending && intent.shown(),
    })
}

#[tauri::command]
pub fn mark_usage_guide_shown(state: State<AppState>) -> Result<()> {
    let device = state.device_store.lock().unwrap();
    let tx = device.conn.unchecked_transaction().map_err(db_error)?;
    tx.execute("INSERT INTO sync_meta VALUES('usageGuideShown','1') ON CONFLICT(key) DO UPDATE SET value='1'", []).map_err(db_error)?;
    tx.commit().map_err(db_error)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn manual_launch_waits_for_both_services_and_page_and_coalesces_requests() {
        let intent = LaunchIntent::new(true);
        intent.request_show();
        intent.page_ready();
        assert!(!intent.take_show());
        intent.services_ready();
        assert!(intent.take_show());
        assert!(!intent.take_show());
        intent.request_show();
        assert!(intent.take_show());
        let background = LaunchIntent::new(false);
        background.services_ready();
        background.page_ready();
        assert!(!background.take_show());
        background.request_show();
        assert!(background.take_show());
    }
    #[test]
    fn guide_is_device_metadata_and_legacy_upgrade_does_not_reset_it() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::open(dir.path()).unwrap();
        initialize_guide(&store, true).unwrap();
        assert_eq!(private_local::meta(&store, "usageGuideShown").unwrap(), "0");
        initialize_guide(&store, false).unwrap();
        assert_eq!(private_local::meta(&store, "usageGuideShown").unwrap(), "0");
        store
            .conn
            .execute("DELETE FROM sync_meta WHERE key='usageGuideShown'", [])
            .unwrap();
        initialize_guide(&store, false).unwrap();
        assert_eq!(private_local::meta(&store, "usageGuideShown").unwrap(), "1");
        assert!(store.categories().unwrap().is_empty());
    }
}
