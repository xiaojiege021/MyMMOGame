-- B6：觉醒。满级后开放（方案 8.2），一个角色只觉醒一次，之后不可逆、不洗点。
-- 觉醒解锁的是「本分支那一条觉醒技」（编号在 config/awaken.json 的 branch_skills），
-- 技能本体与数值仍在 config/skills.json 的 awaken=true 那一组，这里只记事实：
-- 觉醒了哪一条、什么时候。0 表示还没觉醒，所以默认值与「未觉醒」是同一个值。
ALTER TABLE "角色".character
    ADD COLUMN IF NOT EXISTS awaken_skill_id integer NOT NULL DEFAULT 0;

ALTER TABLE "角色".character
    ADD COLUMN IF NOT EXISTS awaken_at timestamptz;

ALTER TABLE "角色".character
    DROP CONSTRAINT IF EXISTS character_awaken_skill_nonnegative;
ALTER TABLE "角色".character
    ADD CONSTRAINT character_awaken_skill_nonnegative CHECK (awaken_skill_id >= 0);

-- 两列要么一起写、要么都不写：有技能编号就必须有时刻，没有编号就不能有时刻。
-- 这样「觉醒了但没记时间」「有时间但没技能」在库里结构上不成立。
ALTER TABLE "角色".character
    DROP CONSTRAINT IF EXISTS character_awaken_pair;
ALTER TABLE "角色".character
    ADD CONSTRAINT character_awaken_pair
    CHECK ((awaken_skill_id = 0) = (awaken_at IS NULL));
