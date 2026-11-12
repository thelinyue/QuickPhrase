//! 同步和目录更新只唤醒一个后台缓存任务；素材共用既有下载锁与两个网络名额，不触发投递。
use crate::{private_local, AppState};
use futures_util::StreamExt;
use tauri::Manager;

enum Job {
    Private(String),
    Team(String),
    Member(String),
    Company(String),
}

pub fn wake(app: &tauri::AppHandle) {
    if let Some(state) = app.try_state::<AppState>() {
        state.material_cache.notify_one();
    }
}

pub fn start(app: tauri::AppHandle) {
    tauri::async_runtime::spawn(async move {
        loop {
            let state = app.state::<AppState>();
            state.material_cache.notified().await;
            let status = state.teams.status();
            if status.offline
                || !status
                    .user
                    .as_ref()
                    .is_some_and(|user| !user.disabled && !user.must_change_password)
            {
                continue;
            }
            let epoch = status.session_epoch;
            let mut jobs = vec![];
            {
                let store = state.store.lock().unwrap();
                if let Ok(library) = store.library() {
                    jobs.extend(library.phrases.iter().filter_map(|phrase| {
                        (!private_local::materials_ready(&store, phrase))
                            .then(|| private_local::content_key(&store, "phrase", &phrase.id).ok())
                            .flatten()
                            .map(Job::Private)
                    }));
                }
            }
            for team in status.teams {
                if let Ok(view) = state.teams.view(&team.team_id) {
                    jobs.extend(
                        view.phrases
                            .into_iter()
                            .filter(|phrase| !phrase.ready)
                            .map(|phrase| Job::Team(phrase.phrase.id)),
                    );
                }
            }
            if let Ok(view) = state.teams.member_view() {
                jobs.extend(
                    view.phrases
                        .into_iter()
                        .filter(|phrase| !phrase.ready)
                        .map(|phrase| Job::Member(phrase.phrase.id)),
                );
            }
            if status.company_files {
                if let Ok(view) = state.teams.company_view(epoch, false, String::new()).await {
                    if !view.offline {
                        jobs.extend(
                            view.files
                                .into_iter()
                                .filter(|entry| !entry.cached && entry.file.deleted_at.is_none())
                                .map(|entry| Job::Company(entry.file.asset_id)),
                        );
                    }
                }
            }
            // 同步期间的新通知合并到下一轮；每个任务开始前再核对账号，旧队列不能跨会话下载。
            futures_util::stream::iter(jobs.into_iter().map(|job| {
                let app = app.clone();
                async move {
                    let state = app.state::<AppState>();
                    let current = state.teams.status();
                    if current.session_epoch != epoch || current.offline {
                        return;
                    }
                    let Ok(_activity) = crate::update_activity::Activity::enter() else {
                        return;
                    };
                    let result = match job {
                        Job::Private(key) => state
                            .private_sync
                            .prepare_delivery(&app, &key)
                            .await
                            .map(|_| ()),
                        Job::Team(key) => state.teams.prepare(&app, &key).await,
                        Job::Member(key) => state.teams.prepare_member(&app, vec![key], true).await,
                        Job::Company(id) => state
                            .teams
                            .company_download(&app, epoch, &id)
                            .await
                            .map(|_| ()),
                    };
                    if let Err(error) = result {
                        log::warn!("后台缓存未完成，后续同步或使用时可重新下载：{error}");
                    }
                }
            }))
            .buffer_unordered(2)
            .collect::<Vec<_>>()
            .await;
        }
    });
}
