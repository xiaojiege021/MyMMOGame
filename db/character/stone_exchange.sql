-- P3-2a：灵石相邻档兑换流水。只建表，不建数据库。
--
-- 方案第 12 章：相邻一档 10000:1，兑换只在角色服做、不经过拍卖。
-- txn_id 是唯一写入点：同一个事务号再提交会撞主键，整笔不做，不发第二次。
-- 数值不写在库里：from_cost / to_gain 是这一笔按 config/stone_exchange.json 算出来的事实值。
CREATE TABLE IF NOT EXISTS "角色".stone_exchange_txn (
    txn_id       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    from_kind    text        NOT NULL,
    to_kind      text        NOT NULL,
    from_cost    bigint      NOT NULL CHECK (from_cost > 0),
    to_gain      bigint      NOT NULL CHECK (to_gain > 0),
    exchanged_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS stone_exchange_character
    ON "角色".stone_exchange_txn (character_id, exchanged_at DESC);
