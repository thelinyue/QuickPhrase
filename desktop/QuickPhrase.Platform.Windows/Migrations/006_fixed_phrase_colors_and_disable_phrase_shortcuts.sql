-- 006_fixed_phrase_colors_and_disable_phrase_shortcuts
-- 在单个迁移事务内重建 phrases/phrase_tags，保留正文、标签、分类、排序和时间字段。
-- 历史话术快捷键统一清空；旧颜色 red/yellow 映射到 pink/tan。

DROP INDEX IF EXISTS ux_phrases_shortcut_normalized;
DROP INDEX IF EXISTS ix_phrases_category_id;
DROP INDEX IF EXISTS ix_phrases_favorite;
DROP INDEX IF EXISTS ix_phrases_last_used_at;
DROP INDEX IF EXISTS ix_phrases_category_sort;
DROP INDEX IF EXISTS ix_phrase_tags_tag_id;

ALTER TABLE phrase_tags RENAME TO phrase_tags_legacy;
ALTER TABLE phrases RENAME TO phrases_legacy;

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
    'None', NULL, NULL,
    usage_count, last_used_at_utc, version, created_at_utc, updated_at_utc,
    CASE LOWER(COALESCE(color_key, 'default'))
        WHEN 'red' THEN 'pink'
        WHEN 'yellow' THEN 'tan'
        WHEN 'default' THEN 'default'
        WHEN 'orange' THEN 'orange'
        WHEN 'blue' THEN 'blue'
        WHEN 'magenta' THEN 'magenta'
        WHEN 'purple' THEN 'purple'
        WHEN 'green' THEN 'green'
        WHEN 'pink' THEN 'pink'
        WHEN 'teal' THEN 'teal'
        WHEN 'tan' THEN 'tan'
        WHEN 'gray' THEN 'gray'
        ELSE 'default'
    END,
    sort_order
FROM phrases_legacy;

CREATE TABLE phrase_tags (
    phrase_id TEXT NOT NULL REFERENCES phrases(id) ON DELETE CASCADE,
    tag_id TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    PRIMARY KEY (phrase_id, tag_id)
);

INSERT INTO phrase_tags (phrase_id, tag_id)
SELECT phrase_id, tag_id FROM phrase_tags_legacy;

DROP TABLE phrase_tags_legacy;
DROP TABLE phrases_legacy;

CREATE UNIQUE INDEX ux_phrases_shortcut_normalized
    ON phrases(shortcut_normalized)
    WHERE shortcut_normalized IS NOT NULL;
CREATE INDEX ix_phrases_category_id ON phrases(category_id);
CREATE INDEX ix_phrases_favorite ON phrases(favorite);
CREATE INDEX ix_phrases_last_used_at ON phrases(last_used_at_utc);
CREATE INDEX ix_phrases_category_sort ON phrases(category_id, sort_order);
CREATE INDEX ix_phrase_tags_tag_id ON phrase_tags(tag_id);
