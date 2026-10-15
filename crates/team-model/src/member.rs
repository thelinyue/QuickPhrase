//! 成员话术只读投影：分类授权由来源本人维护，接收方不能修改或再次分享这份缓存。
//! 沿用个人库世代和记录修订，授权、订阅各自的版本用于拒绝撤权或切换前的旧任务。
use crate::{private, Category, MemberIdentity, Phrase};
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Grant {
    pub id: String,
    pub owner: MemberIdentity,
    pub enabled: bool,
    pub mode: ScopeMode,
    pub root_ids: Vec<String>,
    pub roots: Vec<Category>,
    pub version: u64,
}

/// 全部模式动态覆盖今后新增的一级分类；指定模式只扩展所选分类内的内容。
#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ScopeMode {
    All,
    Selected,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Subscription {
    pub grant_id: Option<String>,
    pub version: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ShareWrite {
    pub enabled: bool,
    pub mode: ScopeMode,
    pub root_ids: Vec<String>,
    pub expected_version: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Subscribe {
    pub grant_id: Option<String>,
    pub expected_version: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Overview {
    pub sharing: Option<Grant>,
    pub users: Vec<MemberIdentity>,
    pub available: Vec<Grant>,
    pub subscription: Subscription,
    pub categories: Vec<Category>,
    pub source_ready: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub grant: Grant,
    pub generation: String,
    pub version: u64,
    pub categories: Vec<Category>,
    pub phrases: Vec<Phrase>,
    pub assets: Vec<private::Asset>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Follow {
    pub recipient_id: String,
    pub subscription: Subscription,
    pub snapshot: Option<Snapshot>,
}

/// 投递始终绑定用户看到的版本；同 ID 的本人内容、其他成员内容不会落入同一条引用。
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ContentRef {
    pub source: String,
    pub grant_id: String,
    pub grant_version: u64,
    pub subscription_version: u64,
    pub generation: String,
    pub id: String,
    pub revision: u64,
}
impl ContentRef {
    pub fn key(&self) -> String {
        format!(
            "member:{}:{}:{}:{}:{}:{}",
            self.grant_id,
            self.grant_version,
            self.subscription_version,
            self.generation,
            self.id,
            self.revision
        )
    }
    pub fn from_key(key: &str) -> Option<Self> {
        let parts: Vec<_> = key.split(':').collect();
        if parts.len() != 7
            || parts[0] != "member"
            || parts[1].is_empty()
            || parts[4].is_empty()
            || parts[5].is_empty()
        {
            return None;
        }
        Some(Self {
            source: "member".into(),
            grant_id: parts[1].into(),
            grant_version: parts[2].parse().ok()?,
            subscription_version: parts[3].parse().ok()?,
            generation: parts[4].into(),
            id: parts[5].into(),
            revision: parts[6].parse().ok()?,
        })
    }
}

pub fn category_key(grant: &str, id: &str) -> String {
    format!("member-category:{grant}:{id}")
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn member_reference_keeps_authorization_subscription_and_content_versions() {
        let reference = ContentRef {
            source: "member".into(),
            grant_id: "grant".into(),
            grant_version: 3,
            subscription_version: 7,
            generation: "generation".into(),
            id: "phrase".into(),
            revision: 11,
        };
        assert_eq!(ContentRef::from_key(&reference.key()), Some(reference));
        assert!(ContentRef::from_key("private:grant:3:7:generation:phrase:11").is_none());
        assert!(ContentRef::from_key("member:grant:3:7:generation:phrase").is_none());
    }
}
