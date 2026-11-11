-- P1-5a：拍卖行挂单与成交。只建表，不建数据库。
--
-- 技术架构第 6 节：拍卖行放在「交易」schema，和角色背包分开，避免抢同一批行锁。
-- 挂单由全局服务写，成交状态与流水由角色服在同一笔事务里写（角色服是角色数据
-- 的唯一写入方，跨 schema 仍在同一连接的一笔事务里）。
CREATE TABLE IF NOT EXISTS "交易".auction_listing (
    listing_id  bigserial   PRIMARY KEY,
    -- 挂单事务号：同一笔挂单请求重放只产生一条挂单。
    list_txn    text        NOT NULL UNIQUE,
    seller_id   bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    slot        smallint    NOT NULL CHECK (slot BETWEEN 1 AND 200),
    count       integer     NOT NULL CHECK (count > 0),
    price_gold  bigint      NOT NULL CHECK (price_gold > 0),
    state       text        NOT NULL DEFAULT 'listed'
                CHECK (state IN ('listed', 'sold', 'cancelled')),
    created_at  timestamptz NOT NULL DEFAULT now()
);

-- 同一卖方的同一格同时只能挂一单。撤单/成交后可以再挂，所以用部分唯一索引。
CREATE UNIQUE INDEX IF NOT EXISTS auction_listing_live_slot
    ON "交易".auction_listing (seller_id, slot) WHERE state = 'listed';

-- 事务号主键：同一笔成交第二次提交会撞主键，整笔不做。
CREATE TABLE IF NOT EXISTS "交易".auction_txn (
    txn_id      text        PRIMARY KEY,
    listing_id  bigint      NOT NULL REFERENCES "交易".auction_listing (listing_id) ON DELETE CASCADE,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

-- 成交流水。tax_gold 是「从买方付出的标价里销毁掉的那一部分」：
-- 没有任何一张表会把它记成某个角色的收入，net_gold = price_gold - tax_gold。
CREATE TABLE IF NOT EXISTS "交易".auction_settlement (
    txn_id      text        PRIMARY KEY REFERENCES "交易".auction_txn (txn_id),
    listing_id  bigint      NOT NULL REFERENCES "交易".auction_listing (listing_id) ON DELETE CASCADE,
    buyer_id    bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    seller_id   bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    item_id     text        NOT NULL,
    count       integer     NOT NULL CHECK (count > 0),
    price_gold  bigint      NOT NULL CHECK (price_gold > 0),
    tax_gold    bigint      NOT NULL CHECK (tax_gold >= 0),
    net_gold    bigint      NOT NULL CHECK (net_gold >= 0),
    settled_at  timestamptz NOT NULL DEFAULT now(),
    CHECK (net_gold + tax_gold = price_gold)
);
