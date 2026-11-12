-- P1-5d：内部充值订单。P0 不接真实支付渠道，订单与回调都在自己这边。
-- 一笔订单一行：order_id 主键保证同一笔回调重复到达时只发一次；status 只有
-- pending / paid 两态，paid_at 与 status 必须一起对得上。
CREATE TABLE IF NOT EXISTS "角色".recharge_order (
    order_id     text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    product_id   text        NOT NULL,
    yuanbao      bigint      NOT NULL CHECK (yuanbao > 0),
    status       text        NOT NULL CHECK (status IN ('pending', 'paid')),
    created_at   timestamptz NOT NULL DEFAULT now(),
    paid_at      timestamptz
);

ALTER TABLE "角色".recharge_order
    DROP CONSTRAINT IF EXISTS recharge_order_paid_at_matches;
ALTER TABLE "角色".recharge_order
    ADD CONSTRAINT recharge_order_paid_at_matches
    CHECK ((status = 'paid' AND paid_at IS NOT NULL) OR (status = 'pending' AND paid_at IS NULL));

CREATE INDEX IF NOT EXISTS recharge_order_character
    ON "角色".recharge_order (character_id, created_at);
