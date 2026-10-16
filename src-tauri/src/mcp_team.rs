//! MCP 共享写入复用缓存、素材上传与服务端版本事务，整个操作固定最初捕获的账号。
use super::*;
pub struct McpWrite {
    pub epoch: u64,
    pub team: String,
    pub version: u64,
    pub lifecycle: u64,
    pub operation: String,
    pub phrase: Option<local::Phrase>,
    pub category: Option<local::Category>,
    pub id: String,
    pub root_id: Option<String>,
    pub group_id: Option<String>,
    pub kind: Option<String>,
    pub position: Option<usize>,
}
impl TeamClient {
    /// 已有素材以当前话术的服务端引用复用，即使本机尚未下载也不尝试重新上传空路径。
    pub fn mcp_existing_phrase(&self, key: &str, epoch: u64) -> Result<local::Phrase> {
        let reference = wire::ContentRef::from_key(key).ok_or("话术引用无效。")?;
        let ctx = self.recycle_context(
            &reference.team_id,
            Some(epoch),
            Some(reference.lifecycle_version),
        )?;
        let snapshot = self.cached_snapshot(&ctx.team)?;
        let wire = snapshot
            .phrases
            .iter()
            .find(|p| p.id == reference.id && p.revision == reference.revision)
            .ok_or("话术已更新，请重新读取。")?;
        let mut phrase = self.local_phrase(&ctx, wire).phrase;
        for (local, remote) in phrase
            .segments
            .iter_mut()
            .flat_map(|s| &mut s.blocks)
            .zip(wire.segments.iter().flat_map(|s| &s.blocks))
        {
            if let (
                local::Block::Image { path, .. } | local::Block::File { path, .. },
                Some((id, name)),
            ) = (local, remote.asset())
            {
                *path = local::external_path(&self.asset_path(
                    &ctx,
                    id,
                    name,
                    matches!(remote, wire::Block::Image { .. }),
                ));
            }
        }
        Ok(phrase)
    }

