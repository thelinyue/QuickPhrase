CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    checksum TEXT NOT NULL,
    applied_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS categories (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL UNIQUE,
    sort_order INTEGER NOT NULL DEFAULT 0,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS phrases (
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
    CHECK ((shortcut_mode = 'None' AND shortcut_display IS NULL AND shortcut_normalized IS NULL)
        OR (shortcut_mode <> 'None' AND shortcut_display IS NOT NULL AND shortcut_normalized IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS tags (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL UNIQUE,
    created_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS phrase_tags (
    phrase_id TEXT NOT NULL REFERENCES phrases(id) ON DELETE CASCADE,
    tag_id TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    PRIMARY KEY (phrase_id, tag_id)
);

CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value_json TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_at_utc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_phrases_shortcut_normalized
    ON phrases(shortcut_normalized)
    WHERE shortcut_normalized IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_phrases_category_id ON phrases(category_id);
CREATE INDEX IF NOT EXISTS ix_phrases_favorite ON phrases(favorite);
CREATE INDEX IF NOT EXISTS ix_phrases_last_used_at ON phrases(last_used_at_utc);
CREATE INDEX IF NOT EXISTS ix_phrase_tags_tag_id ON phrase_tags(tag_id);

INSERT OR IGNORE INTO categories (id, name, normalized_name, sort_order, version, created_at_utc, updated_at_utc) VALUES
('10000000-0000-4000-8000-000000000001', '设备问题', '设备问题', 1, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('10000000-0000-4000-8000-000000000002', '网络问题', '网络问题', 2, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('10000000-0000-4000-8000-000000000003', '账户问题', '账户问题', 3, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('10000000-0000-4000-8000-000000000004', '售后服务', '售后服务', 4, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('10000000-0000-4000-8000-000000000005', '订单问题', '订单问题', 5, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('10000000-0000-4000-8000-000000000006', '信息收集', '信息收集', 6, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('10000000-0000-4000-8000-000000000007', '通用话术', '通用话术', 7, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

INSERT OR IGNORE INTO tags (id, name, normalized_name, created_at_utc) VALUES
('30000000-0000-4000-8000-000000000001', '客服', '客服', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000002', '设备', '设备', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000003', '网络', '网络', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000004', '账户', '账户', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000005', '售后', '售后', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000006', '订单', '订单', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000007', 'SN', 'SN', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000008', '开场', '开场', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000009', '步骤', '步骤', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000010', '注意事项', '注意事项', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000011', '配置', '配置', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000012', '排查', '排查', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000013', '时间', '时间', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000014', '版本', '版本', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000015', '截图', '截图', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000016', '日志', '日志', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000017', '现象', '现象', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000018', '密码', '密码', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000019', '确认', '确认', strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('30000000-0000-4000-8000-000000000020', '处理', '处理', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

INSERT OR IGNORE INTO phrases (id, title, content, category_id, favorite, shortcut_mode, usage_count, version, created_at_utc, updated_at_utc) VALUES
('20000000-0000-4000-8000-000000000001', '恢复出厂设置', '恢复出厂设置前请先备份重要数据。', '10000000-0000-4000-8000-000000000001', 1, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000002', '网络连接异常', '请检查网络连接是否正常，或重启设备后重试。', '10000000-0000-4000-8000-000000000001', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000003', '密码重置', '您可以通过绑定的手机号或邮箱重置密码。', '10000000-0000-4000-8000-000000000003', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000004', '售后处理说明', '如需售后支持，请提供订单号和问题描述。', '10000000-0000-4000-8000-000000000004', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000005', '订单信息确认', '为了尽快为您处理，请提供订单号和收货手机号。', '10000000-0000-4000-8000-000000000005', 1, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000006', '请提供设备序列号', '请提供设备序列号（SN），方便我们进一步确认设备信息。', '10000000-0000-4000-8000-000000000006', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000007', '您好，请问有什么可以帮助您的？', '您好，请问有什么可以帮助您的？', '10000000-0000-4000-8000-000000000007', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000008', '恢复出厂操作步骤', '请进入设置 > 系统 > 重置，按照页面提示完成操作。', '10000000-0000-4000-8000-000000000001', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000009', '恢复出厂注意事项', '操作前请备份照片、联系人等重要数据，并保持设备电量充足。', '10000000-0000-4000-8000-000000000001', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000010', '恢复配置', '配置恢复完成后，请重新打开应用确认设置是否生效。', '10000000-0000-4000-8000-000000000001', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000011', '恢复网络配置', '请在网络设置中选择恢复默认配置，然后重新连接当前网络。', '10000000-0000-4000-8000-000000000002', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000012', '恢复默认密码', '设备恢复默认密码后，请及时设置新的登录密码。', '10000000-0000-4000-8000-000000000003', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000013', '请提供故障发生时间', '请提供故障发生的具体时间，方便我们对照日志进一步确认。', '10000000-0000-4000-8000-000000000006', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000014', '请提供当前软件版本号', '请提供当前软件版本号，方便我们确认对应的处理方案。', '10000000-0000-4000-8000-000000000006', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000015', '请提供完整的报错截图', '请提供完整的报错截图，确保错误提示和页面上下文都清晰可见。', '10000000-0000-4000-8000-000000000006', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000016', '请提供问题发生后的日志文件', '请提供问题发生后的日志文件，我们会根据日志进一步定位原因。', '10000000-0000-4000-8000-000000000006', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000017', '请确认设备当前网络连接状态', '请确认设备当前是否可以正常连接网络，以及使用的是 Wi-Fi 还是移动网络。', '10000000-0000-4000-8000-000000000006', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
('20000000-0000-4000-8000-000000000018', '请描述具体操作步骤和异常现象', '请描述一下具体的操作步骤和异常现象，方便我们复现并定位问题。', '10000000-0000-4000-8000-000000000006', 0, 'None', 0, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

INSERT OR IGNORE INTO phrase_tags (phrase_id, tag_id) VALUES
('20000000-0000-4000-8000-000000000001', '30000000-0000-4000-8000-000000000001'),
('20000000-0000-4000-8000-000000000001', '30000000-0000-4000-8000-000000000002'),
('20000000-0000-4000-8000-000000000002', '30000000-0000-4000-8000-000000000001'),
('20000000-0000-4000-8000-000000000002', '30000000-0000-4000-8000-000000000003'),
('20000000-0000-4000-8000-000000000003', '30000000-0000-4000-8000-000000000001'),
('20000000-0000-4000-8000-000000000003', '30000000-0000-4000-8000-000000000004'),
('20000000-0000-4000-8000-000000000004', '30000000-0000-4000-8000-000000000001'),
('20000000-0000-4000-8000-000000000004', '30000000-0000-4000-8000-000000000005'),
('20000000-0000-4000-8000-000000000005', '30000000-0000-4000-8000-000000000001'),
('20000000-0000-4000-8000-000000000005', '30000000-0000-4000-8000-000000000006'),
('20000000-0000-4000-8000-000000000006', '30000000-0000-4000-8000-000000000002'),
('20000000-0000-4000-8000-000000000006', '30000000-0000-4000-8000-000000000007'),
('20000000-0000-4000-8000-000000000007', '30000000-0000-4000-8000-000000000001'),
('20000000-0000-4000-8000-000000000007', '30000000-0000-4000-8000-000000000008'),
('20000000-0000-4000-8000-000000000008', '30000000-0000-4000-8000-000000000002'),
('20000000-0000-4000-8000-000000000008', '30000000-0000-4000-8000-000000000009'),
('20000000-0000-4000-8000-000000000009', '30000000-0000-4000-8000-000000000002'),
('20000000-0000-4000-8000-000000000009', '30000000-0000-4000-8000-000000000010'),
('20000000-0000-4000-8000-000000000010', '30000000-0000-4000-8000-000000000002'),
('20000000-0000-4000-8000-000000000010', '30000000-0000-4000-8000-000000000011'),
('20000000-0000-4000-8000-000000000011', '30000000-0000-4000-8000-000000000003'),
('20000000-0000-4000-8000-000000000011', '30000000-0000-4000-8000-000000000011'),
('20000000-0000-4000-8000-000000000012', '30000000-0000-4000-8000-000000000004'),
('20000000-0000-4000-8000-000000000012', '30000000-0000-4000-8000-000000000018'),
('20000000-0000-4000-8000-000000000013', '30000000-0000-4000-8000-000000000012'),
('20000000-0000-4000-8000-000000000013', '30000000-0000-4000-8000-000000000013'),
('20000000-0000-4000-8000-000000000014', '30000000-0000-4000-8000-000000000012'),
('20000000-0000-4000-8000-000000000014', '30000000-0000-4000-8000-000000000014'),
('20000000-0000-4000-8000-000000000015', '30000000-0000-4000-8000-000000000012'),
('20000000-0000-4000-8000-000000000015', '30000000-0000-4000-8000-000000000015'),
('20000000-0000-4000-8000-000000000016', '30000000-0000-4000-8000-000000000012'),
('20000000-0000-4000-8000-000000000016', '30000000-0000-4000-8000-000000000016'),
('20000000-0000-4000-8000-000000000017', '30000000-0000-4000-8000-000000000012'),
('20000000-0000-4000-8000-000000000017', '30000000-0000-4000-8000-000000000003'),
('20000000-0000-4000-8000-000000000018', '30000000-0000-4000-8000-000000000012'),
('20000000-0000-4000-8000-000000000018', '30000000-0000-4000-8000-000000000017');

INSERT OR IGNORE INTO settings (key, value_json, version, updated_at_utc) VALUES
('app.settings', '{"launchOnStartup":false,"startMinimized":false,"stayInTrayOnClose":true,"launcherShortcutDisplay":"Alt + Space","launcherShortcutNormalized":"Alt+Space","autoSend":false,"clipboardCompatibilityMode":true}', 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
