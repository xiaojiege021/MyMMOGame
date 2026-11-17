-- K3 生产 / 配方：进行中的一件制作与每一次制作的流水。只建表，不建数据库。
--
-- 制作的持久事实只有两样：谁手上有一件正在做、以及每一次动手成没成。
-- 材料与费用在 start 那一笔事务里就扣掉（和强化 / 转移同口径：扣减与写入同一条事务，
-- 中间失败整笔回滚，不留下「扣了材料没活」）；ready_at 到点之前 finish 只能被拒，
-- 不会提前掷成功率。成与不成各写一行 craft_log，方案 12 章的口径是「这次成没成写流水」。
--
-- 配方本身（材料 / 产出 / 费用 / 成功率 / 耗时 / 解锁）只在 config/recipes.json；
-- 这里只存「哪一条配方」与结果，不存第二份数值。成功率的掷点只在角色服、只在 finish 那一手。
CREATE TABLE IF NOT EXISTS "角色".craft_log (
    txn_id         text        PRIMARY KEY,
    character_id   bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    action         text        NOT NULL CHECK (action IN ('start', 'finish')),
    recipe_id      text        NOT NULL CHECK (recipe_id <> ''),
    profession     text        NOT NULL CHECK (profession <> ''),
    success        boolean     NOT NULL DEFAULT false,
    output_item_id text        NOT NULL DEFAULT '',
    output_count   integer     NOT NULL DEFAULT 0,
    created_at     timestamptz NOT NULL DEFAULT now(),
    CHECK (output_count >= 0),
    -- 没成的那一笔必须没有产出；成了的那一笔必须有产出。
    -- start 那行也落在「没成」这一支：它只是抢号，不代表这一次制作失败。
    CHECK ((success AND output_item_id <> '' AND output_count > 0)
        OR (NOT success AND output_item_id = '' AND output_count = 0))
);

CREATE INDEX IF NOT EXISTS craft_log_character
    ON "角色".craft_log (character_id, created_at DESC);

CREATE TABLE IF NOT EXISTS "角色".craft_job (
    character_id bigint      PRIMARY KEY REFERENCES "角色".character (id) ON DELETE CASCADE,
    recipe_id    text        NOT NULL,
    start_txn_id text        NOT NULL,
    started_at   timestamptz NOT NULL DEFAULT now(),
    ready_at     timestamptz NOT NULL,
    CHECK (recipe_id <> ''),
    CHECK (start_txn_id <> ''),
    CHECK (ready_at >= started_at),
    -- 一个角色同时只做一件：主键就是「一件活」的约束，第二条 start 会被它挡住。
    -- start 那一行流水也由这里回指，删角色时两张表一起走。
    FOREIGN KEY (start_txn_id) REFERENCES "角色".craft_log (txn_id) ON DELETE CASCADE
);
