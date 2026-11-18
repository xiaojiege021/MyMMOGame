-- Q6/J4：赛季通行证进度。点数不是货币，单独两张表：
--   season_pass_points       这一季这个角色攒了多少点（一行，PK 挡重复行）
--   season_pass_point_ledger 每笔加点一行（txn_id 主键就是幂等）
-- 发什么奖励仍只在 config/season_pass.json 与 season_pass_claim，不在这里。
-- 同一份 DDL 整份追加进 db/character/character.sql 主迁移，让只跑主迁移的核对脚本自包含。
CREATE TABLE IF NOT EXISTS "角色".season_pass_points (
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    season_id    text        NOT NULL,
    -- 本季累计点数。只增不减；换季按 season_id 重新从 0 起，不删旧行。
    points       bigint      NOT NULL DEFAULT 0 CHECK (points >= 0),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, season_id)
);

CREATE TABLE IF NOT EXISTS "角色".season_pass_point_ledger (
    -- 同一笔重放就撞这个主键：点数不二次加。
    txn_id       text        NOT NULL PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    season_id    text        NOT NULL,
    source_id    text        NOT NULL,
    kind         text        NOT NULL CHECK (kind IN ('dungeon_clear', 'mainline_stage')),
    -- daily / weekly 是业务日 / 业务周首；once 固定写 once（跨季也只给一次）。
    period_key   text        NOT NULL,
    -- 副本编号或主线场次。只作核对，不参与判定。
    ref_id       text        NOT NULL DEFAULT '',
    points       int         NOT NULL CHECK (points > 0),
    awarded_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS season_pass_point_ledger_period
    ON "角色".season_pass_point_ledger (character_id, season_id, source_id, period_key);

CREATE INDEX IF NOT EXISTS season_pass_point_ledger_once
    ON "角色".season_pass_point_ledger (character_id, source_id);
