-- 社交 schema 主迁移。宗门、邮件都放在这个 schema，与角色、交易分域，
-- 避免抢同一批行锁；不新建数据库。只跑这一份就能建齐社交 schema 已落地的表。
-- 每张卡自己的 DDL 在 db/social/<卡>.sql，同一份幂等 DDL 也追加进这里：
-- 老卡只跑主迁移时不会缺表（沿用 db/character/character.sql 的写法）。
CREATE SCHEMA IF NOT EXISTS "社交";

-- === P2-1a1 sect.sql（同一份幂等 DDL，追加进主迁移）===
-- 宗门与名册。社交 schema 是名册的唯一写入方，不建外键指向角色 schema，
-- 社交服不碰角色库。人数判定在 SectRepository 的一笔事务里：
-- select ... for update 锁宗门行 → count(*) → insert，同一笔事务。
CREATE SCHEMA IF NOT EXISTS "社交";

CREATE TABLE IF NOT EXISTS "社交".sect (
    id                   bigserial   PRIMARY KEY,
    name                 text        NOT NULL UNIQUE,
    level                int         NOT NULL DEFAULT 1 CHECK (level BETWEEN 1 AND 5),
    founder_character_id bigint      NOT NULL,
    create_txn           text        NOT NULL UNIQUE,
    created_at           timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS "社交".sect_member (
    id           bigserial   PRIMARY KEY,
    sect_id      bigint      NOT NULL REFERENCES "社交".sect (id) ON DELETE CASCADE,
    character_id bigint      NOT NULL UNIQUE,
    joined_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS sect_member_sect
    ON "社交".sect_member (sect_id);

-- === P2-1a2 sect_position.sql（同一份幂等 DDL，追加进主迁移）===
-- 职位列：默认 disciple（无席位上限的默认职位），取值只能是五个职位名之一。
-- 幂等：ADD COLUMN IF NOT EXISTS + DROP/ADD CONSTRAINT，重复跑主迁移不报错。
-- 人数上限、create_txn 等 P2-1a1 的表结构不动。
ALTER TABLE "社交".sect_member
    ADD COLUMN IF NOT EXISTS position text NOT NULL DEFAULT 'disciple';

ALTER TABLE "社交".sect_member
    DROP CONSTRAINT IF EXISTS sect_member_position_chk;
ALTER TABLE "社交".sect_member
    ADD CONSTRAINT sect_member_position_chk
    CHECK (position IN ('master', 'vice_master', 'elder', 'steward', 'disciple'));

-- === P2-1a3 sect_transfer.sql（同一份幂等 DDL，追加进主迁移）===
-- 给 "社交".sect 加「当前掌门」与「幂等事务号」两列，另建传位流水表
-- sect_transfer（txn_id 主键）挡同一事务号重放。master_character_id 是
-- 当前掌门的唯一权威（创建时 = founder_character_id）；founder_character_id
-- 只作创建历史，不再参与权限判定。a1 两表与 a2 的 position 列结构不动。
-- 幂等：ADD COLUMN IF NOT EXISTS / CREATE TABLE IF NOT EXISTS / UPDATE ... WHERE IS NULL
-- / ALTER COLUMN SET NOT NULL，重复跑主迁移不报错。
ALTER TABLE "社交".sect
    ADD COLUMN IF NOT EXISTS master_character_id bigint;
ALTER TABLE "社交".sect
    ADD COLUMN IF NOT EXISTS last_transfer_txn text;

UPDATE "社交".sect
    SET master_character_id = founder_character_id
    WHERE master_character_id IS NULL;

ALTER TABLE "社交".sect
    ALTER COLUMN master_character_id SET NOT NULL;

CREATE TABLE IF NOT EXISTS "社交".sect_transfer (
    txn_id      text        PRIMARY KEY,
    sect_id     bigint      NOT NULL REFERENCES "社交".sect (id) ON DELETE CASCADE,
    old_master  bigint      NOT NULL,
    new_master  bigint      NOT NULL,
    at          timestamptz NOT NULL DEFAULT now()
);

-- === P2-1a4 sect_upgrade.sql（同一份幂等 DDL，追加进主迁移）===
-- 给 "社交".sect 加「宗门建设」与「升级幂等事务号」两列，另建 sect_upgrade
-- （txn_id 主键）流水挡重放。建设只从宗门秘境和宗门任务来、不从商城买；
-- 升级一个字不扣角色货币，社交服不碰角色 schema。a1 两表、a2 的 position 列、
-- a3 的 master_character_id / sect_transfer 结构都不动。
-- 幂等：ADD COLUMN IF NOT EXISTS / DROP + ADD CONSTRAINT /
-- CREATE TABLE IF NOT EXISTS，重复跑主迁移不报错。
ALTER TABLE "社交".sect
    ADD COLUMN IF NOT EXISTS construction bigint NOT NULL DEFAULT 0;

ALTER TABLE "社交".sect
    DROP CONSTRAINT IF EXISTS sect_construction_chk;
ALTER TABLE "社交".sect
    ADD CONSTRAINT sect_construction_chk CHECK (construction >= 0);

ALTER TABLE "社交".sect
    ADD COLUMN IF NOT EXISTS last_upgrade_txn text;

CREATE TABLE IF NOT EXISTS "社交".sect_upgrade (
    txn_id      text        PRIMARY KEY,
    sect_id     bigint      NOT NULL REFERENCES "社交".sect (id) ON DELETE CASCADE,
    from_level  int         NOT NULL,
    to_level    int         NOT NULL,
    cost        bigint      NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- === P3-3a3 sect_tech.sql（同一份幂等 DDL，追加进主迁移）===
-- 宗门科技等级事实表。方案 11.1 只写「贡献 / 建设 / 科技三条线，科技只在城战生效」，
-- 没写定档位数与升级规则：这里只存等级，不存加成系数（系数在 config/sect_tech.json，
-- 战斗服折算），不写商城 / 税收 / 领地字段。社交服写、战斗服经 gRPC 只读。
-- 等级上限不写死在 SQL，由配置硬校验；a1 两表 / a2 position 列 / a3 掌门列 /
-- a4 建设列与 sect_upgrade 结构都不动。
CREATE TABLE IF NOT EXISTS "社交".sect_tech (
    sect_id     bigint      PRIMARY KEY REFERENCES "社交".sect (id) ON DELETE CASCADE,
    level       int         NOT NULL DEFAULT 0 CHECK (level >= 0),
    updated_at  timestamptz NOT NULL DEFAULT now()
);
