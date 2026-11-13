-- P2-1a4：升级只耗建设。给 "社交".sect 加「宗门建设」与「升级幂等事务号」两列，
-- 另建 sect_upgrade（txn_id 主键）流水挡同一事务号重放。
-- 幂等：ADD COLUMN IF NOT EXISTS / DROP + ADD CONSTRAINT / CREATE TABLE IF NOT EXISTS，
-- 重复跑主迁移不报错。a1 两表、a2 的 sect_member.position、a3 的
-- master_character_id / last_transfer_txn / sect_transfer 结构都不动；不碰角色 schema。
--
-- 建设只从宗门秘境和宗门任务来、不从商城买（方案 11.1 / 开发架构 4.4）；本卡不做来源。
-- 升级一个字不扣角色货币：这里不建外键指向角色 schema，社交服不写角色库。
ALTER TABLE "社交".sect
    ADD COLUMN IF NOT EXISTS construction bigint NOT NULL DEFAULT 0;

ALTER TABLE "社交".sect
    DROP CONSTRAINT IF EXISTS sect_construction_chk;
ALTER TABLE "社交".sect
    ADD CONSTRAINT sect_construction_chk CHECK (construction >= 0);

ALTER TABLE "社交".sect
    ADD COLUMN IF NOT EXISTS last_upgrade_txn text;

-- 升级流水：txn_id 主键挡重放；只写社交 schema，不建外键指向角色 schema。
-- from_level / to_level / cost 都是这一笔的只读事实值，请求里带不进来。
CREATE TABLE IF NOT EXISTS "社交".sect_upgrade (
    txn_id      text        PRIMARY KEY,
    sect_id     bigint      NOT NULL REFERENCES "社交".sect (id) ON DELETE CASCADE,
    from_level  int         NOT NULL,
    to_level    int         NOT NULL,
    cost        bigint      NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);
