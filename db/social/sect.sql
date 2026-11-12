-- P2-1a1：宗门与名册。社交 schema 是名册的唯一写入方，不建外键指向角色 schema，
-- 社交服不碰角色库。人数判定在 SectRepository 的一笔事务里：
-- select ... for update 锁宗门行 → count(*) → insert，同一笔事务，
-- 并发抢最后一个席位时也只会有一个人成功。
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
