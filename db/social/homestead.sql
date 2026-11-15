-- C3 洞府摆放：只记「谁的哪一格摆了哪一件」这一份展示事实，放社交 schema
-- （技术架构 3 节家园归社交服 / 6 节宗门邮件在社交 schema）。摆件本体仍在
-- 角色 schema 的背包里，社交服只读问角色服 ListInventory，不消耗、不改角色库。
-- 不建外键指向角色 schema：两边 schema 独立，角色号只作事实值。
--
-- 只做展示和生活（方案 12.1）：这里没有家园币、价格、概率、属性、伤害、经验、
-- 掉落、发奖、税收、灵石一类列；摆放不写任何角色数值。
-- 同一件摆件在同一洞府只占一格：unique (character_id, item_id) 是库里的硬约束，
-- 同格重摆走 on conflict do update，撤下只删这一格。
-- homestead_txn 的 txn_id 主键挡同一事务号重放（网关生成，客户端带不进来），
-- 业务拒绝整笔回滚、不占号，与 P1-1b5 的 grant_txn 同一口径。
CREATE TABLE IF NOT EXISTS "社交".homestead_placement (
    character_id bigint      NOT NULL,
    slot_id      text        NOT NULL,
    item_id      text        NOT NULL,
    txn_id       text        NOT NULL,
    placed_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, slot_id),
    CHECK (slot_id <> '' AND item_id <> '')
);

CREATE UNIQUE INDEX IF NOT EXISTS homestead_placement_item
    ON "社交".homestead_placement (character_id, item_id);

CREATE TABLE IF NOT EXISTS "社交".homestead_txn (
    txn_id       text        PRIMARY KEY,
    character_id bigint      NOT NULL,
    slot_id      text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now()
);
