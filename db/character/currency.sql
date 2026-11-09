-- P1-1b4：货币表。只建表，不建数据库。
--
-- 一个角色一个币种一行，没有的行按 0 算。amount 不允许为负：接口层先挡，
-- 库里这道 CHECK 是最后一道闸，绕过接口也写不进负数。
-- 灵石五档在这里是五种币种，相邻档 10000:1 的兑换在 P3-2a，这一行只记账。
CREATE TABLE IF NOT EXISTS "角色".currency (
    character_id bigint   NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    kind         text     NOT NULL,
    amount       bigint   NOT NULL DEFAULT 0 CHECK (amount >= 0),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, kind)
);
