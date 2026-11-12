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
