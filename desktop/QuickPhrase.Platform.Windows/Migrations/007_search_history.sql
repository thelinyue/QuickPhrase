-- 007_search_history
-- 搜索历史只保存关键词和当前本机时间，不写入话术正文或投递诊断。
-- 字段名保留 _utc 以兼容已批准的 Core 契约，实际值使用 ISO 8601 本地偏移量。
CREATE TABLE search_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    query TEXT NOT NULL CHECK (length(query) BETWEEN 1 AND 200),
    normalized_query TEXT NOT NULL UNIQUE,
    last_searched_at_utc TEXT NOT NULL
);

CREATE INDEX ix_search_history_last_searched_at
    ON search_history(last_searched_at_utc DESC, id DESC);
