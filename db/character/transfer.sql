-- P1-4c：强化转移事务号。只建表，不建数据库。
--
-- 强化等级本身仍存在 "角色".enhance 里（按角色 + 格位一行），目标格的绑定状态
-- 仍在 "角色".inventory 的 bound / bound_at 里。转移不另存一份等级，也不另存一份绑定。
-- 事务号只由网关生成一次：扣转移石、扣金币、改两格等级、必要时绑定目标格
-- 都在同一笔事务里，主键挡重放，同一个号再提交不会扣第二次材料。
CREATE TABLE IF NOT EXISTS "角色".transfer_txn (
    txn_id       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    from_slot    smallint    NOT NULL,
    to_slot      smallint    NOT NULL,
    applied_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS transfer_txn_character
    ON "角色".transfer_txn (character_id, applied_at DESC);
