-- B9：装备穿戴。一个人每个部位最多穿一件，穿着的就是背包里的那一行。
--
-- 为什么不是给 inventory 加一列 equipped_slot：
--   · 一格一个物品实例，穿戴是「这一行现在穿在哪」，不是物品本身的属性；
--     加列会让「脱下」变成改物品行，卖出 / 移动 / 排序都要多照顾一列。
--   · 单独一张表把「同部位一件」交给主键，把「穿着的行被删掉」交给外键：
--     卖出、丢弃、合并删行时这条记录跟着 CASCADE 走，不需要每个写入点各记一次。
--
-- 指向 inventory(id) 而不是 (character_id, slot)：格子会换（Q10 的移动 / 排序），
-- 身份是背包行。强化（C4 的 enhance）与结晶（crystal_socket）也挂在这一行上，
-- 所以「换格不丢强化」与「卖掉不留强化」两件事在穿戴状态下同样成立。
--
-- 幂等：重复跑主迁移不报错。同一份 DDL 也追加在 db/character/character.sql
-- （角色主迁移，老卡只跑主迁移也要建得出来）。

CREATE TABLE IF NOT EXISTS "角色".character_equip (
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    -- 三个部位：weapon / armor / accessory。取值与 config/items.json 的 slot 一致。
    slot         text        NOT NULL CHECK (slot IN ('weapon', 'armor', 'accessory')),
    -- 穿在身上的那一行背包。同一个人同一行只能穿一次，同一部位只能一件。
    inventory_id bigint      NOT NULL REFERENCES "角色".inventory (id) ON DELETE CASCADE,
    equipped_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, slot),
    UNIQUE (inventory_id)
);

-- 脱下 / 换部位时按角色读全部，一行一条。
CREATE INDEX IF NOT EXISTS character_equip_character
    ON "角色".character_equip (character_id);
