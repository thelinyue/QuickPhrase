-- 为现有扁平分类表增加自引用父级列；历史分类默认保持一级分类。
ALTER TABLE categories ADD COLUMN parent_id TEXT NULL REFERENCES categories(id) ON DELETE RESTRICT;
CREATE INDEX IF NOT EXISTS ix_categories_parent_id ON categories(parent_id);
