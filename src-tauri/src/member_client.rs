//! 成员话术只在展示时与本人分类组合，缓存、授权和素材始终按账号与来源隔离。
//! 分类整类只读，不建立逐条话术权限；内容引用只用于拒绝切号、撤权和旧版本投递。
use super::*;
use shanyu_team_model::member as wire;
use shanyu_team_model::{private::Asset, Block as SharedBlock, Phrase as SharedPhrase};
use std::collections::{BTreeMap, HashSet};

#[derive(Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Cached {
    follow: wire::Follow,
    last_successful_sync_at: Option<u64>,
}
/// 账号分区内保存目录与本人使用名单，计数由同一名单计算；退出登录统一删除此目录。
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Overview {
    user_id: String,
    #[serde(flatten)]
    value: wire::Overview,
    offline: bool,
}
#[derive(Default)]
pub(super) struct MemberState {
    epoch: u64,
    worker_epoch: u64,
    worker_cancel: CancellationToken,
    cached: Cached,
    shortcuts: BTreeMap<String, String>,
    cancel: CancellationToken,
    message: String,
    refreshing: bool,
    pub(super) delivery: Option<wire::ContentRef>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientCategory {
    #[serde(flatten)]
    pub category: local::Category,
    read_only: bool,
    source_label: String,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientPhrase {
    #[serde(flatten)]
    pub phrase: local::Phrase,
    content_ref: wire::ContentRef,
    ready: bool,
}
#[derive(Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct View {
    pub session_epoch: u64,
    pub categories: Vec<ClientCategory>,
    pub phrases: Vec<ClientPhrase>,
    subscription: wire::Subscription,
    owner_label: String,
    state: String,
    message: String,
    last_successful_sync_at: Option<u64>,
}
fn owner_label(snapshot: &wire::Snapshot) -> String {
    let owner = &snapshot.grant.owner;
    if owner.display_name.is_empty() {
        owner.username.clone()
    } else {
        format!("{}（{}）", owner.display_name, owner.username)
    }
}
fn reference(follow: &wire::Follow, phrase: &SharedPhrase) -> wire::ContentRef {
    let snapshot = follow.snapshot.as_ref().unwrap();
    wire::ContentRef {
        source: "member".into(),
        grant_id: snapshot.grant.id.clone(),
        grant_version: snapshot.grant.version,
        subscription_version: follow.subscription.version,
        generation: snapshot.generation.clone(),
        id: phrase.id.clone(),
        revision: phrase.revision,
    }
}
/// 快捷键属于使用者的本机偏好，不写回分享快照。正文修订保留绑定，来源库重建则隔离。
/// 文件独立于可删除的登录缓存；服务地址和账号参与键，退出后重新登录仍能恢复自己的绑定。
fn shortcut_key(active: &Active, reference: &wire::ContentRef) -> String {
    serde_json::to_string(&(
        &active.connection.server,
        &active.authorization.user.id,
        &reference.grant_id,
        &reference.generation,
        &reference.id,
    ))
    .expect("字符串元组可序列化")
}
fn find_phrase<'a>(
    follow: &'a wire::Follow,
    reference: &wire::ContentRef,
) -> Option<&'a SharedPhrase> {
    let snapshot = follow.snapshot.as_ref()?;
    if follow.subscription.grant_id.as_deref() != Some(&reference.grant_id)
        || follow.subscription.version != reference.subscription_version
        || snapshot.grant.id != reference.grant_id
        || snapshot.grant.version != reference.grant_version
        || snapshot.generation != reference.generation
    {
        return None;
    }
    snapshot.phrases.iter().find(|p| {
        p.id == reference.id
            && p.revision == reference.revision
            && snapshot.grant.root_ids.contains(&p.root_id)
    })
}
fn same_content(left: &wire::Follow, right: &wire::Follow) -> bool {
    left.subscription == right.subscription
        && match (&left.snapshot, &right.snapshot) {
            (Some(a), Some(b)) => {
                a.grant.id == b.grant.id
                    && a.grant.version == b.grant.version
                    && a.generation == b.generation
                    && a.version == b.version
            }
            (None, None) => true,
            _ => false,
        }
}
fn valid_follow(follow: &wire::Follow, user: &str) -> bool {
    if follow.recipient_id != user {
        return false;
    }
    let Some(snapshot) = &follow.snapshot else {
        return true;
    };
    let uuid = |id: &str| uuid::Uuid::parse_str(id).is_ok();
    follow.subscription.grant_id.as_deref() == Some(&snapshot.grant.id)
        && uuid(&snapshot.grant.id)
        && uuid(&snapshot.generation)
        && snapshot.grant.owner.user_id != user
        && !snapshot.grant.owner.disabled
        && snapshot.grant.enabled
        && snapshot
            .assets
            .iter()
            .all(|a| uuid(&a.id) && a.size <= LIMIT && ["image", "file"].contains(&a.kind.as_str()))
        && snapshot.categories.iter().all(|c| {
            (uuid(&c.id) || c.id == "common")
                && snapshot
                    .grant
                    .root_ids
                    .contains(c.parent_id.as_ref().unwrap_or(&c.id))
        })
        && snapshot
            .phrases
            .iter()
            .all(|p| uuid(&p.id) && snapshot.grant.root_ids.contains(&p.root_id))
}

