-- P3-3a4：城战胜负写回流水。只记这一场由服务端确认过的胜方事实值，
-- 不记税率之外的任何数值，也不发奖。city 表的 owner_sect / tax_pct 由同一笔事务更新。
-- 同一份 DDL 整份追加进 db/character/character.sql 主迁移，让只跑主迁移的核对脚本自包含。
CREATE TABLE IF NOT EXISTS "社交".siege_result (
    txn_id       text        PRIMARY KEY,
    city_id      text        NOT NULL REFERENCES "社交".city (city_id) ON DELETE CASCADE,
    match_id     text        NOT NULL,
    winner_team  int         NOT NULL CHECK (winner_team BETWEEN 0 AND 1),
    winner_sect  text        NOT NULL,
    tax_pct      int         NOT NULL CHECK (tax_pct BETWEEN 0 AND 100),
    recorded_at  timestamptz NOT NULL DEFAULT now()
);
