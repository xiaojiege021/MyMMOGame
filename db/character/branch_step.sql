-- P2-4a：二转步数。0 未转、1 一转（15 级选了分支）、2 二转（45 级把这条分支往前推了一步）。
-- 分支本身就存在 character.branch 里，这里只多一列步数：45 级后不能改去另一条分支，
-- 靠「目标分支必须等于已经写下的那条」判定，不需要第二份分支表。
ALTER TABLE "角色".character
    ADD COLUMN IF NOT EXISTS branch_step integer NOT NULL DEFAULT 0;

ALTER TABLE "角色".character
    DROP CONSTRAINT IF EXISTS character_branch_step_range;
ALTER TABLE "角色".character
    ADD CONSTRAINT character_branch_step_range CHECK (branch_step BETWEEN 0 AND 2);

-- 建号时就写了分支的（P1-1b1 允许）按一转算，别让它们停在 0 又去走一遍一转。
UPDATE "角色".character
   SET branch_step = 1
 WHERE branch IS NOT NULL AND branch <> '' AND branch_step = 0;
