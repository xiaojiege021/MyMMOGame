-- C4：强化等级跟着装备走，不跟格子。
--
-- P1-4a 的 enhance 主键是 (character_id, slot)：等级挂在格子上。格子只是物品当前
-- 的位置，同一件装备换格、卖出去再买回来、挂上拍卖又撤单，都会读到别人的等级。
-- 这里把等级改挂 "角色".inventory.id（一格一个物品实例，改 slot 不动这一行）：
--   · 换格 = update inventory.slot，等级还在这一行上，不丢；
--   · 卖出 / 成交扣光 = 删 inventory 行，等级跟着 CASCADE 走，不留给下一个进这一格的物品；
--   · 上架 / 撤单 = 背包行与等级都不动，成交时由角色服把等级交给买方那一行。
-- character_id 留着：只用于按角色汇总（战场折算与只读核对），不参与身份。
-- slot 列删掉：身份已经不是格子，留着只会让下一个读它的人再按格子写一次。
--
-- 这是分卡迁移（reapply=false），只跑一次；已应用的版本不许原地改。

ALTER TABLE "角色".enhance ADD COLUMN IF NOT EXISTS inventory_id bigint;

-- 老库回填：按迁移当时的 (character_id, slot) 找回那一行背包。
UPDATE "角色".enhance e
   SET inventory_id = i.id
  FROM "角色".inventory i
 WHERE e.inventory_id IS NULL
   AND i.character_id = e.character_id
   AND i.slot = e.slot;

-- 找不到背包行的强化行是悬空数据（装备早就不在了）。等级跟着装备，装备没了就不该留。
DELETE FROM "角色".enhance WHERE inventory_id IS NULL;

ALTER TABLE "角色".enhance ALTER COLUMN inventory_id SET NOT NULL;

-- 摘掉以「格子」为身份的旧约束：主键 (character_id, slot) 与指向 inventory(character_id, slot)
-- 的那条外键。按定义找、不按自动生成的名字找：老库上名字不一定和仓库里推的一致。
-- 指向角色表的那条外键（enhance_character_id_fkey）留着，按角色汇总还要用。
DO $$
DECLARE item record;
BEGIN
    FOR item IN
        SELECT conname FROM pg_constraint
         WHERE conrelid = '"角色".enhance'::regclass
           AND contype IN ('p', 'f')
           AND conname <> 'enhance_character_id_fkey'
    LOOP
        EXECUTE format('ALTER TABLE "角色".enhance DROP CONSTRAINT %I', item.conname);
    END LOOP;
END $$;

-- 主键换成 inventory_id：一件装备最多一条等级。
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conrelid = '"角色".enhance'::regclass AND contype = 'p'
    ) THEN
        ALTER TABLE "角色".enhance ADD CONSTRAINT enhance_pkey PRIMARY KEY (inventory_id);
    END IF;
END $$;

-- 背包行删掉，等级跟着删；不留悬空强化。
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conrelid = '"角色".enhance'::regclass AND conname = 'enhance_inventory_fkey'
    ) THEN
        ALTER TABLE "角色".enhance
            ADD CONSTRAINT enhance_inventory_fkey
            FOREIGN KEY (inventory_id) REFERENCES "角色".inventory (id) ON DELETE CASCADE;
    END IF;
END $$;

ALTER TABLE "角色".enhance DROP COLUMN IF EXISTS slot;
