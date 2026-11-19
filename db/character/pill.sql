-- B1：丹药。突破事务里要扣一枚境界丹，所以突破流水要记下「扣的是哪种丹、扣了几枚」。
--
-- 已经有 realm_breakthrough 表（P3-1a2），这里只追加两列，不动旧列、不动主键与 CHECK：
--   · pill_kind   扣的是哪种丹（config/pill.json 的编号）。老流水没有这一笔，默认空串；
--   · pill_count  这一笔扣了几枚。老流水默认 0，CHECK 只要求非负。
-- 两列都带默认值，所以老库上的历史行不用回填，也不会因为 NOT NULL 起不来。
--
-- 这是分卡迁移（reapply=false），只跑一次；主迁移 character.sql 末尾放同一份幂等 DDL，
-- 只跑主迁移的老卡也一样有这两列。已应用的版本不许原地改。

ALTER TABLE "角色".realm_breakthrough
    ADD COLUMN IF NOT EXISTS pill_kind text NOT NULL DEFAULT '';

ALTER TABLE "角色".realm_breakthrough
    ADD COLUMN IF NOT EXISTS pill_count bigint NOT NULL DEFAULT 0;

ALTER TABLE "角色".realm_breakthrough
    DROP CONSTRAINT IF EXISTS realm_breakthrough_pill_count_non_negative;
ALTER TABLE "角色".realm_breakthrough
    ADD CONSTRAINT realm_breakthrough_pill_count_non_negative CHECK (pill_count >= 0);
