-- N10：段内阶。方案 8.1 每一段分初期、中期、后期、大圆满；后期才允许突破下一段大境界。
-- R1：炼体 1–9 阶，筑基起才是四阶；realm_phase 上限按最大 9 阶放宽到 8。
-- 被动百分比只记在 config/realms.json，角色服按境界 + 段内阶算，客户端不算。
ALTER TABLE "角色".character
    ADD COLUMN IF NOT EXISTS realm_phase smallint NOT NULL DEFAULT 0;

ALTER TABLE "角色".character
    DROP CONSTRAINT IF EXISTS character_realm_phase_range;
ALTER TABLE "角色".character
    ADD CONSTRAINT character_realm_phase_range CHECK (realm_phase >= 0 AND realm_phase <= 8);

-- 段内升阶幂等：一个事务号只升一阶。
CREATE TABLE IF NOT EXISTS "角色".realm_phase_promote (
    txn_id       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    from_phase   smallint    NOT NULL CHECK (from_phase >= 0 AND from_phase <= 8),
    to_phase     smallint    NOT NULL CHECK (to_phase >= 1 AND to_phase <= 8),
    promoted_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS realm_phase_promote_character
    ON "角色".realm_phase_promote (character_id, promoted_at DESC);