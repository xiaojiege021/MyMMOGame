-- P1-5c6：复活卷轴的全局冷却到期。只建表，不建数据库，不改 schema 属主。
--
-- 方案 8.3 / 12.1 写定「卷轴的冷却记在当前玩家身上，跨场保留，时间长」，
-- 技术架构第 104 行把「复活卷轴的冷却到期」点名放在角色 schema。
-- 所以这一行是持久数据：换一场、换一个战斗进程、战斗服重启都还读得到它。
-- 反过来，牧师生息的复活冷却只留在这场战斗进程内存里，结束就丢，
-- 不写角色库、不写这张表——两段冷却各记各的，用一个不开始另一个。
CREATE TABLE IF NOT EXISTS "角色".revive_scroll_cooldown (
    character_id bigint      PRIMARY KEY REFERENCES "角色".character (id) ON DELETE CASCADE,
    ready_at     timestamptz NOT NULL,
    used_at      timestamptz NOT NULL DEFAULT now(),
    last_txn_id  text        NOT NULL
);

-- 冷却到期是从「用掉那一张」开始算的，不是从角色建号开始算。
-- 表里没有行表示这个角色从来没用过卷轴，等价于冷却已到。
