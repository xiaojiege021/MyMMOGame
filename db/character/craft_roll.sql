-- B3：制作掷点落库。只建表 / 只加列，不建数据库。
--
-- 方案 9.1：胚子没有战斗属性，按配方做成装备的那一次服务端掷点，掷定后写在这件
-- 装备上成为它的基础属性；后续强化按这个基础算，转移只转强化等级、结晶不改它们。
-- 所以掷点值必须跟着「那一件」走，落点只能是背包行（inventory.id），不是物品编号。
--
-- 两列都是 NULL 表示「不是制作产出的装备」：掉落成品、商店、任务、GM 发的都按
-- config/items.json 的 attack 算（B2 口径）。两列同生同灭，免得出现「有主属性没附加」
-- 这种半截状态。附加属性用 jsonb 存 [{attr,value}, ...]，种类与区间只在
-- config/craft_roll.json，这里不存第二份数值，也不存能算回来的概率。
ALTER TABLE "角色".inventory
    ADD COLUMN IF NOT EXISTS craft_attr integer;

ALTER TABLE "角色".inventory
    ADD COLUMN IF NOT EXISTS craft_extras jsonb;

ALTER TABLE "角色".inventory
    DROP CONSTRAINT IF EXISTS inventory_craft_roll_paired;
ALTER TABLE "角色".inventory
    ADD CONSTRAINT inventory_craft_roll_paired
    CHECK ((craft_attr IS NULL AND craft_extras IS NULL)
        OR (craft_attr IS NOT NULL AND craft_extras IS NOT NULL));

ALTER TABLE "角色".inventory
    DROP CONSTRAINT IF EXISTS inventory_craft_attr_positive;
ALTER TABLE "角色".inventory
    ADD CONSTRAINT inventory_craft_attr_positive
    CHECK (craft_attr IS NULL OR craft_attr > 0);

-- 熟练度：每个角色每个副职一条。方案 9.1 只写「制作技能只提高高值权重」，
-- 没给副职等级与经验曲线，所以这里不做等级，只记两次计数：
-- craft_count 是动手次数（成与不成都算，做成那一手就 +1）、success_count 是成了几次。
-- 选权重档的输入是 craft_count，档位在 config/craft_roll.json 的 skill_tiers。
-- 只增不减：没有洗点、没有降级、没有清零入口。
CREATE TABLE IF NOT EXISTS "角色".character_profession (
    character_id  bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    profession    text        NOT NULL CHECK (profession <> ''),
    craft_count   integer     NOT NULL DEFAULT 0 CHECK (craft_count >= 0),
    success_count integer     NOT NULL DEFAULT 0 CHECK (success_count >= 0),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, profession),
    CONSTRAINT character_profession_success_le_craft CHECK (success_count <= craft_count)
);
