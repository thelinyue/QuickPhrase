-- 004: “常用”落库为真实一级分类。
-- 背景：收藏视图（常用 chip）已移除，由真实分类承接“常用话术”入口。
-- 幂等：INSERT OR IGNORE —— 已存在（按 id 或 normalized_name 唯一冲突）时静默跳过，
-- 新库与既有库升级后都会执行一次。
INSERT OR IGNORE INTO categories (id, name, normalized_name, sort_order, version, created_at_utc, updated_at_utc) VALUES
('10000000-0000-4000-8000-000000000008', '常用', '常用', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
