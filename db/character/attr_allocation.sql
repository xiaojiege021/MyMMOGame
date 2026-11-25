-- A9：属性加点（版本 38）。A8 发下来的 attr_points 在本卡减：每次加点从
-- character.attr_points 扣、加到下面这张表的四个属性列上。四属性的键与职业基础值
-- 的正本仍是 config/classes.json；单属性上限与「不做免费洗点」在 config/attr_points.json。
-- 本版没有洗点：表里只有加，没有任何一列记「重置」，洗点要开新卡改表。
CREATE TABLE IF NOT EXISTS "角色".character_attr_points (
    character_id bigint      NOT NULL PRIMARY KEY REFERENCES "角色".character (id) ON DELETE CASCADE,
    strength     integer     NOT NULL DEFAULT 0,
    agility      integer     NOT NULL DEFAULT 0,
    spirit       integer     NOT NULL DEFAULT 0,
    vitality     integer     NOT NULL DEFAULT 0,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    -- 四列都是「手动加了多少点」，减到负数就是算错了。
    CONSTRAINT character_alloc_range CHECK (
        strength >= 0 AND agility >= 0 AND spirit >= 0 AND vitality >= 0)
);

-- 一笔加点一行。txn_id 是主键，就是「同一个事务号只加一次」的落法；
-- attribute 只认四个键（与仓储白名单同一套），写错键在库层也挡一下。
CREATE TABLE IF NOT EXISTS "角色".character_attr_ledger (
    txn_id          text        NOT NULL PRIMARY KEY,
    character_id    bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    attribute       text        NOT NULL,
    points          integer     NOT NULL CHECK (points > 0),
    -- 这一笔做完之后手上还剩多少点（事实值，不是推算；对账时与 character.attr_points 比）。
    remaining_after integer     NOT NULL CHECK (remaining_after >= 0),
    created_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT character_attr_ledger_attribute CHECK (
        attribute IN ('strength', 'agility', 'spirit', 'vitality'))
);

CREATE INDEX IF NOT EXISTS character_attr_ledger_character
    ON "角色".character_attr_ledger (character_id);
