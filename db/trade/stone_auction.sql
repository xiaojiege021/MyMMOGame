-- P3-2a：任务发放的灵石可以进拍卖。只建表，不建数据库。
--
-- 技术架构第 6 节：挂单放「交易」schema，和角色背包分开，避免抢同一批行锁。
-- 灵石是「角色」.currency 的货币行，不在背包，所以挂单没有 slot / item_id，
-- 只有档位与数量；成交时由角色服在同一笔事务里等量转移双方灵石、
-- 扣买方金币、给卖方净额、销毁税，并写挂单状态与流水。
CREATE TABLE IF NOT EXISTS "交易".stone_listing (
    listing_id   bigserial   PRIMARY KEY,
    list_txn     text        NOT NULL UNIQUE,
    seller_id    bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    stone_kind   text        NOT NULL,
    stone_amount bigint      NOT NULL CHECK (stone_amount > 0),
    price_gold   bigint      NOT NULL CHECK (price_gold > 0),
    state        text        NOT NULL DEFAULT 'listed'
                 CHECK (state IN ('listed', 'sold', 'cancelled')),
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS stone_listing_live_seller
    ON "交易".stone_listing (seller_id, stone_kind) WHERE state = 'listed';

-- 同一笔成交第二次提交会撞主键，整笔不做。
CREATE TABLE IF NOT EXISTS "交易".stone_auction_txn (
    txn_id      text        PRIMARY KEY,
    listing_id  bigint      NOT NULL REFERENCES "交易".stone_listing (listing_id) ON DELETE CASCADE,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

-- 税仍直接销毁：没有任何一张表把它记成某个角色的收入，
-- net_gold = price_gold - tax_gold，全库金币净减少 tax_gold。
CREATE TABLE IF NOT EXISTS "交易".stone_settlement (
    txn_id       text        PRIMARY KEY REFERENCES "交易".stone_auction_txn (txn_id),
    listing_id   bigint      NOT NULL REFERENCES "交易".stone_listing (listing_id) ON DELETE CASCADE,
    buyer_id     bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    seller_id    bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    stone_kind   text        NOT NULL,
    stone_amount bigint      NOT NULL CHECK (stone_amount > 0),
    price_gold   bigint      NOT NULL CHECK (price_gold > 0),
    tax_gold     bigint      NOT NULL CHECK (tax_gold >= 0),
    net_gold     bigint      NOT NULL CHECK (net_gold >= 0),
    settled_at   timestamptz NOT NULL DEFAULT now(),
    CHECK (net_gold + tax_gold = price_gold)
);
