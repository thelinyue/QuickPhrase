-- v1 -> v2：收藏能力退出，只删除收藏索引、收藏列和值，其他业务数据保持原样。
DROP INDEX IF EXISTS ix_phrases_favorite;
ALTER TABLE phrases DROP COLUMN favorite;
PRAGMA user_version = 2;
