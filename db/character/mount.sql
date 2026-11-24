-- B4：玩法坐骑。一个人同一时间只骑一只，骑着的就是背包里的那一行。
--
-- 为什么仍是「指向 inventory.id」而不是给 inventory 加一列 equipped_mount：
--   与 B9 的 character_equip 同一个理由——格会换（移动 / 排序），身份是背包行；
--   卖掉 / 丢弃 / 合并删行时这条记录跟着 CASCADE 走，不需要每个写入点各记一次。
--
-- 速度不落库：方案 9.4 只写「普通和高级只差移动速度」，档位与 speed_bps 都在
-- config/mount.json，读的时候按 inventory → items(kind=mount) → mount.json 现算。
-- 这里只存「骑的是哪一行」，不存第二份速度数字，也不存档位。
--
-- 灵兽没有表：方案 9.4 只要求收集 / 外观 / 生活协助，收集口径与外观收藏一致
-- （背包里现在持有就算），所以不建第二张收集表。
--
-- 幂等：重复跑主迁移不报错。同一份 DDL 也追加在 db/character/character.sql
-- （角色主迁移，老卡只跑主迁移也要建得出来）。

CREATE TABLE IF NOT EXISTS "角色".character_mount (
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    -- 骑在身上的那一行背包。同一行只能被一个角色骑着，一个角色只能骑一行。
    inventory_id bigint      NOT NULL REFERENCES "角色".inventory (id) ON DELETE CASCADE,
    mounted_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id),
    UNIQUE (inventory_id)
);
