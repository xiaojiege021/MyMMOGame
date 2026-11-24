-- 交易 schema。这一行只建 schema，不建表，也不新建数据库。
CREATE SCHEMA IF NOT EXISTS "交易";

-- B5 摆摊：幂等 DDL 同步放在主迁移里，只跑主迁移的老库也能补上；
-- 分卡迁移 db/trade/stall.sql 是同一段（按版本只跑一次，记 sha256）。
ALTER TABLE "交易".auction_listing
    ADD COLUMN IF NOT EXISTS channel text NOT NULL DEFAULT 'auction';

ALTER TABLE "交易".auction_listing
    ADD COLUMN IF NOT EXISTS stall_id text NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS auction_listing_stall_live
    ON "交易".auction_listing (stall_id) WHERE state = 'listed';

CREATE TABLE IF NOT EXISTS "交易".stall (
    stall_id    text        PRIMARY KEY,
    owner_id    bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    owner_name  text        NOT NULL DEFAULT '',
    region_id   text        NOT NULL,
    pos_x       double precision NOT NULL,
    pos_y       double precision NOT NULL,
    title       text        NOT NULL DEFAULT '',
    state       text        NOT NULL DEFAULT 'open'
                CHECK (state IN ('open', 'closed')),
    opened_at   timestamptz NOT NULL DEFAULT now(),
    closed_at   timestamptz
);

CREATE UNIQUE INDEX IF NOT EXISTS stall_live_owner
    ON "交易".stall (owner_id) WHERE state = 'open';
