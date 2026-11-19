-- C4：挂单记住挂的是哪一件物品。
--
-- P1-5a 的挂单只记 (seller_id, slot)：成交时角色服按卖方「那一格」读物品。
-- 卖方在挂单之后把那一格换成别的东西，买方就会买到没挂过的那件，强化等级也跟着串。
-- 这里记下挂单当时的物品编号，成交时由角色服核对：这一格还是挂的那件才结，
-- 不是就按 item_missing 拒（等同「挂的那件不在了」），不动买方的钱。
--
-- 分卡迁移（reapply=false），只跑一次；带默认值，老库已有的挂单补空串。
ALTER TABLE "交易".auction_listing
    ADD COLUMN IF NOT EXISTS item_id text NOT NULL DEFAULT '';
