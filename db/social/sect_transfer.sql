-- P2-1a3：传位只给副掌门。给 "社交".sect 加「当前掌门」与「幂等事务号」两列，
-- 另建传位流水表 sect_transfer（txn_id 主键）挡同一事务号重放。
-- 幂等：ADD COLUMN IF NOT EXISTS / CREATE TABLE IF NOT EXISTS / UPDATE ... WHERE IS NULL
-- / ALTER COLUMN SET NOT NULL，重复跑主迁移不报错。
-- a1 的两张表结构与 a2 的 sect_member.position 列结构都不动；不碰角色 schema。
--
-- master_character_id 是当前掌门的唯一权威（创建时 = founder_character_id）；
-- founder_character_id 只作创建历史，不再参与权限判定。并发两笔传位靠
-- select ... for update 锁同一宗门行串行，第二笔看到掌门已变就拒绝。
ALTER TABLE "社交".sect
    ADD COLUMN IF NOT EXISTS master_character_id bigint;
ALTER TABLE "社交".sect
    ADD COLUMN IF NOT EXISTS last_transfer_txn text;

-- 迁移前已建的宗门：当前掌门回填为创建者（创建时即现任掌门）。
UPDATE "社交".sect
    SET master_character_id = founder_character_id
    WHERE master_character_id IS NULL;

ALTER TABLE "社交".sect
    ALTER COLUMN master_character_id SET NOT NULL;

-- 传位流水：txn_id 主键挡重放；只写社交 schema，不建外键指向角色 schema。
CREATE TABLE IF NOT EXISTS "社交".sect_transfer (
    txn_id      text        PRIMARY KEY,
    sect_id     bigint      NOT NULL REFERENCES "社交".sect (id) ON DELETE CASCADE,
    old_master  bigint      NOT NULL,
    new_master  bigint      NOT NULL,
    at          timestamptz NOT NULL DEFAULT now()
);
