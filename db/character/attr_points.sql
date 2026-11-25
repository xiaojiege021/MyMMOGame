-- A8：大圆满可分配属性点（版本 37）。点数只在角色服算：走到「大圆满」这一阶时，
-- 按 config/realms.json 的 sub_phases.by_realm[境界].grand_points 发一次，
-- 按大境界累计（筑基一段一次、结丹一段一次……），炼体末阶是九阶，不发。
-- 这一列是「手上还有多少点」：本卡只加，A9 的加点从这里减。
ALTER TABLE "角色".character
    ADD COLUMN IF NOT EXISTS attr_points integer NOT NULL DEFAULT 0;

-- 减到负数就是算错了（A9 每次加点的扣减也受这条约束）。
ALTER TABLE "角色".character
    DROP CONSTRAINT IF EXISTS character_attr_points_range;
ALTER TABLE "角色".character
    ADD CONSTRAINT character_attr_points_range CHECK (attr_points >= 0);

-- 一段大圆满一行。主键 (character_id, realm) 就是「一段只发一次」的落法：
-- 同一段再走一次升阶、或者同一个事务号重放，都不会发第二次。
-- 与升阶台账 realm_phase_promote 的 txn_id 幂等是两道不同的闸，两道都要。
CREATE TABLE IF NOT EXISTS "角色".realm_phase_points (
    txn_id       text        NOT NULL PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    -- 发点时的境界（realms.json 顺序表里的规范名，不写别名）。
    realm        text        NOT NULL CHECK (realm <> ''),
    -- 这一段发了多少点（配置发点那一刻的现值；改配置不回改历史账）。
    points       integer     NOT NULL CHECK (points > 0),
    granted_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT realm_phase_points_once UNIQUE (character_id, realm)
);
