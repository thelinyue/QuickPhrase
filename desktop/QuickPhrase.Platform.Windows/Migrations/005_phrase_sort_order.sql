-- 005: phrases 表新增 sort_order 列，用于支持话术列表的鼠标拖拽排序持久化。
ALTER TABLE phrases ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0;

-- 回填：在每个 category_id 组内，按 updated_at_utc 倒序（同时间按 id 升序）分配连续递增的 sort_order，
-- 保证现有数据稳定有序。注：即使 group 内 sort_order 相等，运行时再排序即可消除。
UPDATE phrases
SET sort_order = (
    SELECT COUNT(*)
    FROM phrases p2
    WHERE p2.category_id = phrases.category_id
      AND (p2.updated_at_utc > phrases.updated_at_utc
           OR (p2.updated_at_utc = phrases.updated_at_utc AND p2.id <= phrases.id))
);

CREATE INDEX IF NOT EXISTS ix_phrases_category_sort ON phrases(category_id, sort_order);