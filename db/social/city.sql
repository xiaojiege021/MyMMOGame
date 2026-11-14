-- P3-2a：城池与城池税收账本。只建表，不建数据库、不新建 schema 属主。
--
-- 开发架构 4.2 / 6 节：灵石矿脉归属记在城池上，不做成玩家个人采集点；
-- 开采权变更写在城池数据里。城池税收与拍卖成交销毁的金币分开记账：
-- 这里是「社交」.city_tax_ledger，拍卖销毁税是「交易」.auction_settlement.tax_gold。
-- 归属变更与抽成写回留给 P3-3a4 城战卡；本卡只登记归属、建账本，不写回。
CREATE TABLE IF NOT EXISTS "社交".city (
    city_id     text        PRIMARY KEY,
    name        text        NOT NULL,
    vein_id     text        NOT NULL UNIQUE,
    -- 掌管方（宗门编号）。城战写回前为空；本卡不写这一列。
    owner_sect  text,
    tax_pct     int         NOT NULL CHECK (tax_pct BETWEEN 0 AND 100),
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS "社交".city_tax_ledger (
    txn_id      text        PRIMARY KEY,
    city_id     text        NOT NULL REFERENCES "社交".city (city_id) ON DELETE CASCADE,
    source      text        NOT NULL CHECK (source IN ('task_grant', 'mining', 'other')),
    gross_gold  bigint      NOT NULL CHECK (gross_gold >= 0),
    tax_gold    bigint      NOT NULL CHECK (tax_gold >= 0),
    recorded_at timestamptz NOT NULL DEFAULT now(),
    CHECK (tax_gold <= gross_gold)
);