impl TeamClient {
    fn member_dir(&self, active: &Active) -> PathBuf {
        self.dir.join(&active.connection.partition).join("member")
    }
    fn member_assets(&self, active: &Active, snapshot: &wire::Snapshot) -> PathBuf {
        self.member_dir(active)
            .join("assets")
            .join(&snapshot.grant.id)
            .join(snapshot.grant.version.to_string())
            .join(&snapshot.generation)
    }
    fn member_asset_path(
        &self,
        active: &Active,
        snapshot: &wire::Snapshot,
        asset: &Asset,
    ) -> PathBuf {
        let safe: String = asset
            .name
            .chars()
            .map(|c| {
                if c.is_control() || "<>:\"/\\|?*".contains(c) {
                    '_'
                } else {
                    c
                }
            })
            .take(120)
            .collect();
        self.member_assets(active, snapshot).join(format!(
            "{}-{}",
            asset.id,
            if asset.kind == "image" {
                "image.png"
            } else {
                safe.trim_end_matches(['.', ' '])
            }
        ))
    }
    pub(super) fn initialize_member(&self) {
        let Ok(active) = self.active() else { return };
        let mut member = self.member.lock().unwrap();
        if member.epoch == active.epoch {
            return;
        }
        member.cancel.cancel();
        member.worker_cancel.cancel();
        let cached = load_json::<Cached>(&self.member_dir(&active).join("follow.json"))
            .filter(|c| {
                active.authorization.capabilities.member_phrase_sync
                    && !active.authorization.user.must_change_password
                    && valid_follow(&c.follow, &active.authorization.user.id)
            })
            .unwrap_or_default();
        *member = MemberState {
            epoch: active.epoch,
            cached,
            shortcuts: load_json(&self.dir.join("member-shortcuts.json")).unwrap_or_default(),
            cancel: active.cancel.child_token(),
            ..Default::default()
        };
    }
    pub(super) fn clear_member(&self, app: &tauri::AppHandle) {
        let mut member = self.member.lock().unwrap();
        if member.epoch == 0 {
            return;
        }
        member.cancel.cancel();
        member.worker_cancel.cancel();
        let delivering = member.delivery.is_some();
        *member = MemberState::default();
        drop(member);
        self.member_invalidated(app, delivering, true);
    }
    fn member_invalidated(&self, app: &tauri::AppHandle, delivering: bool, content_changed: bool) {
        let follow = self.member.lock().unwrap().cached.follow.clone();
        if let Some(state) = app.try_state::<AppState>() {
            if delivering {
                state.delivery.cancelled.store(true, Ordering::Release);
            }
            let mut map = state.native.sequence_map.write().unwrap();
            if map.0.iter().flatten().any(|id| {
                wire::ContentRef::from_key(id).is_some_and(|r| find_phrase(&follow, &r).is_none())
            }) {
                *map = (vec![], true);
            }
            drop(map);
            if content_changed {
                state.native.hide_preview(None);
                state.native.cancel_candidates();
                if let Err(error) = crate::refreshed(app, &state) {
                    log::error!("成员话术快捷键刷新失败：{error}");
                    let _ = app.emit("native-error", error);
                }
            }
        }
        let _ = app.emit("member-content-changed", ());
    }
    fn member_context(&self, epoch: u64) -> Result<AccountContext> {
        let active = self.active()?;
        if active.epoch != epoch
            || !active.authorization.capabilities.member_phrase_sync
            || active.authorization.user.must_change_password
        {
            return Err("账号已变化或 Hub 尚不支持成员话术，请修改初始密码或升级。".into());
        }
        Ok(AccountContext {
            server: active.connection.server,
            user_id: active.authorization.user.id,
            username: active.authorization.user.username,
            epoch,
            cancel: active.cancel,
        })
    }
    pub async fn member_request<T: DeserializeOwned>(
        &self,
        epoch: u64,
        method: Method,
        route: &str,
        body: Option<serde_json::Value>,
    ) -> Result<T> {
        let ctx = self.member_context(epoch)?;
        let active = self.active()?;
        self.with_account(&ctx, || Ok(()))?;
        let value = self.request(&active, method, route, body).await?;
        self.with_account(&ctx, || Ok(value))
    }
    pub async fn member_overview(&self, epoch: u64) -> Result<Overview> {
        let ctx = self.member_context(epoch)?;
        let active = self.active()?;
        let path = self.member_dir(&active).join("overview.json");
        let result = self
            .member_request::<wire::Overview>(epoch, Method::GET, "me/phrase-grants", None)
            .await;
        self.with_account(&ctx, || match result {
            Ok(value) => {
                let cached = Overview {
                    user_id: ctx.user_id.clone(),
                    value,
                    offline: false,
                };
                save_json(&path, &cached)?;
                Ok(cached)
            }
            Err(error) => {
                // 只允许连接故障退回缓存；明确拒绝和损坏响应不能被离线提示掩盖。
                if error.starts_with("服务器连接失败") {
                    if let Some(mut cached) =
                        load_json::<Overview>(&path).filter(|c| c.user_id == ctx.user_id)
                    {
                        cached.offline = true;
                        return Ok(cached);
                    }
                } else if error.starts_with("401:")
                    || error.starts_with("403:")
                    || error.starts_with("404:")
                {
                    let _ = fs::remove_file(&path);
                }
                Err(error)
            }
        })
    }
    pub async fn member_subscribe(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        epoch: u64,
        request: wire::Subscribe,
    ) -> Result<wire::Subscription> {
        let ctx = self.member_context(epoch)?;
        let _refresh = self.member_refresh.lock().await;
        let subscription: wire::Subscription = self
            .member_request(
                epoch,
                Method::PUT,
                "me/phrase-subscription",
                Some(serde_json::to_value(request).map_err(io_error)?),
            )
            .await?;
        // 已提交切换后先撤下旧缓存；后续刷新失败也不能继续显示或投递旧成员内容。
        let follow = wire::Follow {
            recipient_id: ctx.user_id.clone(),
            subscription: subscription.clone(),
            snapshot: None,
        };
        self.accept_member(app, &ctx, follow)?;
        drop(_refresh);
        self.member_wake.notify_one();
        if let Err(error) = self.refresh_member(app).await {
            log::warn!("成员话术设置已保存，内容稍后重试：{error}");
        }
        Ok(subscription)
    }
    fn accept_member(
        &self,
        app: &tauri::AppHandle,
        ctx: &AccountContext,
        follow: wire::Follow,
    ) -> Result<()> {
        if !valid_follow(&follow, &ctx.user_id) {
            return Err("成员话术响应的来源或素材无效。".into());
        }
        let active = self.active()?;
        let mut cancel_delivery = false;
        let mut content_changed = false;
        let result = self.with_account(ctx, || {
            let mut member = self.member.lock().unwrap();
            if member.epoch != ctx.epoch {
                return Err("旧成员同步已失效。".into());
            }
            if follow.subscription.version < member.cached.follow.subscription.version {
                return Err("旧成员话术响应已丢弃。".into());
            }
            let old = member.cached.follow.clone();
            if !same_content(&old, &follow) {
                content_changed = true;
                member.cancel.cancel();
                member.cancel = active.cancel.child_token();
                cancel_delivery = member
                    .delivery
                    .as_ref()
                    .is_some_and(|r| find_phrase(&follow, r).is_none());
                if cancel_delivery {
                    member.delivery = None;
                }
            }
            member.cached = Cached {
                last_successful_sync_at: follow.snapshot.as_ref().map(|_| {
                    SystemTime::now()
                        .duration_since(UNIX_EPOCH)
                        .unwrap_or_default()
                        .as_millis() as u64
                }),
                follow,
            };
            member.refreshing = false;
            member.message.clear();
            let path = self.member_dir(&active).join("follow.json");
            if let Err(error) = save_json(&path, &member.cached) {
                if fs::remove_file(&path).is_err() {
                    team_credentials::delete(&active.connection.partition)?;
                }
                return Err(error);
            }
            // 清理只针对本功能受控目录；缩小范围与切换成员后不保留旧来源的素材。
            if let Some(previous) = old.snapshot {
                let old_dir = self.member_assets(&active, &previous);
                let current_dir = member
                    .cached
                    .follow
                    .snapshot
                    .as_ref()
                    .map(|s| self.member_assets(&active, s));
                if Some(&old_dir) != current_dir.as_ref() && old_dir.exists() {
                    fs::remove_dir_all(old_dir).map_err(io_error)?;
                }
            }
            if let Some(snapshot) = &member.cached.follow.snapshot {
                let assets = self.member_assets(&active, snapshot);
                let allowed: HashSet<_> = snapshot
                    .assets
                    .iter()
                    .flat_map(|a| {
                        let p = self.member_asset_path(&active, snapshot, a);
                        [p.clone(), p.with_extension("ready.json")]
                    })
                    .collect();
                if assets.exists() {
                    for entry in fs::read_dir(assets).map_err(io_error)? {
                        let path = entry.map_err(io_error)?.path();
                        if path.is_file() && !allowed.contains(&path) {
                            fs::remove_file(path).map_err(io_error)?;
                        }
                    }
                }
            }
            Ok(())
        });
        self.member_invalidated(app, cancel_delivery, content_changed);
        result
    }
    pub async fn refresh_member(&self, app: &tauri::AppHandle) -> Result<()> {
        let _refresh = self.member_refresh.lock().await;
        let active = self.active()?;
        self.initialize_member();
        if !active.authorization.capabilities.member_phrase_sync
            || active.authorization.user.must_change_password
        {
            self.clear_member(app);
            return Ok(());
        }
        let ctx = self.member_context(active.epoch)?;
        self.with_account(&ctx, || {
            self.member.lock().unwrap().refreshing = true;
            Ok(())
        })?;
        let result = self
            .member_request::<wire::Follow>(
                active.epoch,
                Method::GET,
                "me/phrase-subscription",
                None,
            )
            .await;
        match result {
            Ok(follow) => self.accept_member(app, &ctx, follow),
            Err(error) => {
                if error.starts_with("401:")
                    || error.starts_with("403:")
                    || error.starts_with("404:")
                {
                    // 明确拒绝先持久化空快照，再清理素材；删除失败也不能在离线重启后恢复旧内容。
                    let follow = self.with_account(&ctx, || {
                        let mut follow = self.member.lock().unwrap().cached.follow.clone();
                        follow.recipient_id = ctx.user_id.clone();
                        follow.snapshot = None;
                        Ok(follow)
                    })?;
                    self.accept_member(app, &ctx, follow)?;
                }
                let _ = self.with_account(&ctx, || {
                    let mut member = self.member.lock().unwrap();
                    member.refreshing = false;
                    member.message = error.clone();
                    Ok(())
                });
                let _ = app.emit("member-content-changed", ());
                Err(error)
            }
        }
    }
    pub(super) fn start_member(self: &Arc<Self>, app: tauri::AppHandle) {
        self.initialize_member();
        let Ok(active) = self.active() else { return };
        if !active.authorization.capabilities.member_phrase_sync
            || active.authorization.user.must_change_password
        {
            return;
        }
        let worker_cancel = {
            let mut member = self.member.lock().unwrap();
            if member.worker_epoch == active.epoch {
                return;
            }
            member.worker_epoch = active.epoch;
            member.worker_cancel = active.cancel.child_token();
            member.worker_cancel.clone()
        };
        let this = self.clone();
        let _ = app
            .asset_protocol_scope()
            .allow_directory(this.member_dir(&active), true);
        tauri::async_runtime::spawn(async move {
            loop {
                if worker_cancel.is_cancelled() {
                    return;
                }
                let _ = this.refresh_member(&app).await;
                tokio::select! { _ = worker_cancel.cancelled() => return, _ = this.member_wake.notified() => {}, _ = tokio::time::sleep(Duration::from_secs(60)) => {} }
            }
        });
    }
    fn local_member_phrase(
        &self,
        active: &Active,
        follow: &wire::Follow,
        phrase: &SharedPhrase,
        shortcuts: &BTreeMap<String, String>,
    ) -> ClientPhrase {
        let snapshot = follow.snapshot.as_ref().unwrap();
        let content_ref = reference(follow, phrase);
        let mut ready = true;
        let segments = phrase
            .segments
            .iter()
            .map(|s| local::Segment {
                blocks: s
                    .blocks
                    .iter()
                    .map(|b| match b {
                        SharedBlock::Text { text } => local::Block::Text { text: text.clone() },
                        SharedBlock::Image { asset_id, name }
                        | SharedBlock::File { asset_id, name } => {
                            let asset = snapshot.assets.iter().find(|a| a.id == *asset_id);
                            let path = asset.map(|a| self.member_asset_path(active, snapshot, a));
                            let available = path.as_ref().is_some_and(|p| {
                                ready_path(p)
                                    && asset.is_some_and(|a| {
                                        fs::metadata(p).is_ok_and(|m| m.len() == a.size)
                                    })
                            });
                            ready &= available;
                            let path = path
                                .filter(|_| available)
                                .map(|p| local::external_path(&p))
                                .unwrap_or_default();
                            if matches!(b, SharedBlock::Image { .. }) {
                                local::Block::Image {
                                    path,
                                    name: name.clone(),
                                }
                            } else {
                                local::Block::File {
                                    path,
                                    name: name.clone(),
                                }
                            }
                        }
                    })
                    .collect(),
            })
            .collect();
        ClientPhrase {
            phrase: local::Phrase {
                id: content_ref.key(),
                root_id: wire::category_key(&snapshot.grant.id, &phrase.root_id),
                group_id: phrase
                    .group_id
                    .as_ref()
                    .map(|id| wire::category_key(&snapshot.grant.id, id)),
                title: phrase.title.clone(),
                color: phrase.color.clone(),
                background: phrase.background.clone(),
                shortcut: shortcuts
                    .get(&shortcut_key(active, &content_ref))
                    .cloned()
                    .unwrap_or_default(),
                position: phrase.position,
                segments,
            },
            content_ref,
            ready,
        }
    }
    pub fn member_view(&self) -> Result<View> {
        let Ok(active) = self.active() else {
            return Ok(View::default());
        };
        if !active.authorization.capabilities.member_phrase_sync
            || active.authorization.user.must_change_password
        {
            return Ok(View::default());
        }
        let ctx = self.member_context(active.epoch)?;
        self.with_account(&ctx, || {
            let member = self.member.lock().unwrap();
            if member.epoch != active.epoch || !active.authorization.capabilities.member_phrase_sync
            {
                return Ok(View::default());
            }
            let mut view = View {
                session_epoch: active.epoch,
                subscription: member.cached.follow.subscription.clone(),
                state: if active.offline {
                    "离线"
                } else if member.refreshing {
                    "同步中"
                } else if !member.message.is_empty() {
                    "更新失败"
                } else if member.cached.follow.snapshot.is_some() {
                    "已同步"
                } else if member.cached.follow.subscription.grant_id.is_some() {
                    "同步中"
                } else {
                    "未使用"
                }
                .into(),
                message: member.message.clone(),
                last_successful_sync_at: member.cached.last_successful_sync_at,
                ..Default::default()
            };
            if let Some(snapshot) = &member.cached.follow.snapshot {
                view.owner_label = owner_label(snapshot);
                view.categories = snapshot
                    .categories
                    .iter()
                    .map(|c| ClientCategory {
                        category: local::Category {
                            id: wire::category_key(&snapshot.grant.id, &c.id),
                            name: c.name.clone(),
                            kind: "phrase".into(),
                            parent_id: c
                                .parent_id
                                .as_ref()
                                .map(|id| wire::category_key(&snapshot.grant.id, id)),
                            position: c.position,
                            background: c.background.clone(),
                        },
                        read_only: true,
                        source_label: view.owner_label.clone(),
                    })
                    .collect();
                view.phrases = snapshot
                    .phrases
                    .iter()
                    .map(|p| {
                        self.local_member_phrase(
                            &active,
                            &member.cached.follow,
                            p,
                            &member.shortcuts,
                        )
                    })
                    .collect();
            }
            Ok(view)
        })
    }
    pub fn member_phrase(&self, key: &str, require_ready: bool) -> Result<local::Phrase> {
        let reference = wire::ContentRef::from_key(key).ok_or("成员话术引用无效。")?;
        let active = self.active()?;
        let ctx = self.member_context(active.epoch)?;
        self.with_account(&ctx, || {
            let member = self.member.lock().unwrap();
            if member.epoch != ctx.epoch {
                return Err("账号已变化，请重新选择话术。".into());
            }
            let phrase = find_phrase(&member.cached.follow, &reference)
                .ok_or("成员话术或分享范围已更新，请重新选择。")?;
            let phrase =
                self.local_member_phrase(&active, &member.cached.follow, phrase, &member.shortcuts);
            if require_ready && !phrase.ready {
                return Err("素材未缓存，请下载后重新触发。".into());
            }
            Ok(phrase.phrase)
        })
    }
    /// 只接受当前授权版本的引用；保存期间持有账号锁，旧窗口不能把偏好写到另一个账号。
    pub fn save_member_shortcut(&self, epoch: u64, key: &str, shortcut: &str) -> Result<()> {
        let reference = wire::ContentRef::from_key(key).ok_or("成员话术引用无效。")?;
        let active = self.active()?;
        let ctx = self.member_context(epoch)?;
        if active.epoch != epoch {
            return Err("账号已变化，请重新设置快捷键。".into());
        }
        let canonical = if shortcut.trim().is_empty() {
            String::new()
        } else {
            crate::native::parse_shortcut(shortcut)?.canonical()
        };
        self.with_account(&ctx, || {
            let mut member = self.member.lock().unwrap();
            if member.epoch != ctx.epoch || find_phrase(&member.cached.follow, &reference).is_none()
            {
                return Err("账号、成员话术或分享范围已更新，请重新选择。".into());
            }
            let mut shortcuts = member.shortcuts.clone();
            let key = shortcut_key(&active, &reference);
            if canonical.is_empty() {
                shortcuts.remove(&key);
            } else {
                shortcuts.insert(key, canonical);
            }
            save_json(&self.dir.join("member-shortcuts.json"), &shortcuts)?;
            member.shortcuts = shortcuts;
            Ok(())
        })
    }
    pub async fn prepare_member(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        keys: Vec<String>,
        attachments: bool,
    ) -> Result<()> {
        let active = self.active()?;
        let mut ctx = self.member_context(active.epoch)?;
        let (follow, assets, cancel) = self.with_account(&ctx, || {
            let member = self.member.lock().unwrap();
            let follow = member.cached.follow.clone();
            let snapshot = follow.snapshot.as_ref().ok_or("请先选择使用话术的成员。")?;
            let mut ids = HashSet::new();
            for key in &keys {
                let reference = wire::ContentRef::from_key(key).ok_or("成员话术引用无效。")?;
                let phrase =
                    find_phrase(&follow, &reference).ok_or("成员话术已变化，请重新选择。")?;
                for block in phrase.segments.iter().flat_map(|s| &s.blocks) {
                    if let Some((id, kind)) = block.asset() {
                        if attachments || kind == "image" {
                            ids.insert(id);
                        }
                    }
                }
            }
            let assets = snapshot
                .assets
                .iter()
                .filter(|a| ids.contains(a.id.as_str()))
                .cloned()
                .collect::<Vec<_>>();
            Ok((follow, assets, member.cancel.clone()))
        })?;
        ctx.cancel = cancel;
        let snapshot = follow.snapshot.as_ref().unwrap();
        let changed = Arc::new(std::sync::atomic::AtomicBool::new(false));
        let mut tasks = futures_util::stream::iter(assets.into_iter().map(|asset| {
            let this = self.clone(); let ctx = ctx.clone(); let active = active.clone(); let snapshot = snapshot.clone(); let follow = follow.clone(); let changed = changed.clone();
            async move {
                let target = this.member_asset_path(&active, &snapshot, &asset);
                let lock_key = format!("member:{}:{}:{}", snapshot.grant.id, snapshot.grant.version, asset.id);
                let lock = this.download_locks.lock().unwrap().entry(lock_key).or_insert_with(|| Arc::new(AsyncMutex::new(()))).clone();
                let _lock = tokio::select! { _ = ctx.cancel.cancelled() => return Err("成员素材下载已取消。".into()), lock = lock.lock() => lock };
                if ready_path(&target) && fs::metadata(&target).is_ok_and(|m| m.len() == asset.size) { return Ok(()); }
                this.with_account(&ctx, || { if ctx.cancel.is_cancelled() { return Err("分享范围已变化，下载已停止。".into()); }
                    fs::create_dir_all(target.parent().ok_or("素材目录无效。")?).map_err(io_error) })?;
                let mut route = Url::parse("http://localhost/").map_err(io_error)?;
                route.query_pairs_mut().append_pair("grantVersion", &snapshot.grant.version.to_string())
                    .append_pair("subscriptionVersion", &follow.subscription.version.to_string()).append_pair("generation", &snapshot.generation);
                let route = format!("phrase-grants/{}/assets/{}?{}", snapshot.grant.id, asset.id, route.query().unwrap());
                let temp = this.account_download_route(&ctx, &route, &target, asset.size, asset.kind == "image").await?;
                this.with_account(&ctx, || {
                    let member = this.member.lock().unwrap();
                    if ctx.cancel.is_cancelled() || !same_content(&follow, &member.cached.follow) { return Err("分享范围已变化，旧下载已丢弃。".into()); }
                    temp.persist(&target).map_err(io_error)?;
                    save_json(&target.with_extension("ready.json"), &asset.size)?;
                    changed.store(true, Ordering::Release);
                    Ok(())
                })
            }
        })).buffer_unordered(2);
        while let Some(result) = tasks.next().await {
            result?;
        }
        if changed.load(Ordering::Acquire) {
            let _ = app.emit("member-content-changed", ());
        }
        Ok(())
    }
    pub async fn check_member_delivery(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        key: &str,
        expected_epoch: Option<u64>,
    ) -> Result<local::Phrase> {
        let initial = self.active()?;
        if expected_epoch.is_some_and(|epoch| epoch != initial.epoch) {
            return Err("快捷键候选的账号已变化，请重新触发。".into());
        }
        let _ = self.refresh_authorization(app).await;
        let active = self.active()?;
        if active.epoch != initial.epoch {
            return Err("账号已变化，请重新选择话术。".into());
        }
        if !active.offline {
            self.refresh_member(app).await?;
        }
        let phrase = self.member_phrase(key, false)?;
        if self.member_phrase(key, true).is_err() {
            return Err("素材未缓存，请先下载，完成后重新触发投递。".into());
        }
        let reference = wire::ContentRef::from_key(key).ok_or("成员话术引用无效。")?;
        let ctx = self.member_context(active.epoch)?;
        self.with_account(&ctx, || {
            let mut member = self.member.lock().unwrap();
            if find_phrase(&member.cached.follow, &reference).is_none() {
                return Err("成员话术已更新，请重新选择。".into());
            }
            member.delivery = Some(reference);
            Ok(())
        })?;
        Ok(phrase)
    }
    pub fn search_member(
        &self,
        query: &crate::search_match::SearchQuery,
    ) -> Vec<serde_json::Value> {
        let Ok(view) = self.member_view() else {
            return vec![];
        };
        view.phrases.into_iter().filter_map(|p| {
            let filenames = p.phrase.segments.iter().flat_map(|s| &s.blocks).filter_map(|b| match b {
                local::Block::Image { name, .. } | local::Block::File { name, .. } => Some(name.as_str()), _ => None }).collect::<Vec<_>>().join(" ");
            let text = format!("{} {} {}", p.phrase.title, p.phrase.text(), filenames).to_lowercase();
            query.matches(&text).then(|| serde_json::json!({"id":p.phrase.id,"kind":"phrase","phrase":p,"file":null,"sourceLabel":format!("来自{}",view.owner_label)}))
        }).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture(user: &str) -> wire::Follow {
        let grant = local::id();
        wire::Follow {
            recipient_id: user.into(),
            subscription: wire::Subscription {
                grant_id: Some(grant.clone()),
                version: 3,
            },
            snapshot: Some(wire::Snapshot {
                grant: wire::Grant {
                    id: grant,
                    owner: shanyu_team_model::MemberIdentity {
                        user_id: local::id(),
                        username: "002".into(),
                        display_name: "来源".into(),
                        disabled: false,
                    },
                    enabled: true,
                    mode: wire::ScopeMode::Selected,
                    root_ids: vec!["common".into()],
                    roots: vec![],
                    version: 2,
                },
                generation: local::id(),
                version: 7,
                categories: vec![shanyu_team_model::Category {
                    id: "common".into(),
                    name: "同名分类".into(),
                    parent_id: None,
                    position: 0,
                    background: String::new(),
                }],
                phrases: vec![SharedPhrase {
                    id: local::id(),
                    root_id: "common".into(),
                    group_id: None,
                    title: "成员话术".into(),
                    color: String::new(),
                    background: String::new(),
                    position: 0,
                    revision: 5,
                    segments: vec![shanyu_team_model::Segment {
                        blocks: vec![SharedBlock::Text {
                            text: "离线内容".into(),
                        }],
                    }],
                }],
                assets: vec![],
            }),
        }
    }
    #[test]
    fn personal_shortcuts_persist_without_changing_shared_content_and_reject_stale_accounts() {
        let (_dir, client, ctx) = super::super::tests::setup();
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .authorization
            .capabilities
            .member_phrase_sync = true;
        let follow = fixture(&ctx.active.authorization.user.id);
        let original = serde_json::to_value(&follow).unwrap();
        save_json(
            &client.member_dir(&ctx.active).join("follow.json"),
            &Cached {
                follow: follow.clone(),
                last_successful_sync_at: None,
            },
        )
        .unwrap();
        client.initialize_member();
        let first = client.member_view().unwrap().phrases.remove(0).phrase;
        assert!(client
            .save_member_shortcut(ctx.active.epoch + 1, &first.id, "Ctrl+K")
            .is_err());
        assert!(client
            .save_member_shortcut(ctx.active.epoch, &first.id, "K")
            .is_err());
        client
            .save_member_shortcut(ctx.active.epoch, &first.id, "Control+K")
            .unwrap();
        assert_eq!(
            client.member_phrase(&first.id, false).unwrap().shortcut,
            "Ctrl+K"
        );
        assert_eq!(
            serde_json::to_value(&client.member.lock().unwrap().cached.follow).unwrap(),
            original
        );
        // 模拟重启重新加载缓存和偏好，且普通正文修订不会抹掉使用者的快捷键。
        *client.member.lock().unwrap() = MemberState::default();
        client.initialize_member();
        assert_eq!(
            client.member_view().unwrap().phrases[0].phrase.shortcut,
            "Ctrl+K"
        );
        client
            .member
            .lock()
            .unwrap()
            .cached
            .follow
            .snapshot
            .as_mut()
            .unwrap()
            .phrases[0]
            .revision += 1;
        let revised = client.member_view().unwrap().phrases.remove(0).phrase;
        assert_eq!(revised.shortcut, "Ctrl+K");
        assert!(client
            .save_member_shortcut(ctx.active.epoch, &first.id, "Alt+K")
            .is_err());
        // 同一分享给另一个账号使用时，旧窗口和旧偏好都不能跨账号生效。
        let old_ref = wire::ContentRef::from_key(&revised.id).unwrap();
        let mut other = ctx.active.clone();
        other.authorization.user.id = local::id();
        assert_ne!(
            shortcut_key(&ctx.active, &old_ref),
            shortcut_key(&other, &old_ref)
        );
        client
            .save_member_shortcut(ctx.active.epoch, &revised.id, "")
            .unwrap();
        assert!(client
            .member_phrase(&revised.id, false)
            .unwrap()
            .shortcut
            .is_empty());
        client
            .save_member_shortcut(ctx.active.epoch, &revised.id, "Ctrl+K")
            .unwrap();
        client.member.lock().unwrap().cached.follow.snapshot = None;
        assert!(client.member_view().unwrap().phrases.is_empty());
        assert!(client.member_phrase(&revised.id, false).is_err());
        assert!(client
            .save_member_shortcut(ctx.active.epoch, &revised.id, "Alt+K")
            .is_err());
    }
    #[test]
    fn same_shared_phrase_uses_separate_shortcuts_after_account_switch() {
        let (_dir, client, ctx) = super::super::tests::setup();
        let mut active = ctx.active.clone();
        active.authorization.capabilities.member_phrase_sync = true;
        let original_user = active.authorization.user.id.clone();
        let mut follow = fixture(&original_user);
        for (user, expected) in [
            (original_user.clone(), ""),
            (local::id(), ""),
            (original_user.clone(), "Ctrl+K"),
        ] {
            active.epoch += 1;
            active.authorization.user.id = user.clone();
            *client.inner.lock().unwrap().active.as_mut().unwrap() = active.clone();
            follow.recipient_id = user.clone();
            save_json(
                &client.member_dir(&active).join("follow.json"),
                &Cached {
                    follow: follow.clone(),
                    last_successful_sync_at: None,
                },
            )
            .unwrap();
            client.initialize_member();
            let phrase = client.member_view().unwrap().phrases.remove(0).phrase;
            assert_eq!(phrase.shortcut, expected);
            assert!(client
                .save_member_shortcut(active.epoch - 1, &phrase.id, "Alt+K")
                .is_err());
            client
                .save_member_shortcut(
                    active.epoch,
                    &phrase.id,
                    if user == original_user {
                        "Ctrl+K"
                    } else {
                        "Alt+K"
                    },
                )
                .unwrap();
        }
    }
    #[test]
    fn offline_cache_is_namespaced_readonly_and_keeps_displayed_revision() {
        let (_dir, client, ctx) = super::super::tests::setup();
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .authorization
            .capabilities
            .member_phrase_sync = true;
        let follow = fixture(&ctx.active.authorization.user.id);
        let reference = reference(&follow, &follow.snapshot.as_ref().unwrap().phrases[0]);
        save_json(
            &client.member_dir(&ctx.active).join("follow.json"),
            &Cached {
                follow: follow.clone(),
                last_successful_sync_at: Some(1234),
            },
        )
        .unwrap();
        client.initialize_member();
        let view = client.member_view().unwrap();
        for exact in [false, true] {
            for query in ["成员话术", "CHENGYUANHUASHU", "cyhs", "lixianneirong"] {
                assert_eq!(
                    client
                        .search_member(&crate::search_match::SearchQuery::new(query, exact))
                        .len(),
                    1
                );
            }
        }
        assert_eq!(view.state, "离线");
        assert_eq!(view.last_successful_sync_at, Some(1234));
        assert!(view.categories[0].read_only);
        assert_ne!(view.categories[0].category.id, "common");
        assert_eq!(
            view.phrases[0].phrase.root_id,
            view.categories[0].category.id
        );
        assert!(view.phrases[0].phrase.shortcut.is_empty());
        assert_eq!(
            client.member_phrase(&reference.key(), true).unwrap().text(),
            "离线内容"
        );
        let mut changed = follow.clone();
        changed.snapshot.as_mut().unwrap().phrases[0].revision += 1;
        assert!(find_phrase(&changed, &reference).is_none());
        let mut changed = follow.clone();
        changed.snapshot.as_mut().unwrap().grant.version += 1;
        assert!(find_phrase(&changed, &reference).is_none());
        let mut changed = follow.clone();
        changed.subscription.version += 1;
        assert!(find_phrase(&changed, &reference).is_none());
        let mut changed = follow.clone();
        changed.snapshot.as_mut().unwrap().generation = local::id();
        assert!(find_phrase(&changed, &reference).is_none());
        client.member.lock().unwrap().cached.follow = changed;
        assert!(client.member_phrase(&reference.key(), false).is_err());
        client
            .inner
            .lock()
            .unwrap()
            .active
            .take()
            .unwrap()
            .cancel
            .cancel();
        assert!(client.member_phrase(&reference.key(), false).is_err());
        assert!(client
            .search_member(&crate::search_match::SearchQuery::new("cyhs", false))
            .is_empty());
    }
    #[test]
    fn legacy_cache_cannot_be_reused_without_recipient_binding() {
        let mut value = serde_json::to_value(fixture("recipient")).unwrap();
        value.as_object_mut().unwrap().remove("recipientId");
        assert!(serde_json::from_value::<wire::Follow>(value).is_err());
        let mut empty = wire::Follow {
            recipient_id: "recipient".into(),
            ..Default::default()
        };
        assert!(valid_follow(&empty, "recipient"));
        assert!(!valid_follow(&empty, "other"));
        empty.recipient_id.clear();
        assert!(!valid_follow(&empty, "recipient"));
    }
    #[test]
    fn cache_rejects_other_recipient_and_unsafe_material_paths_and_stale_account_writes() {
        let (_dir, client, ctx) = super::super::tests::setup();
        client
            .inner
            .lock()
            .unwrap()
            .active
            .as_mut()
            .unwrap()
            .authorization
            .capabilities
            .member_phrase_sync = true;
        let follow = fixture(&ctx.active.authorization.user.id);
        assert!(valid_follow(&follow, &ctx.active.authorization.user.id));
        assert!(!valid_follow(&follow, &local::id()));
        let mut unsafe_cache = follow.clone();
        unsafe_cache.snapshot.as_mut().unwrap().grant.id = "../elsewhere".into();
        assert!(!valid_follow(
            &unsafe_cache,
            &ctx.active.authorization.user.id
        ));
        let mut unsafe_cache = follow.clone();
        unsafe_cache.snapshot.as_mut().unwrap().generation = "../elsewhere".into();
        assert!(!valid_follow(
            &unsafe_cache,
            &ctx.active.authorization.user.id
        ));
        let old = client.member_context(ctx.active.epoch).unwrap();
        client.inner.lock().unwrap().active.as_mut().unwrap().epoch += 1;
        assert!(client
            .with_account::<()>(&old, || { panic!("旧账号下载不得执行持久化") })
            .is_err());
    }
}
