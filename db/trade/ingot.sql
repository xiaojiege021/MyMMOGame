-- P1-5b：元宝寄售。只建表，不建数据库。
--
-- 开发架构第 13 节 P1-5b：元宝可以换成金币，商城不直接卖金币。
-- currency.json 写定「元宝只经寄售换成金币」「金币拍卖与寄售用」，
-- 所以这里没有「用元宝买金币商品」这条路，只有卖方挂元宝、买方付金币。
--
-- 技术架构第 6 节：寄售也放在「交易」schema。挂单由全局服务写，
-- 成交时的元宝与金币划转由角色服在一笔事务里写（角色服是角色数据唯一写入方）。
-- 与 P1-5a 的物品拍卖分开建表：物品有背包格位，元宝是货币、没有格位，
-- 两者共用同一套税率（config/auction.json）但行形状不同。
CREATE TABLE IF NOT EXISTS "交易".auction_ingot_listing (
    listing_id    bigserial   PRIMARY KEY,
    -- 挂单事务号：同一笔挂单请求重放只产生一条挂单。
    list_txn      text        NOT NULL UNIQUE,
    seller_id     bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    -- 这次挂出去多少元宝、要多少金币。到账时卖方只拿 price - tax。
    ingot_amount  bigint      NOT NULL CHECK (ingot_amount > 0),
    price_gold    bigint      NOT NULL CHECK (price_gold > 0),
    state         text        NOT NULL DEFAULT 'listed'
                  CHECK (state IN ('listed', 'sold', 'cancelled')),
    created_at    timestamptz NOT NULL DEFAULT now()
);

-- 事务号主键：同一笔成交第二次提交会撞主键，整笔不做。
CREATE TABLE IF NOT EXISTS "交易".auction_ingot_txn (
    txn_id      text        PRIMARY KEY,
    listing_id  bigint      NOT NULL REFERENCES "交易".auction_ingot_listing (listing_id) ON DELETE CASCADE,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

-- 成交流水。tax_gold 是「从买方付出的标价里销毁掉的那一部分」：
-- 没有任何一张表会把它记成某个角色的收入，net_gold = price_gold - tax_gold。
-- 元宝在买卖双方之间等量转移：卖方 -ingot_amount、买方 +ingot_amount，
-- 不销毁、不生成，全库元宝总量不变。
CREATE TABLE IF NOT EXISTS "交易".auction_ingot_settlement (
    txn_id        text        PRIMARY KEY REFERENCES "交易".auction_ingot_txn (txn_id),
    listing_id    bigint      NOT NULL REFERENCES "交易".auction_ingot_listing (listing_id) ON DELETE CASCADE,
    buyer_id      bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    seller_id     bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    ingot_amount  bigint      NOT NULL CHECK (ingot_amount > 0),
    price_gold    bigint      NOT NULL CHECK (price_gold > 0),
    tax_gold      bigint      NOT NULL CHECK (tax_gold >= 0),
    net_gold      bigint      NOT NULL CHECK (net_gold >= 0),
    settled_at    timestamptz NOT NULL DEFAULT now(),
    CHECK (net_gold + tax_gold = price_gold)
);
