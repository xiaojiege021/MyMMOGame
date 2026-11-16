-- N11 家园种植：只记「谁的哪一格种了哪一作物、何时种下」，产出家园币经角色服
-- ApplyGrant 写入角色 schema（技术架构 6 节货币在角色服）。社交服不写 home_coin 余额。
CREATE TABLE IF NOT EXISTS "社交".homestead_plant (
    character_id bigint      NOT NULL,
    slot_id      text        NOT NULL,
    crop_id      text        NOT NULL,
    planted_at   timestamptz NOT NULL DEFAULT now(),
    plant_txn_id text        NOT NULL,
    PRIMARY KEY (character_id, slot_id),
    CHECK (slot_id <> '' AND crop_id <> '')
);

CREATE TABLE IF NOT EXISTS "社交".homestead_plant_txn (
    txn_id       text        PRIMARY KEY,
    character_id bigint      NOT NULL,
    action       text        NOT NULL,
    slot_id      text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CHECK (action IN ('plant', 'harvest'))
);