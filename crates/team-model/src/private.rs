//! 私人同步的归属由认证会话确定，不能把私人库伪装成小组，也不能提交其他账号 ID。
//! 删除仍是一条带版本的记录；备份恢复换 generation，避免旧设备把回滚前的队列直接重放。
use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "lowercase")]
pub enum Kind {
    Category,
    Phrase,
    Common,
    Recycle,
    Setting,
}
impl Kind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Category => "category",
            Self::Phrase => "phrase",
            Self::Common => "common",
            Self::Recycle => "recycle",
            Self::Setting => "setting",
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Record {
    pub kind: Kind,
    pub id: String,
    pub version: u64,
    pub value: Option<Value>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Change {
    pub kind: Kind,
    pub id: String,
    pub expected_version: u64,
    pub value: Option<Value>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Mutation {
    pub generation: String,
    pub request_id: String,
    pub changes: Vec<Change>,
    #[serde(default)]
    pub new_assets: Vec<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Commit {
    pub generation: String,
    pub request_id: String,
    pub version: u64,
    pub records: Vec<Record>,
    pub conflicts: Vec<Record>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Asset {
    pub id: String,
    pub kind: String,
    pub name: String,
    pub size: u64,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub generation: String,
    pub version: u64,
    pub records: Vec<Record>,
    pub assets: Vec<Asset>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Phrase {
    pub id: String,
    pub root_id: String,
    pub group_id: Option<String>,
    pub title: String,
    pub color: String,
    pub background: String,
    pub shortcut: String,
    pub position: i64,
    pub segments: Vec<crate::Segment>,
}
impl Phrase {
    pub fn content(&self) -> crate::Phrase {
        crate::Phrase {
            id: self.id.clone(),
            root_id: self.root_id.clone(),
            group_id: self.group_id.clone(),
            title: self.title.clone(),
            color: self.color.clone(),
            background: self.background.clone(),
            position: self.position,
            revision: 0,
            segments: self.segments.clone(),
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Common {
    pub id: String,
    pub text: String,
    pub position: i64,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Recycle {
    pub id: String,
    pub kind: String,
    pub label: String,
    pub location: String,
    pub deleted_at: u64,
    pub categories: Vec<crate::Category>,
    pub phrases: Vec<Phrase>,
}

/// 明确的设置白名单防止以后新增凭据、路径或设备配置时被顺带上传。
pub const SETTINGS: &[&str] = &[
    "enter",
    "interval",
    "exitSearch",
    "showNumbers",
    "showPreview",
    "showTitle",
    "showSearch",
    "showCommon",
    "fontSize",
    "expand",
    "searchMode",
    "searchHotkey",
    "windowHotkey",
    "commonHotkey",
    "customHotkeys",
    "globalPhraseHotkeys",
    "sequenceHotkeys",
    "sequenceDirectSend",
    "sequenceBindings",
    "categoryHeight",
    "phraseDoubleClick",
    "commonSingleClick",
    "theme",
    "titleColor",
    "phraseCategoryColor",
    "fileCategoryColor",
    "filesFirst",
    "fileCategoryHeight",
    "commonHeight",
];
pub fn requires_confirmation(key: &str) -> bool {
    matches!(
        key,
        "enter"
            | "interval"
            | "phraseDoubleClick"
            | "commonSingleClick"
            | "searchHotkey"
            | "windowHotkey"
            | "commonHotkey"
            | "customHotkeys"
            | "globalPhraseHotkeys"
            | "sequenceHotkeys"
            | "sequenceDirectSend"
            | "sequenceBindings"
    )
}
pub fn validate_setting(key: &str, value: &Value) -> bool {
    match key {
        "enter" => matches!(value.as_str(), Some("enter" | "ctrl-enter")),
        "phraseDoubleClick" => matches!(value.as_str(), Some("insert" | "send")),
        "commonSingleClick" => matches!(value.as_str(), Some("select" | "insert" | "send")),
        "searchMode" => matches!(value.as_str(), Some("fuzzy" | "exact")),
        "theme" => matches!(
            value.as_str(),
            Some("default" | "green" | "blue" | "black" | "pink" | "lavender" | "orange")
        ),
        "interval" => value.as_u64().is_some_and(|v| v <= 10000),
        "fontSize" => value.as_u64().is_some_and(|v| (10..=18).contains(&v)),
        "categoryHeight" | "fileCategoryHeight" => {
            value.as_u64().is_some_and(|v| (31..=200).contains(&v))
        }
        "commonHeight" => value.as_u64().is_some_and(|v| (28..=300).contains(&v)),
        "searchHotkey" | "windowHotkey" | "commonHotkey" => value
            .as_str()
            .is_some_and(|s| s.len() == 1 && s.as_bytes()[0].is_ascii_alphanumeric()),
        "sequenceBindings" => value.as_array().is_some_and(|v| {
            v.len() == 10 && v.iter().all(|v| v.as_str().is_some_and(|s| s.len() <= 100))
        }),
        "titleColor" | "phraseCategoryColor" | "fileCategoryColor" => {
            value.as_str().is_some_and(|s| {
                s.is_empty()
                    || (s.len() == 7
                        && s.starts_with('#')
                        && s[1..].bytes().all(|b| b.is_ascii_hexdigit()))
            })
        }
        key if SETTINGS.contains(&key) => value.is_boolean(),
        _ => false,
    }
}
