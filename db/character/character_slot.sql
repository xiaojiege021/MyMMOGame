-- P3-4b：第二角色栏。一个账号一行，只存「这名账号最多能有几个角色」这一个事实值。
--
-- 为什么不是「额外几栏」：基础位是配置（config/character_slot.json 的 base_slots），
-- 库里存总名额就能让「买到第二栏」与「核对种子给 8 个位」用同一种写法，
-- 判定时也不必在两处各算一次加法。没有这一行就按 base_slots 算，所以老账号不受影响。
--
-- 只存名额，不存装备 / 等级 / 境界 / 外观：那些都按 character_id 各存一行（P1-1b1 起），
-- 「不共享」是表结构本身的结果，不靠这一张表守。价格也不在这里：标价只从
-- config/shop.json 那条 second_slot 商品读，这里记的 price_yuanbao 是成交时的那个数，
-- 只为流水可回溯，不是价格正本。
--
-- CHECK (slots BETWEEN 1 AND 16)：上限 16 只是防呆（配置里 max_slots 是 2），
-- 真正的上限由 config/character_slot.json 的 max_slots 在购买时判。
CREATE TABLE IF NOT EXISTS "角色".character_slot (
    account       text        PRIMARY KEY REFERENCES "角色".account (account) ON DELETE CASCADE,
    slots         integer     NOT NULL CHECK (slots BETWEEN 1 AND 16),
    price_yuanbao bigint      NOT NULL CHECK (price_yuanbao >= 0),
    txn_id        text        NOT NULL,
    updated_at    timestamptz NOT NULL DEFAULT now()
);
