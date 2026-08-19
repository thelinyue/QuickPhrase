-- 话术颜色只增加可选元数据；SQLite 默认值保证历史话术内容和其他字段不被重写。
ALTER TABLE phrases ADD COLUMN color_key TEXT NOT NULL DEFAULT 'default'
    CHECK (color_key IN ('default', 'red', 'orange', 'yellow', 'green', 'blue', 'purple', 'gray'));
