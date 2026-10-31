//! 公共命令库只保存原始文本，不携带执行目标、脚本文件或投递指令。
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CloudCommandPermissions {
    pub can_use: bool,
    pub can_manage: bool,
    pub version: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CloudCommandPermissionsWrite {
    pub can_use: bool,
    pub can_manage: bool,
    pub expected_version: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CloudCommandSummary {
    pub id: String,
    pub name: String,
    pub category: String,
    pub description: String,
    pub revision: u64,
    pub updated_at: u64,
    pub maintained_by: String,
    pub maintainer_name: String,
    pub deleted_at: Option<u64>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CloudCommand {
    #[serde(flatten)]
    pub summary: CloudCommandSummary,
    pub body: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CloudCommands {
    pub commands: Vec<CloudCommandSummary>,
    pub total: usize,
    pub categories: Vec<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CloudCommandQuery {
    #[serde(default)]
    pub q: String,
    #[serde(default)]
    pub category: Option<String>,
    #[serde(default)]
    pub recycled: bool,
    #[serde(default)]
    pub offset: usize,
    #[serde(default = "page_size")]
    pub limit: usize,
}
fn page_size() -> usize {
    10
}
impl Default for CloudCommandQuery {
    fn default() -> Self {
        Self {
            q: String::new(),
            category: None,
            recycled: false,
            offset: 0,
            limit: page_size(),
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CloudCommandWrite {
    pub name: String,
    pub category: String,
    pub description: String,
    pub body: String,
    pub expected_revision: Option<u64>,
}
impl CloudCommandWrite {
    pub fn validate(&self) -> Result<(), String> {
        if self.name.trim().is_empty()
            || self.name.chars().count() > 100
            || self.name.chars().any(char::is_control)
        {
            return Err("名称需为1至100个字，不能包含控制字符。".into());
        }
        if self.category.chars().count() > 50 || self.category.chars().any(char::is_control) {
            return Err("分类最多50个字，不能包含控制字符。".into());
        }
        if self.description.chars().count() > 5000
            || self.body.trim().is_empty()
            || self.body.len() > 65536
            || self.body.contains('\0')
            || self.description.contains('\0')
        {
            return Err("说明最多5000个字；命令正文不能为空，最多64KB，不能包含空字符。".into());
        }
        Ok(())
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CloudCommandRevision {
    pub expected_revision: u64,
    pub confirm_name: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn legacy_defaults_and_raw_body_are_preserved() {
        let user: crate::User = serde_json::from_value(serde_json::json!({"id":"a","username":"a","admin":false,"disabled":false,"mustChangePassword":false})).unwrap();
        assert_eq!(
            user.cloud_command_permissions,
            CloudCommandPermissions::default()
        );
        let capabilities: crate::Capabilities =
            serde_json::from_value(serde_json::json!({"privateSync":true})).unwrap();
        assert!(!capabilities.cloud_commands);
        let body = "  curl -fsSL 'https://example.test/a?x=1&y=2' | sh\n\\\"$HOME\"\r\n";
        let write = CloudCommandWrite {
            name: "命令".into(),
            category: String::new(),
            description: String::new(),
            body: body.into(),
            expected_revision: None,
        };
        write.validate().unwrap();
        let decoded: CloudCommandWrite =
            serde_json::from_str(&serde_json::to_string(&write).unwrap()).unwrap();
        assert_eq!(decoded.body, body);
        assert!(CloudCommandWrite {
            body: "\0".into(),
            ..write
        }
        .validate()
        .is_err());
    }
}
