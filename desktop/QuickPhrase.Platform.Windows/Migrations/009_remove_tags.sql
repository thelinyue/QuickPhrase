-- 不可逆数据清理：正式产品 V1.1 永久移除标签领域及其关联数据。
DROP INDEX IF EXISTS ix_phrase_tags_tag_id;
DROP TABLE IF EXISTS phrase_tags;
DROP TABLE IF EXISTS tags;
