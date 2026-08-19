-- 010_sibling_category_name_uniqueness
-- 重建 categories 与 phrases，移除历史的全局分类名唯一约束，改为 ParentId + normalized_name 同级唯一。
-- 根分类使用 parent_id IS NULL 的唯一索引；二级分类使用 parent_id + normalized_name 的唯一索引。

DROP INDEX IF EXISTS ix_categories_parent_id;
DROP INDEX IF EXISTS ux_phrases_shortcut_normalized;
DROP INDEX IF EXISTS ix_phrases_category_id;
DROP INDEX IF EXISTS ix_phrases_favorite;
DROP INDEX IF EXISTS ix_phrases_last_used_at;
DROP INDEX IF EXISTS ix_phrases_category_sort;

ALTER TABLE phrases RENAME TO phrases_legacy;
ALTER TABLE categories RENAME TO categories_legacy;

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

INSERT INTO categories (id, parent_id, name, normalized_name, sort_order, version, created_at_utc, updated_at_utc)
SELECT id, parent_id, name, normalized_name, sort_order, version, created_at_utc, updated_at_utc
FROM categories_legacy
ORDER BY CASE WHEN parent_id IS NULL THEN 0 ELSE 1 END, sort_order, id;

CREATE TABLE phrases (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 80),
    content TEXT NOT NULL CHECK (length(content) BETWEEN 1 AND 4000),
    category_id TEXT NOT NULL REFERENCES categories(id) ON DELETE RESTRICT,
    favorite INTEGER NOT NULL DEFAULT 0 CHECK (favorite IN (0, 1)),
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

INSERT INTO phrases (
    id, title, content, category_id, favorite,
    shortcut_mode, shortcut_display, shortcut_normalized,
    usage_count, last_used_at_utc, version, created_at_utc, updated_at_utc,
    color_key, sort_order
)
SELECT
    id, title, content, category_id, favorite,
    shortcut_mode, shortcut_display, shortcut_normalized,
    usage_count, last_used_at_utc, version, created_at_utc, updated_at_utc,
    color_key, sort_order
FROM phrases_legacy;

DROP TABLE phrases_legacy;
DROP TABLE categories_legacy;

CREATE UNIQUE INDEX ux_categories_root_normalized_name
    ON categories(normalized_name)
    WHERE parent_id IS NULL;
CREATE UNIQUE INDEX ux_categories_child_parent_normalized_name
    ON categories(parent_id, normalized_name)
    WHERE parent_id IS NOT NULL;
CREATE INDEX ix_categories_parent_id ON categories(parent_id);
CREATE UNIQUE INDEX ux_phrases_shortcut_normalized
    ON phrases(shortcut_normalized)
    WHERE shortcut_normalized IS NOT NULL;
CREATE INDEX ix_phrases_category_id ON phrases(category_id);
CREATE INDEX ix_phrases_favorite ON phrases(favorite);
CREATE INDEX ix_phrases_last_used_at ON phrases(last_used_at_utc);
CREATE INDEX ix_phrases_category_sort ON phrases(category_id, sort_order);
