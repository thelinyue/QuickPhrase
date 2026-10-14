use serde::{Deserialize, Serialize};

pub type Result<T> = std::result::Result<T, String>;
pub fn id() -> String {
    uuid::Uuid::new_v4().to_string()
}

/// Rust 的规范路径可能带有扩展前缀，Shell 图标与目标剪贴板消费者通常需要普通盘符或 UNC 路径。
pub fn external_path(path: &std::path::Path) -> String {
    let text = path.to_string_lossy();
    if let Some(unc) = text.strip_prefix("\\\\?\\UNC\\") {
        format!("\\\\{unc}")
    } else {
        text.strip_prefix("\\\\?\\").unwrap_or(&text).to_owned()
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Category {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub parent_id: Option<String>,
    pub position: i64,
    /// 标签背景色属于单个分类，空字符串使用默认配色；兼容没有此字段的旧备份。
    #[serde(default)]
    pub background: String,
}

/// 分段内使用有序内容块。文字、图片与文件共享同一顺序，避免附件被统一挪到末尾。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub enum Block {
    Text { text: String },
    Image { path: String, name: String },
    File { path: String, name: String },
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Segment {
    pub blocks: Vec<Block>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Phrase {
    pub id: String,
    pub root_id: String,
    pub group_id: Option<String>,
    pub title: String,
    pub color: String,
    pub background: String,
    pub shortcut: String,
    pub position: i64,
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

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LinkedFile {
    pub id: String,
    pub category_id: String,
    pub path: String,
    pub name: String,
    pub position: i64,
    #[serde(default)]
    pub missing: bool,
}

/// 常用短语是独立的有序文字集合，不依赖一级分类，也不包含图片或附件。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CommonPhrase {
    pub id: String,
    pub text: String,
    pub position: i64,
}

pub fn validate_common_text(text: &str) -> Result<()> {
    if text.trim().is_empty() {
        return Err("常用短语内容不能为空。".into());
    }
    if text.chars().count() > 16 {
        return Err("常用短语最多16个字。".into());
    }
    if text.contains(['\r', '\n']) {
        return Err("常用短语只能输入单行文字。".into());
    }
    Ok(())
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct Settings {
    pub startup: bool,
    pub tray: bool,
    pub enter: String,
    pub interval: u64,
    pub exit_search: bool,
    /// 兼容旧设置和备份；新版吸附始终完整展示，不再按闲置时间收成细条。
    pub auto_hide: bool,
    pub hide_delay: u64,
    pub show_numbers: bool,
    pub show_preview: bool,
    pub show_title: bool,
    pub show_search: bool,
    pub show_common: bool,
    pub dock_common: bool,
    pub font_size: u8,
    pub expand: bool,
    pub dock: bool,
    pub dock_side: String,
    pub search_mode: String,
    pub search_hotkey: String,
    pub window_hotkey: String,
    pub common_hotkey: String,
    pub custom_hotkeys: bool,
    pub global_phrase_hotkeys: bool,
    /// 序号动作独立开关，共用全局范围；空绑定表示该槽位关闭，旧设置默认不启用。
    pub sequence_hotkeys: bool,
    pub sequence_direct_send: bool,
    pub sequence_bindings: [String; 10],
    pub pinned: bool,
    /// 一级分类栏的显示设置不改变分类归属；旧设置与旧备份自动使用默认值。
    pub category_height: u16,
    /// 只在用户主动修改后改变点击行为；旧版设置继续双击插入、短语单击选中。
    pub phrase_double_click: String,
    pub common_single_click: String,
    pub dock_search: bool,
    pub free_resize: bool,
    pub remember_dock: bool,
    pub theme: String,
    /// 全局默认颜色与单条话术、单个分类的独立配色分开保存。
    pub title_color: String,
    pub phrase_category_color: String,
    pub file_category_color: String,
    pub files_first: bool,
    /// 我的话术／共享库入口顺序属于本机布局，暂不上传至现有个人设置协议。
    pub teams_first: bool,
    pub file_category_height: u16,
    pub common_height: u16,
    /// 0 为自适应，1—3 为限制可见行数；超出内容仍可滚动访问。
    pub dock_common_rows: u8,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            startup: false,
            tray: true,
            enter: "enter".into(),
            interval: 500,
            exit_search: true,
            auto_hide: true,
            hide_delay: 500,
            show_numbers: true,
            show_preview: true,
            show_title: true,
            show_search: true,
            show_common: true,
            dock_common: true,
            font_size: 12,
            expand: true,
            dock: true,
            dock_side: "right".into(),
            search_mode: "fuzzy".into(),
            search_hotkey: "Q".into(),
            window_hotkey: "W".into(),
            common_hotkey: "E".into(),
            custom_hotkeys: true,
            global_phrase_hotkeys: false,
            sequence_hotkeys: false,
            sequence_direct_send: false,
            sequence_bindings: std::array::from_fn(|i| format!("Ctrl+{}", (i + 1) % 10)),
            pinned: false,
            category_height: 31,
            phrase_double_click: "insert".into(),
            common_single_click: "select".into(),
            dock_search: false,
            free_resize: true,
            remember_dock: true,
            theme: "default".into(),
            title_color: "#ff5100".into(),
            phrase_category_color: String::new(),
            file_category_color: String::new(),
            files_first: false,
            teams_first: false,
            file_category_height: 31,
            common_height: 144,
            dock_common_rows: 0,
        }
    }
}
impl Settings {
    pub fn validate(&self) -> Result<()> {
        if !["enter", "ctrl-enter"].contains(&self.enter.as_str()) || self.interval > 10_000 {
            return Err("发送按键或分段间隔无效（间隔应为0—10000毫秒）。".into());
        }
        if !(10..=18).contains(&self.font_size)
            || !["left", "right"].contains(&self.dock_side.as_str())
            || !["fuzzy", "exact"].contains(&self.search_mode.as_str())
        {
            return Err("界面或吸附设置无效。".into());
        }
        if !(31..=200).contains(&self.category_height) {
            return Err("一级分类区域高度应为31—200像素。".into());
        }
        if !(31..=200).contains(&self.file_category_height)
            || !(28..=300).contains(&self.common_height)
            || self.dock_common_rows > 3
        {
            return Err("文件分类高度应为31—200像素，侧边短语高度应为28—300像素，底部短语请选择1—3行或自适应。".into());
        }
        if !["insert", "send"].contains(&self.phrase_double_click.as_str())
            || !["select", "insert", "send"].contains(&self.common_single_click.as_str())
        {
            return Err("请选择有效的话术双击或常用短语单击方式。".into());
        }
        if ![
            "default", "green", "blue", "black", "pink", "lavender", "orange",
        ]
        .contains(&self.theme.as_str())
        {
            return Err("请选择有效的主题风格。".into());
        }
        for color in [
            &self.title_color,
            &self.phrase_category_color,
            &self.file_category_color,
        ] {
            if !color.is_empty()
                && !(color.len() == 7
                    && color.starts_with('#')
                    && color[1..].bytes().all(|b| b.is_ascii_hexdigit()))
            {
                return Err("请选择有效的标题字体颜色或分类背景色。".into());
            }
        }
        if !self.show_title && !self.show_preview {
            return Err("话术列表至少显示标题或内容。".into());
        }
        let keys = [
            &self.search_hotkey,
            &self.window_hotkey,
            &self.common_hotkey,
        ];
        if keys
            .iter()
            .any(|k| k.len() != 1 || !k.as_bytes()[0].is_ascii_alphanumeric())
            || keys[0].eq_ignore_ascii_case(keys[1])
            || keys[0].eq_ignore_ascii_case(keys[2])
            || keys[1].eq_ignore_ascii_case(keys[2])
        {
            return Err("全局快捷键必须使用不同的单个英文字母或数字。".into());
        }
        let mut sequence = Vec::new();
        for (index, value) in self.sequence_bindings.iter().enumerate() {
            if value.is_empty() {
                continue;
            }
            let shortcut = crate::native::parse_shortcut(value)?;
            if shortcut.next_key.is_some() || shortcut.modifiers == 0 {
                return Err(format!("序号{}请使用单个组合键，例如 Ctrl+1。", index + 1));
            }
            if sequence
                .iter()
                .any(|s: &crate::native::Shortcut| s.conflicts(&shortcut))
            {
                return Err(format!("序号{}的快捷键与其他序号重复，请更换。", index + 1));
            }
            if shortcut.modifiers == 2
                && keys
                    .iter()
                    .any(|k| k.as_bytes()[0].to_ascii_uppercase() as u32 == shortcut.key)
            {
                return Err(format!(
                    "序号{}的快捷键与 Alt 导航快捷键冲突，请更换。",
                    index + 1
                ));
            }
            sequence.push(shortcut);
        }
        Ok(())
    }

    /// 已启用的序号键保留完整首键，防止自定义候选吞掉序号动作。
    pub fn shortcut_reserved(&self, shortcut: &crate::native::Shortcut) -> bool {
        (shortcut.modifiers == 2
            && [
                &self.search_hotkey,
                &self.window_hotkey,
                &self.common_hotkey,
            ]
            .iter()
            .any(|k| {
                k.as_bytes()
                    .first()
                    .is_some_and(|key| key.to_ascii_uppercase() as u32 == shortcut.key)
            }))
            || (self.sequence_hotkeys
                && self
                    .sequence_bindings
                    .iter()
                    .filter(|s| !s.is_empty())
                    .any(|s| {
                        crate::native::parse_shortcut(s)
                            .is_ok_and(|reserved| reserved.conflicts(shortcut))
                    }))
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Library {
    pub categories: Vec<Category>,
    pub phrases: Vec<Phrase>,
    pub files: Vec<LinkedFile>,
    // 旧版备份没有此字段，读取时保持为空，不破坏已有话术与文件关联。
    #[serde(default)]
    pub common_phrases: Vec<CommonPhrase>,
    pub settings: Settings,
    pub data_dir: String,
}

/// 回收站保存删除时的话术和分类上下文，独立于正在使用的库。
/// 分类主键用于恢复原归属；保留分段与素材路径，绝不删除外部附件源文件。
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecycleRecord {
    pub id: String,
    pub kind: String,
    pub label: String,
    pub location: String,
    pub deleted_at: u64,
    pub categories: Vec<Category>,
    pub phrases: Vec<Phrase>,
}

/// 列表只读取摘要，完整内容由 Rust 在恢复事务中读取，前端不提交恢复数据。
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RecycleItem {
    pub id: String,
    pub kind: String,
    pub label: String,
    pub location: String,
    pub deleted_at: u64,
    pub phrase_count: usize,
}

pub fn validate_phrase(p: &Phrase) -> Result<()> {
    if p.title.chars().count() > 200 {
        return Err("标题不能超过200个字。".into());
    }
    let mut chars = 0;
    let mut images = 0;
    let mut files = 0;
    if p.segments.is_empty() {
        return Err("请至少添加一段话术。".into());
    }
    for (i, segment) in p.segments.iter().enumerate() {
        let mut filled = false;
        for b in &segment.blocks {
            match b {
                Block::Text { text } => {
                    chars += text.chars().count();
                    filled |= !text.trim().is_empty();
                }
                Block::Image { path, .. } => {
                    images += 1;
                    filled |= !path.is_empty();
                }
                Block::File { path, .. } => {
                    files += 1;
                    filled |= !path.is_empty();
                }
            }
        }
        if !filled {
            return Err(format!("第{}段没有内容，请填写或删除空段。", i + 1));
        }
    }
    if chars > 5000 {
        return Err("话术正文不能超过5000个字。".into());
    }
    if images > 9 || files > 9 {
        return Err("每条话术最多9张图片、9个文件。".into());
    }
    for color in [&p.color, &p.background] {
        if !color.is_empty()
            && !(color.len() == 7
                && color.starts_with('#')
                && color[1..].bytes().all(|b| b.is_ascii_hexdigit()))
        {
            return Err("请选择有效的标题颜色。".into());
        }
    }
    if !p.shortcut.is_empty() {
        crate::native::parse_shortcut(&p.shortcut)?;
    }
    Ok(())
}
