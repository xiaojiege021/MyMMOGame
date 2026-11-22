-- B7+B8：背包扩容落角色（character 版本 32，只加列，不建表）。
--
-- 方案 12.1「背包与仓库：背包扩容、仓库扩容。只加格子」。
-- 扩容次数记在角色行上：容量 = min(bag_slots + bag_expand_count × bag_expand_step,
-- bag_slots_max)，三个数都只从 config/character.json 读，库里不钉方案现值
-- （与 fatigue 上限同口径：库层只守非负，改配置不用改约束）。
--
-- 为什么不需要「已扩容格数」这一列：格数由基础值加次数算出来，落两个数就有了第二份
-- 事实，改配置时会打架。这一列只记买过几次，老角色默认 0，容量与迁移前一致。
ALTER TABLE "角色".character
    ADD COLUMN IF NOT EXISTS bag_expand_count integer NOT NULL DEFAULT 0;

ALTER TABLE "角色".character
    DROP CONSTRAINT IF EXISTS character_bag_expand_count_non_negative;
ALTER TABLE "角色".character
    ADD CONSTRAINT character_bag_expand_count_non_negative CHECK (bag_expand_count >= 0);

-- 只作最后一道闸，挡荒谬值；真实上限是 config 里的 bag_slots_max。
ALTER TABLE "角色".character
    DROP CONSTRAINT IF EXISTS character_bag_expand_count_sane;
ALTER TABLE "角色".character
    ADD CONSTRAINT character_bag_expand_count_sane CHECK (bag_expand_count <= 1000);