    /// 与个人库锁配合，把本机读写固定到当前登录世代，也支持未登录本地库。
    pub fn with_mcp_session<T>(&self, epoch: u64, work: impl FnOnce() -> Result<T>) -> Result<T> {
        let inner = self.inner.lock().unwrap();
        if inner.epoch != epoch {
            return Err("账号已变化，AI 连接请求已失效。".into());
        }
        work()
    }
    pub async fn mcp_write(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        r: McpWrite,
    ) -> Result<serde_json::Value> {
        let ctx = self.recycle_context(&r.team, Some(r.epoch), Some(r.lifecycle))?;
        let mut snapshot = self.cached_snapshot(&r.team)?;
        if snapshot.version != r.version || snapshot.lifecycle_version != r.lifecycle {
            return Err("共享话术版本已变化，请重新读取。".into());
        }
        let reference = wire::ContentRef::from_key(&r.id);
        if reference
            .as_ref()
            .is_some_and(|c| c.team_id != r.team || c.lifecycle_version != r.lifecycle)
        {
            return Err("话术引用不属于本次授权库或库版本，请重新读取。".into());
        }
        let id = reference.map(|c| c.id).unwrap_or(r.id);
        if r.operation == "recycle_phrase" {
            if !snapshot.phrases.iter().any(|p| p.id == id) {
                return Err("话术不存在。".into());
            }
            let version=self.request::<u64>(&ctx.active,Method::POST,&format!("teams/{}/recycle",ctx.team),Some(serde_json::json!({"expectedVersion":r.version,"expectedLifecycleVersion":r.lifecycle,"kind":"phrase","id":id}))).await?;
            self.schedule(app.clone(), &ctx.team, version);
            return Ok(serde_json::json!({"committed":true,"id":id,"version":version}));
        }
        let mut new_assets = vec![];
        let result=async {
            let result_id=match r.operation.as_str() {
                "save_phrase"=>{
                    let p=r.phrase.ok_or("缺少话术。")?;
                    let mut next=self.to_wire_selected(&ctx,&p,&mut new_assets,&[]).await?;
                    if let Some(old)=snapshot.phrases.iter_mut().find(|p|p.id==next.id){next.position=old.position;*old=next.clone();}else{
                        next.position=snapshot.phrases.iter().filter(|p|p.root_id==next.root_id&&p.group_id==next.group_id).map(|p|p.position).max().unwrap_or(-1)+1;
                        snapshot.phrases.push(next.clone());
                    }
                    next.id
                }
                "save_category"=>{
                    let c=r.category.ok_or("缺少分类。")?;
                    if c.kind!="phrase" {return Err("只允许话术分类。".into());}
                    let next=wire::Category{id:if c.id.is_empty(){local::id()}else{c.id},name:c.name,parent_id:c.parent_id,position:c.position,background:c.background};
                    if let Some(old)=snapshot.categories.iter_mut().find(|c|c.id==next.id){*old=next.clone();}else{snapshot.categories.push(next.clone());}
                    next.id
                }
                "move_phrase"=>{
                    let root=r.root_id.ok_or("缺少目标分类。")?;
                    let position=snapshot.phrases.iter().filter(|p|p.root_id==root&&p.group_id==r.group_id).map(|p|p.position).max().unwrap_or(-1)+1;
                    let p=snapshot.phrases.iter_mut().find(|p|p.id==id).ok_or("话术不存在。")?;
                    p.root_id=root;p.group_id=r.group_id;p.position=position;id.clone()
                }
                "sort_item"=>{
                    let position=r.position.ok_or("缺少排序位置。")?;
                    match r.kind.as_deref(){
                        Some("phrase")=>{
                            let p=snapshot.phrases.iter().find(|p|p.id==id).ok_or("话术不存在。")?;
                            let (root,group)=(p.root_id.clone(),p.group_id.clone());
                            let mut items:Vec<_>=snapshot.phrases.iter_mut().filter(|p|p.root_id==root&&p.group_id==group).collect();items.sort_by_key(|p|p.position);
                            let index=items.iter().position(|p|p.id==id).unwrap();let p=items.remove(index);items.insert(position.min(items.len()),p);for (i,p) in items.into_iter().enumerate(){p.position=i as i64;}
                        }
                        Some("category")=>{
                            let parent=snapshot.categories.iter().find(|c|c.id==id).ok_or("分类不存在。")?.parent_id.clone();
                            let mut items:Vec<_>=snapshot.categories.iter_mut().filter(|c|c.parent_id==parent).collect();items.sort_by_key(|c|c.position);
                            let index=items.iter().position(|c|c.id==id).unwrap();let c=items.remove(index);items.insert(position.min(items.len()),c);for(i,c)in items.into_iter().enumerate(){c.position=i as i64;}
                        }
                        _=>return Err("排序类型无效。".into())
                    } id.clone()
                }
                _=>return Err("不支持的共享写入。".into())
            };
            if !Self::valid(&self.inner.lock().unwrap(),&ctx){return Err("账号或共享授权已变化。".into());}
            let request=wire::Publish{expected_version:r.version,expected_lifecycle_version:r.lifecycle,categories:snapshot.categories,phrases:snapshot.phrases,new_assets:new_assets.clone()};
            let next=self.request::<wire::Snapshot>(&ctx.active,Method::PUT,&format!("teams/{}/snapshot",ctx.team),Some(serde_json::to_value(request).map_err(io_error)?)).await?;
            let version=next.version; let refresh=self.accept_snapshot(&ctx,next);self.emit(app);
            Ok(serde_json::json!({"committed":true,"id":result_id,"version":version,"refreshRequired":refresh.is_err()}))
        }.await;
        if result.is_err() && !new_assets.is_empty() {
            let _ = self
                .request::<serde_json::Value>(
                    &ctx.active,
                    Method::POST,
                    &format!("teams/{}/assets/cleanup", ctx.team),
                    Some(serde_json::json!(new_assets)),
                )
                .await;
        }
        result
    }
}
