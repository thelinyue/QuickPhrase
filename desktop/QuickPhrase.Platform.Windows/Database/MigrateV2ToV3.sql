-- M3 企业同步缓存。只新增企业域表，不创建 M4 个人 outbox 或个人游标。
CREATE TABLE sync_accounts (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    hub_address TEXT NOT NULL,
    account TEXT NOT NULL,
    display_name TEXT NOT NULL,
    device_id TEXT NOT NULL,
    token_reference TEXT NULL,
    status TEXT NOT NULL CHECK (status IN ('Connected', 'AuthenticationRequired', 'Disconnected')),
    last_authenticated_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE enterprise_categories_cache (
    id TEXT NOT NULL,
    generation TEXT NOT NULL,
    parent_id TEXT NULL,
    name TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    version INTEGER NOT NULL CHECK (version > 0),
    PRIMARY KEY (id, generation),
    FOREIGN KEY (parent_id, generation) REFERENCES enterprise_categories_cache(id, generation) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_enterprise_categories_generation_sort ON enterprise_categories_cache(generation, parent_id, sort_order, id);

CREATE TABLE enterprise_phrases_cache (
    id TEXT NOT NULL,
    generation TEXT NOT NULL,
    category_id TEXT NOT NULL,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    version INTEGER NOT NULL CHECK (version > 0),
    PRIMARY KEY (id, generation),
    FOREIGN KEY (category_id, generation) REFERENCES enterprise_categories_cache(id, generation) DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX ix_enterprise_phrases_generation_category_sort ON enterprise_phrases_cache(generation, category_id, sort_order, id);

CREATE TABLE enterprise_sync_state (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    active_generation TEXT NULL,
    cursor TEXT NULL,
    release_number INTEGER NOT NULL DEFAULT 0 CHECK (release_number >= 0),
    last_synchronized_at_utc TEXT NULL,
    last_result TEXT NULL,
    last_error_code TEXT NULL,
    trace_id TEXT NULL
);
INSERT INTO enterprise_sync_state(id, release_number) VALUES (1, 0);

PRAGMA user_version = 3;
