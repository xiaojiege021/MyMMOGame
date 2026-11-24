-- B5：摆摊。仍在「交易」schema 里，不建数据库。
--
-- 方案第 12 章写定「拍卖行（金币）+ 摆摊」；摆摊不另立账本、也不另开一套成交：
-- 挂单仍是 auction_listing 的一行（channel = 'stall'、stall_id 指向摊位），
-- 成交仍由角色服走同一笔事务（auction_txn / auction_settlement），
-- 税只体现在「卖方少加那一部分」，没有收款方。
--
-- 摊位本身（谁在哪儿摆、叫什么）是持久数据，写在「交易」.stall：
-- 世界服只拿它做显示，读不到角色库，也不改任何背包行。
ALTER TABLE "交易".auction_listing
    ADD COLUMN IF NOT EXISTS channel text NOT NULL DEFAULT 'auction';

ALTER TABLE "交易".auction_listing
    ADD COLUMN IF NOT EXISTS stall_id text NOT NULL DEFAULT '';

-- 摊主看「我这个摊还挂着几件」、关摊撤单都按这一列走。
CREATE INDEX IF NOT EXISTS auction_listing_stall_live
    ON "交易".auction_listing (stall_id) WHERE state = 'listed';

-- 一个摊主同时只开一个摊（关掉才能再开）。摊位不跨服：region_id 只存本服地表编号。
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
