-- P2-1a2：宗门职位席位。给 "社交".sect_member 加一列 position，
-- 默认 disciple（无席位上限的默认职位），取值只能是五个职位名之一。
-- 幂等：ADD COLUMN IF NOT EXISTS + DROP/ADD CONSTRAINT，重复跑主迁移不报错。
-- 同一份 DDL 追加进 db/social/social.sql 主迁移（老卡只跑主迁移时不缺列）。
-- 人数上限、create_txn 等 P2-1a1 的表结构不动。
ALTER TABLE "社交".sect_member
    ADD COLUMN IF NOT EXISTS position text NOT NULL DEFAULT 'disciple';

ALTER TABLE "社交".sect_member
    DROP CONSTRAINT IF EXISTS sect_member_position_chk;
ALTER TABLE "社交".sect_member
    ADD CONSTRAINT sect_member_position_chk
    CHECK (position IN ('master', 'vice_master', 'elder', 'steward', 'disciple'));
