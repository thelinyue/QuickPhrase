//! 小组协议只传素材 ID，不传服务端路径、私人设置或个人快捷键。
//! groupId 始终指二级分类；teamId 才是权限和缓存的隔离边界。
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct User {
    pub id: String,
    pub username: String,
    pub admin: bool,
    pub disabled: bool,
    pub must_change_password: bool,
    #[serde(default)]
    pub display_name: String,
    #[serde(default)]
    pub contact: String,
    /// 建组是账号级授权，不能从任一小组角色推导。旧缓存默认未授权。
    #[serde(default)]
    pub can_create_teams: bool,
}

/// 角色只在对应小组内生效；公司管理员仍由 User.admin 单独表示。
#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum Role {
    #[default]
    Member,
    Editor,
    Manager,
}
impl Role {
    pub fn editable(self) -> bool {
        self != Self::Member
    }
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Member => "member",
            Self::Editor => "editor",
            Self::Manager => "manager",
        }
    }
}
pub fn initial_lifecycle() -> u64 {
    1
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Team {
    pub team_id: String,
    pub name: String,
    pub version: u64,
    pub editable: bool,
    #[serde(default)]
    pub role: Role,
    #[serde(default)]
    pub membership_version: u64,
    #[serde(default = "initial_lifecycle")]
    pub lifecycle_version: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Membership {
    pub user_id: String,
    pub team_id: String,
    pub role: Role,
}

/// 小组管理员只能看到必要身份信息，成员响应不携带联系方式或公司人员目录。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MemberIdentity {
    pub user_id: String,
    pub username: String,
    pub display_name: String,
    pub disabled: bool,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct MemberDetails {
    #[serde(flatten)]
    pub membership: Membership,
    pub username: String,
    #[serde(rename = "displayName")]
    pub display_name: String,
    pub disabled: bool,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TeamMembers {
    pub membership_version: u64,
    pub lifecycle_version: u64,
    pub member_count: usize,
    pub phrase_count: usize,
    pub members: Vec<MemberDetails>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct MembersWrite {
    pub expected_membership_version: u64,
    pub expected_lifecycle_version: u64,
    pub members: Vec<Membership>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TransferManager {
    pub target_user_id: String,
    pub expected_membership_version: u64,
    pub expected_lifecycle_version: u64,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct MembershipVersionRequest {
    pub expected_membership_version: u64,
    pub expected_lifecycle_version: u64,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UserTeamRole {
    pub team_id: String,
    pub name: String,
    pub role: Role,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct DirectoryUser {
    #[serde(flatten)]
    pub user: User,
    pub memberships: Vec<UserTeamRole>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeletedTeam {
    pub team_id: String,
    pub name: String,
    pub lifecycle_version: u64,
    pub deleted_at: u64,
    pub deleted_by: String,
    pub member_count: usize,
    pub phrase_count: usize,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct TeamLifecycleRequest {
    pub expected_lifecycle_version: u64,
    pub confirm_name: Option<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AssetCleanup {
    pub team_id: String,
    pub name: String,
    pub pending_assets: usize,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Login {
    pub username: String,
    pub password: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Session {
    pub token: String,
    pub user: User,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Authorization {
    pub user: User,
    pub teams: Vec<Team>,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Category {
    pub id: String,
    pub name: String,
    pub parent_id: Option<String>,
    pub position: i64,
    #[serde(default)]
    pub background: String,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(tag = "kind", rename_all = "lowercase", deny_unknown_fields)]
pub enum Block {
    Text {
        text: String,
    },
    Image {
        #[serde(rename = "assetId")]
        asset_id: String,
        name: String,
    },
    File {
        #[serde(rename = "assetId")]
        asset_id: String,
        name: String,
    },
}
impl Block {
    pub fn asset(&self) -> Option<(&str, &str)> {
        match self {
            Self::Text { .. } => None,
            Self::Image { asset_id, .. } => Some((asset_id, "image")),
            Self::File { asset_id, .. } => Some((asset_id, "file")),
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub struct Segment {
    pub blocks: Vec<Block>,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Phrase {
    pub id: String,
    pub root_id: String,
    pub group_id: Option<String>,
    pub title: String,
    pub color: String,
    pub background: String,
    pub position: i64,
    pub revision: u64,
    pub segments: Vec<Segment>,
}
impl Phrase {
    pub fn text(&self) -> String {
        self.segments
            .iter()
            .map(|s| {
                s.blocks
                    .iter()
                    .filter_map(|b| match b {
                        Block::Text { text } => Some(text.as_str()),
                        _ => None,
                    })
                    .collect::<String>()
            })
            .collect::<Vec<_>>()
            .join("\n")
    }
    pub fn label(&self) -> String {
        if self.title.is_empty() {
            self.text().chars().take(35).collect()
        } else {
            self.title.clone()
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    pub team_id: String,
    pub version: u64,
    #[serde(default = "initial_lifecycle")]
    pub lifecycle_version: u64,
    pub categories: Vec<Category>,
    pub phrases: Vec<Phrase>,
}

/// 批量写入使用小组版本和事务；新增素材单独列出，失败仅清理本会话尚未引用的素材。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Publish {
    pub expected_version: u64,
    pub expected_lifecycle_version: u64,
    pub categories: Vec<Category>,
    pub phrases: Vec<Phrase>,
    #[serde(default)]
    pub new_assets: Vec<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Asset {
    pub id: String,
    pub team_id: String,
    pub kind: String,
    pub name: String,
    pub size: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecycleRecord {
    pub id: String,
    pub team_id: String,
    pub label: String,
    pub deleted_at: u64,
    pub categories: Vec<Category>,
    pub phrases: Vec<Phrase>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeleteRequest {
    pub expected_version: u64,
    pub expected_lifecycle_version: u64,
    pub kind: String,
    pub id: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PasswordChange {
    pub current_password: String,
    pub password: String,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct UserWrite {
    pub username: Option<String>,
    pub password: Option<String>,
    pub disabled: Option<bool>,
    pub display_name: Option<String>,
    pub contact: Option<String>,
    pub can_create_teams: Option<bool>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TeamWrite {
    pub name: String,
    pub expected_lifecycle_version: Option<u64>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VersionRequest {
    pub expected_version: u64,
    pub expected_lifecycle_version: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Notice {
    pub team_id: Option<String>,
    pub version: Option<u64>,
}

/// 编号快捷键也携带显示版本；只能按这份引用投递，不能悄悄取同步后的新正文。
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ContentRef {
    pub source: String,
    pub team_id: String,
    pub id: String,
    pub revision: u64,
    pub lifecycle_version: u64,
}
impl ContentRef {
    pub fn key(&self) -> String {
        format!(
            "team:{}:{}:{}:{}",
            self.team_id, self.id, self.revision, self.lifecycle_version
        )
    }
    pub fn from_key(key: &str) -> Option<Self> {
        let parts: Vec<_> = key.split(':').collect();
        if parts.len() != 5 || parts[0] != "team" {
            return None;
        }
        Some(Self {
            source: "team".into(),
            team_id: parts[1].into(),
            id: parts[2].into(),
            revision: parts[3].parse().ok()?,
            lifecycle_version: parts[4].parse().ok()?,
        })
    }
}

pub fn validate_phrase(p: &Phrase) -> Result<(), String> {
    if p.title.chars().count() > 200 {
        return Err("标题不能超过200个字。".into());
    }
    if p.segments.is_empty() {
        return Err("请至少添加一段话术。".into());
    }
    let (mut chars, mut images, mut files) = (0, 0, 0);
    for (index, segment) in p.segments.iter().enumerate() {
        let mut filled = false;
        for b in &segment.blocks {
            match b {
                Block::Text { text } => {
                    chars += text.chars().count();
                    filled |= !text.trim().is_empty();
                }
                Block::Image { asset_id, name } | Block::File { asset_id, name } => {
                    if name.is_empty() || name.chars().count() > 255 || asset_id.is_empty() {
                        return Err("素材名称或ID无效。".into());
                    }
                    if matches!(b, Block::Image { .. }) {
                        images += 1;
                    } else {
                        files += 1;
                    }
                    filled = true;
                }
            }
        }
        if !filled {
            return Err(format!("第{}段没有内容。", index + 1));
        }
    }
    if chars > 5000 || images > 9 || files > 9 {
        return Err("每条话术最多5000个字、9张图片、9个文件。".into());
    }
    for c in [&p.color, &p.background] {
        if !c.is_empty()
            && !(c.len() == 7
                && c.starts_with('#')
                && c[1..].bytes().all(|b| b.is_ascii_hexdigit()))
        {
            return Err("请选择有效颜色。".into());
        }
    }
    Ok(())
}
