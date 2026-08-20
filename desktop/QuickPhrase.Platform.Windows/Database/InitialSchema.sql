-- QuickPhrase 首次安装数据库结构。
-- 这里只定义当前版本的最终数据模型，不写入默认分类、示例话术、测试账号或模拟记录。

CREATE TABLE categories (
    id TEXT PRIMARY KEY,
    parent_id TEXT NULL REFERENCES categories(id) ON DELETE RESTRICT,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX ux_categories_root_normalized_name
    ON categories(normalized_name)
    WHERE parent_id IS NULL;
CREATE UNIQUE INDEX ux_categories_child_parent_normalized_name
    ON categories(parent_id, normalized_name)
    WHERE parent_id IS NOT NULL;
CREATE INDEX ix_categories_parent_id ON categories(parent_id);

CREATE TABLE phrases (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 80),
    content TEXT NOT NULL CHECK (length(content) BETWEEN 1 AND 4000),
    category_id TEXT NOT NULL REFERENCES categories(id) ON DELETE RESTRICT,
    shortcut_mode TEXT NOT NULL CHECK (shortcut_mode IN ('None', 'Quick', 'Custom')),
    shortcut_display TEXT NULL,
    shortcut_normalized TEXT NULL,
    usage_count INTEGER NOT NULL DEFAULT 0 CHECK (usage_count >= 0),
    last_used_at_utc TEXT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    color_key TEXT NOT NULL DEFAULT 'default'
        CHECK (color_key IN ('default', 'orange', 'blue', 'magenta', 'purple', 'green', 'pink', 'teal', 'tan', 'gray')),
    sort_order INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
    CHECK ((shortcut_mode = 'None' AND shortcut_display IS NULL AND shortcut_normalized IS NULL)
        OR (shortcut_mode <> 'None' AND shortcut_display IS NOT NULL AND shortcut_normalized IS NOT NULL))
);

CREATE UNIQUE INDEX ux_phrases_shortcut_normalized
    ON phrases(shortcut_normalized)
    WHERE shortcut_normalized IS NOT NULL;
CREATE INDEX ix_phrases_category_id ON phrases(category_id);
CREATE INDEX ix_phrases_last_used_at ON phrases(last_used_at_utc);
CREATE INDEX ix_phrases_category_sort ON phrases(category_id, sort_order);

CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    value_json TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_at_utc TEXT NOT NULL
);

-- 仅保存应用运行所需的默认设置，不包含任何示例业务数据。
INSERT INTO settings (key, value_json, version, updated_at_utc) VALUES
('app.settings', '{"schemaVersion":3,"shortcuts":{"flashLauncher":{"modifiers":2,"keyCode":1}},"launchOnStartup":false,"startMinimized":false,"stayInTrayOnClose":true,"quickSendWithoutConfirmation":false,"clipboardCompatibilityMode":true}', 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TABLE search_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    query TEXT NOT NULL CHECK (length(query) BETWEEN 1 AND 200),
    normalized_query TEXT NOT NULL UNIQUE,
    last_searched_at_utc TEXT NOT NULL
);

CREATE INDEX ix_search_history_last_searched_at
    ON search_history(last_searched_at_utc DESC, id DESC);

PRAGMA user_version = 2;
