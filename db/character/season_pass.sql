-- P3-4a2：赛季通行证。两轨的奖励不单独存表，只存「这一笔领了没有」与「付费轨开没开」：
-- 奖励物品、数量与绑定都从 config/season_pass.json 与 items.json 读，库里只记事实值。
-- 买与领各一笔事务：扣元宝 / 入包、写这两张表、抢事务号全在一起，缺一样整笔退回。
-- 同一份 DDL 整份追加进 db/character/character.sql 主迁移，让只跑主迁移的核对脚本自包含。
CREATE TABLE IF NOT EXISTS "角色".season_pass (
    character_id  bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    season_id     text        NOT NULL,
    -- 付费轨是否已开。买下即 true；免费轨不看这一行。
    paid          boolean     NOT NULL DEFAULT true,
    -- 这一笔真扣掉的元宝（来自 config/shop.json 那条 season_pass 商品的标价）。
    price_yuanbao bigint      NOT NULL CHECK (price_yuanbao >= 0),
    txn_id        text        NOT NULL,
    purchased_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, season_id)
);

CREATE TABLE IF NOT EXISTS "角色".season_pass_claim (
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    season_id    text        NOT NULL,
    -- free 或 paid。方案 12.1 只有这两轨。
    track        text        NOT NULL CHECK (track IN ('free', 'paid')),
    stage        int         NOT NULL CHECK (stage >= 1),
    item_id      text        NOT NULL,
    item_count   int         NOT NULL CHECK (item_count >= 1),
    txn_id       text        NOT NULL,
    claimed_at   timestamptz NOT NULL DEFAULT now(),
    -- 一阶只发一条（协议一回一条事实值），所以这一主键就是「这一季这一轨这一阶领过没有」。
    PRIMARY KEY (character_id, season_id, track, stage)
);
