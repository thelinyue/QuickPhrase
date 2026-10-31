//! 云命令不落盘、不进入话术缓存。请求前后核对在线授权，迟到响应不能跨会话或授权版本。
use super::*;
use wire::cloud_commands::*;

fn allowed(active: &Active, epoch: u64, manage: bool, purge: bool) -> Result<()> {
    if active.epoch != epoch || active.cancel.is_cancelled() {
        return Err("账号已变化，请重新打开闪云中心。".into());
    }
    if !active.authorization.capabilities.cloud_commands {
        return Err("当前闪云版本不支持云命令，请升级服务端。".into());
    }
    let user = &active.authorization.user;
    if active.offline || user.disabled || user.must_change_password {
        return Err("云命令仅在线可用，请重连并完成初始改密。".into());
    }
    let p = &user.cloud_command_permissions;
    if !user.admin && (!p.can_use || (manage && !p.can_manage) || purge) {
        return Err("没有此项云命令权限，请联系服务管理员授权。".into());
    }
    Ok(())
}
impl TeamClient {
    pub fn with_cloud_commands<T>(
        &self,
        epoch: u64,
        version: u64,
        work: impl FnOnce() -> Result<T>,
    ) -> Result<T> {
        let inner = self.inner.lock().unwrap();
        let active = inner.active.as_ref().ok_or("请先登录闪云。")?;
        allowed(active, epoch, false, false)?;
        if active.authorization.user.cloud_command_permissions.version != version {
            return Err("云命令授权已变化，请刷新后重新操作。".into());
        }
        work()
    }
    /// 不重试写请求；失败后只复核授权。复制使用服务端 expectedRevision，绝不静默取新版。
    async fn cloud_request<T: DeserializeOwned>(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        epoch: u64,
        access: (bool, bool),
        method: Method,
        path: &str,
        body: Option<serde_json::Value>,
    ) -> Result<T> {
        if self.active()?.epoch != epoch {
            return Err("账号已变化，旧请求已取消。".into());
        }
        self.refresh_authorization(app).await?;
        let active = self.active()?;
        allowed(&active, epoch, access.0, access.1)?;
        let response = self.request(&active, method, path, body).await;
        let authorization = self.refresh_authorization(app).await;
        let result: T = response?;
        authorization?;
        let current = self.active()?;
        allowed(&current, epoch, access.0, access.1)?;
        if active.authorization.user.cloud_command_permissions
            != current.authorization.user.cloud_command_permissions
            || active.authorization.user.admin != current.authorization.user.admin
        {
            return Err("云命令授权已变化，旧响应已丢弃；请刷新核对结果。".into());
        }
        Ok(result)
    }
    pub async fn cloud_commands(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        epoch: u64,
        query: CloudCommandQuery,
    ) -> Result<CloudCommands> {
        let mut url = Url::parse("http://localhost/cloud/commands").unwrap();
        {
            let mut pairs = url.query_pairs_mut();
            pairs
                .append_pair("q", &query.q)
                .append_pair("recycled", &query.recycled.to_string())
                .append_pair("offset", &query.offset.to_string())
                .append_pair("limit", &query.limit.to_string());
            if let Some(category) = &query.category {
                pairs.append_pair("category", category);
            }
        }
        self.cloud_request(
            app,
            epoch,
            (query.recycled, false),
            Method::GET,
            &format!("cloud/commands?{}", url.query().unwrap()),
            None,
        )
        .await
    }
    pub async fn cloud_command(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        epoch: u64,
        id: &str,
        revision: Option<u64>,
    ) -> Result<CloudCommand> {
        command_id(id)?;
        let path = format!(
            "cloud/commands/{id}{}",
            revision
                .map(|r| format!("?expectedRevision={r}"))
                .unwrap_or_default()
        );
        self.cloud_request(app, epoch, (false, false), Method::GET, &path, None)
            .await
    }
    pub async fn save_cloud_command(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        epoch: u64,
        id: Option<String>,
        request: CloudCommandWrite,
    ) -> Result<CloudCommand> {
        request.validate()?;
        if let Some(id) = &id {
            command_id(id)?;
        }
        let path = id
            .as_ref()
            .map(|id| format!("cloud/commands/{id}"))
            .unwrap_or_else(|| "cloud/commands".into());
        self.cloud_request(
            app,
            epoch,
            (true, false),
            if id.is_some() {
                Method::PUT
            } else {
                Method::POST
            },
            &path,
            Some(serde_json::to_value(request).map_err(io_error)?),
        )
        .await
    }
    pub async fn manage_cloud_command(
        self: &Arc<Self>,
        app: &tauri::AppHandle,
        epoch: u64,
        id: &str,
        operation: &str,
        request: CloudCommandRevision,
    ) -> Result<()> {
        command_id(id)?;
        let (method, path) = match operation {
            "recycle" | "restore" => (Method::POST, format!("cloud/commands/{id}/{operation}")),
            "purge" => (Method::DELETE, format!("cloud/commands/{id}")),
            _ => return Err("未知云命令操作。".into()),
        };
        self.cloud_request(
            app,
            epoch,
            (true, operation == "purge"),
            method,
            &path,
            Some(serde_json::to_value(request).map_err(io_error)?),
        )
        .await
    }
}
fn command_id(id: &str) -> Result<()> {
    if uuid::Uuid::parse_str(id).is_err() {
        return Err("云命令编号无效，请刷新后重试。".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn command_access_rejects_old_session_offline_and_independent_permissions() {
        let (_dir, _client, context) = super::super::tests::setup();
        let mut active = context.active;
        active.offline = false;
        active.authorization.capabilities.cloud_commands = true;
        assert!(allowed(&active, active.epoch, false, false).is_err());
        active.authorization.user.cloud_command_permissions.can_use = true;
        assert!(allowed(&active, active.epoch, false, false).is_ok());
        assert!(allowed(&active, active.epoch, true, false).is_err());
        active
            .authorization
            .user
            .cloud_command_permissions
            .can_manage = true;
        assert!(allowed(&active, active.epoch, true, false).is_ok());
        assert!(allowed(&active, active.epoch, true, true).is_err());
        assert!(allowed(&active, active.epoch + 1, false, false).is_err());
        active.offline = true;
        assert!(allowed(&active, active.epoch, false, false).is_err());
    }

    #[test]
    fn copy_boundary_rejects_revoked_regranted_and_switched_sessions_before_clipboard_work() {
        let (_dir, client, context) = super::super::tests::setup();
        {
            let mut inner = client.inner.lock().unwrap();
            let active = inner.active.as_mut().unwrap();
            active.offline = false;
            active.authorization.capabilities.cloud_commands = true;
            active.authorization.user.cloud_command_permissions = CloudCommandPermissions {
                can_use: true,
                can_manage: false,
                version: 1,
            };
        }
        assert!(client
            .with_cloud_commands(context.active.epoch, 1, || Ok(()))
            .is_ok());
        {
            let mut inner = client.inner.lock().unwrap();
            let p = &mut inner
                .active
                .as_mut()
                .unwrap()
                .authorization
                .user
                .cloud_command_permissions;
            p.can_use = false;
            p.version = 2;
        }
        assert!(client
            .with_cloud_commands::<()>(context.active.epoch, 1, || panic!("撤权后不能写剪贴板"))
            .is_err());
        {
            let mut inner = client.inner.lock().unwrap();
            let p = &mut inner
                .active
                .as_mut()
                .unwrap()
                .authorization
                .user
                .cloud_command_permissions;
            p.can_use = true;
            p.version = 3;
        }
        assert!(client
            .with_cloud_commands::<()>(context.active.epoch, 1, || panic!("重新授权不能接受旧响应"))
            .is_err());
        assert!(client
            .with_cloud_commands::<()>(context.active.epoch + 1, 3, || panic!("切号不能接受旧复制"))
            .is_err());
    }
}
