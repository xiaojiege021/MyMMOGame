-- P1-1b5：发奖事务表。只建表，不建数据库。
--
-- 事务号是主键：同一个号第二次提交会撞主键，整笔不做。
-- 记下角色与落账时刻，便于排查「这笔奖是谁发的、什么时候落的」。
CREATE TABLE IF NOT EXISTS "角色".grant_txn (
    txn_id       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    applied_at   timestamptz NOT NULL DEFAULT now()
);

-- 查某个角色发过哪些奖。
CREATE INDEX IF NOT EXISTS grant_txn_character
    ON "角色".grant_txn (character_id, applied_at DESC);
