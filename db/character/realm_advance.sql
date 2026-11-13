-- P3-1a2：境界突破。方案 8.1 写定「修为就是当前境界，历练靠击杀积累，
-- 炼心靠做完这一段已经打开的主线和必要支线」，所以这里不建「修为点数」表：
-- 修为仍读 character.realm，历练是 character.drill 一列，主线场次仍读
-- character.main_stage，必要支线另记一张 character_quest。
--
-- 一次突破落一行 realm_breakthrough：txn_id 是主键，同一个号再提交不会掷第二次。
-- 历练结算落一行 realm_drill：同一场通关一个号，重复结算不会把历练加两次。
-- 三张表都幂等；main 迁移 character.sql 末尾放同一份 DDL，只跑主迁移的老卡也有。
ALTER TABLE "角色".character
    ADD COLUMN IF NOT EXISTS drill bigint NOT NULL DEFAULT 0;

ALTER TABLE "角色".character
    DROP CONSTRAINT IF EXISTS character_drill_non_negative;
ALTER TABLE "角色".character
    ADD CONSTRAINT character_drill_non_negative CHECK (drill >= 0);

CREATE TABLE IF NOT EXISTS "角色".character_quest (
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    quest_id     text        NOT NULL,
    completed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, quest_id)
);

CREATE TABLE IF NOT EXISTS "角色".realm_breakthrough (
    txn_id       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    from_realm   text        NOT NULL,
    to_realm     text        NOT NULL,
    success      boolean     NOT NULL,
    drill_cost   bigint      NOT NULL CHECK (drill_cost >= 0),
    stone_kind   text        NOT NULL,
    stone_cost   bigint      NOT NULL CHECK (stone_cost > 0),
    attempted_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS realm_breakthrough_character
    ON "角色".realm_breakthrough (character_id, attempted_at DESC);

CREATE TABLE IF NOT EXISTS "角色".realm_drill (
    txn_id       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    dungeon_id   text        NOT NULL,
    drill        bigint      NOT NULL CHECK (drill >= 0),
    settled_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS realm_drill_character
    ON "角色".realm_drill (character_id, settled_at DESC);
