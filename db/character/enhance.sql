-- P1-4a：装备强化等级与强化事务号。只建表，不建数据库。
--
-- 强化等级是角色持久数据，跟着背包那一格走，不放在任何进程内存里。
-- 一格没强化过就没有这一行，按 +0 算；外键指向 inventory 的 (character_id, slot)，
-- 装备被移走或角色被删掉时这一行跟着走，不留悬空的强化等级。
-- 库层 CHECK 是最后一道闸：方案 9.2 写死上限 +12，服务端的 max_level 也从配置校验成 12。
CREATE TABLE IF NOT EXISTS "角色".enhance (
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    slot         smallint    NOT NULL CHECK (slot BETWEEN 1 AND 200),
    level        smallint    NOT NULL DEFAULT 0 CHECK (level BETWEEN 0 AND 12),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, slot),
    FOREIGN KEY (character_id, slot)
        REFERENCES "角色".inventory (character_id, slot) ON DELETE CASCADE
);

-- 强化扣石头、扣金币、改等级是同一笔事务，事务号只由网关生成一次。
-- 主键挡重放：同一个号再点一次不会扣第二次材料，也不会多算一次随机。
CREATE TABLE IF NOT EXISTS "角色".enhance_txn (
    txn_id       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    slot         smallint    NOT NULL,
    applied_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS enhance_txn_character
    ON "角色".enhance_txn (character_id, applied_at DESC);
