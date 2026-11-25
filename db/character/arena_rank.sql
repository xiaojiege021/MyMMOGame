-- B11：竞技场赛季段位。段位分只认「竞技场一场通关」这一个服务端事实，按季各存一行。
-- 档位阈值、每胜加分与专属外观编号都在 config/arena_rank.json；本表只记账：
-- 这一季这个人多少分、当前哪一档、赢了几场。段位不改竞技场模板属性（P2-3a 一字不变）。
CREATE TABLE IF NOT EXISTS "角色".arena_rank (
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    -- 季编号来自全局服务的当前季，角色服不自己认定；换季后按新季另起一行，旧行不删。
    season_id    text        NOT NULL CHECK (season_id <> ''),
    score        bigint      NOT NULL DEFAULT 0 CHECK (score >= 0),
    -- 当前档位编号（config/arena_rank.json 的 tiers[].key）。
    tier_key     text        NOT NULL CHECK (tier_key <> ''),
    wins         integer     NOT NULL DEFAULT 0 CHECK (wins >= 0),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, season_id)
);

-- 排行按「这一季分数从高到低」取前 N；同分按角色号定序，回包才稳定。
CREATE INDEX IF NOT EXISTS arena_rank_season_score
    ON "角色".arena_rank (season_id, score DESC, character_id);

-- 每场一次加分行。txn_id 主键就是幂等：战斗服同一个结算号重放不会加两次分。
CREATE TABLE IF NOT EXISTS "角色".arena_rank_ledger (
    txn_id       text        NOT NULL PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    season_id    text        NOT NULL CHECK (season_id <> ''),
    -- 竞技场模式（config/arena_match.json 的 modes[].id），只作核对，不参与判定。
    mode         text        NOT NULL CHECK (mode <> ''),
    -- 这一场加了多少分、加完是多少。分数只增不减：方案没写失败惩罚，这里不自编。
    score_delta  integer     NOT NULL CHECK (score_delta > 0),
    score_after  bigint      NOT NULL CHECK (score_after >= 0),
    tier_key     text        NOT NULL CHECK (tier_key <> ''),
    awarded_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS arena_rank_ledger_season
    ON "角色".arena_rank_ledger (character_id, season_id);

-- 段位专属外观：这一季这一档领过一次就一行。物品编号从配置读，领到手进背包（绑定）。
-- 一档一件、每季只领一次：主键就是这条规则的落法。
CREATE TABLE IF NOT EXISTS "角色".arena_rank_appearance (
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    season_id    text        NOT NULL CHECK (season_id <> ''),
    tier_key     text        NOT NULL CHECK (tier_key <> ''),
    item_id      text        NOT NULL CHECK (item_id <> ''),
    txn_id       text        NOT NULL,
    claimed_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, season_id, tier_key)
);
