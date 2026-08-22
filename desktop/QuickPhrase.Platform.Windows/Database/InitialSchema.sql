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
    title TEXT NOT NULL CHECK (length(title) BETWEEN 0 AND 80),
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

-- 媒体资产只保存应用内部标识和脱敏元数据；原文件名、绝对路径和二进制均不进入数据库。
CREATE TABLE media_assets (
    asset_id TEXT PRIMARY KEY,
    storage_key TEXT NOT NULL UNIQUE,
    mime_type TEXT NOT NULL CHECK (mime_type IN ('image/png', 'image/jpeg')),
    byte_length INTEGER NOT NULL CHECK (byte_length BETWEEN 1 AND 10485760),
    pixel_width INTEGER NOT NULL CHECK (pixel_width > 0),
    pixel_height INTEGER NOT NULL CHECK (pixel_height > 0),
    created_at_utc TEXT NOT NULL
);

CREATE TABLE phrase_segments (
    segment_id TEXT PRIMARY KEY,
    phrase_id TEXT NOT NULL REFERENCES phrases(id) ON DELETE CASCADE,
    segment_kind TEXT NOT NULL CHECK (segment_kind IN ('Text', 'Image')),
    text_content TEXT NULL,
    media_asset_id TEXT NULL REFERENCES media_assets(asset_id) ON DELETE RESTRICT,
    sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
    CHECK ((segment_kind = 'Text' AND text_content IS NOT NULL AND length(trim(text_content)) > 0 AND media_asset_id IS NULL)
        OR (segment_kind = 'Image' AND text_content IS NULL AND media_asset_id IS NOT NULL))
);
CREATE UNIQUE INDEX ux_phrase_segments_phrase_sort ON phrase_segments(phrase_id, sort_order);
CREATE INDEX ix_phrase_segments_media_asset ON phrase_segments(media_asset_id) WHERE media_asset_id IS NOT NULL;

CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    value_json TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_at_utc TEXT NOT NULL
);

-- 仅保存应用运行所需的默认设置，不包含任何示例业务数据。
INSERT INTO settings (key, value_json, version, updated_at_utc) VALUES
('app.settings', '{"schemaVersion":1,"shortcuts":{"flashLauncher":{"modifiers":2,"keyCode":1}},"launchOnStartup":false,"startMinimized":false,"stayInTrayOnClose":true,"quickSendWithoutConfirmation":false,"clipboardCompatibilityMode":true,"hasCompletedOnboarding":false,"onboardingVersion":0}', 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

CREATE TABLE search_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    query TEXT NOT NULL CHECK (length(query) BETWEEN 1 AND 200),
    normalized_query TEXT NOT NULL UNIQUE,
    last_searched_at_utc TEXT NOT NULL
);

CREATE INDEX ix_search_history_last_searched_at
    ON search_history(last_searched_at_utc DESC, id DESC);

-- 企业同步缓存。仅保存企业域表，不创建个人同步投递或个人游标。
CREATE TABLE sync_accounts (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    hub_address TEXT NOT NULL,
    account TEXT NOT NULL,
    display_name TEXT NOT NULL,
    device_id TEXT NOT NULL,
    token_reference TEXT NULL,
    status TEXT NOT NULL CHECK (status IN ('Connected', 'AuthenticationRequired', 'Disconnected')),
    last_authenticated_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE enterprise_categories_cache (
    id TEXT NOT NULL,
    generation TEXT NOT NULL,
    parent_id TEXT NULL,
    name TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    version INTEGER NOT NULL CHECK (version > 0),
    PRIMARY KEY (id, generation),
    FOREIGN KEY (parent_id, generation) REFERENCES enterprise_categories_cache(id, generation) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_enterprise_categories_generation_sort ON enterprise_categories_cache(generation, parent_id, sort_order, id);

CREATE TABLE enterprise_phrases_cache (
    id TEXT NOT NULL,
    generation TEXT NOT NULL,
    category_id TEXT NOT NULL,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    version INTEGER NOT NULL CHECK (version > 0),
    PRIMARY KEY (id, generation),
    FOREIGN KEY (category_id, generation) REFERENCES enterprise_categories_cache(id, generation) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_enterprise_phrases_generation_category_sort ON enterprise_phrases_cache(generation, category_id, sort_order, id);

CREATE TABLE enterprise_sync_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    active_generation TEXT NULL,
    cursor TEXT NULL,
    release_number INTEGER NOT NULL DEFAULT 0 CHECK (release_number >= 0),
    last_synchronized_at_utc TEXT NULL,
    last_result TEXT NULL,
    last_error_code TEXT NULL,
    trace_id TEXT NULL
);
INSERT INTO enterprise_sync_state(id, release_number) VALUES (1, 0);



PRAGMA user_version = 1;
