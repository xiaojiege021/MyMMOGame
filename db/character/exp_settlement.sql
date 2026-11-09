-- P1-2d：等级经验的结算流水。活动、副本、任务、竞技场、战场、城战和世界 Boss
-- 都走这一笔，txn_id 是幂等号：同一个号再提交不发第二次。
-- 这一笔只加经验，不写货币、背包、疲劳、周份额与进本记录。
CREATE TABLE IF NOT EXISTS "角色".exp_settlement (
    txn_id        text        PRIMARY KEY,
    character_id  bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    source        text        NOT NULL,
    party_size    integer     NOT NULL CHECK (party_size >= 1),
    base_exp      bigint      NOT NULL CHECK (base_exp >= 0),
    party_bonus   bigint      NOT NULL CHECK (party_bonus >= 0),
    exp_gain      bigint      NOT NULL CHECK (exp_gain >= 0),
    level_before  integer     NOT NULL,
    level_after   integer     NOT NULL,
    settled_at    timestamptz NOT NULL DEFAULT now()
);
