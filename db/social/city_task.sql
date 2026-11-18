-- Q5/J2：城池灵石抽成账本。只建表，不建数据库、不新建 schema 属主。
--
-- 方案 9.5 / 12 写定「采出的灵石大头做成修炼任务和境界任务的奖励，掌管方按
-- 城池税收抽成，比例走配置」：这张表记的是任务发放灵石时的抽成流水。
--
-- 与金币账本「社交」.city_tax_ledger 是**两张表**：灵石与金币单位不同，加在一起
-- 没有意义；与拍卖销毁税的「交易」.auction_settlement.tax_gold 也是分开的。
--
-- 写入方是角色服（钱只在角色服写）：发灵石、写任务流水、写这一笔抽成在同一个
-- 事务、同一个 `grant_txn` 号里；抽成只记账、不从玩家钱包扣（与 N9 金币口径一致）。
-- 税率读运行时「社交」.city.tax_pct（城战写回会改它），不在配置里。
--
-- 幂等：重复跑主迁移不报错（CREATE TABLE IF NOT EXISTS）。同一份 DDL 也追加在
-- db/social/social.sql（社交聚合主迁移）与 db/character/character.sql（角色主迁移，
-- 老卡只跑主迁移也要建得出来）。

CREATE TABLE IF NOT EXISTS "社交".city_stone_tax_ledger (
    txn_id      text        NOT NULL,
    -- 灵石档位。与金币账本分开记，单位不同不能相加。
    currency    text        NOT NULL,
    city_id     text        NOT NULL REFERENCES "社交".city (city_id) ON DELETE CASCADE,
    source      text        NOT NULL CHECK (source IN ('task_grant', 'mining', 'other')),
    gross_stone bigint      NOT NULL CHECK (gross_stone > 0),
    tax_stone   bigint      NOT NULL CHECK (tax_stone >= 0),
    recorded_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (txn_id, currency),
    CHECK (tax_stone <= gross_stone)
);

CREATE INDEX IF NOT EXISTS city_stone_tax_ledger_city
    ON "社交".city_stone_tax_ledger (city_id, recorded_at);
