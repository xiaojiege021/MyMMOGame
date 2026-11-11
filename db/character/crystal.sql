-- P1-4d：能量结晶镶嵌状态与事务号。只建表，不建数据库。
--
-- 结晶跟着背包那一格装备走，不放在任何进程内存里；装备被移走或角色被删掉时，
-- 这一行跟着走，不留悬空的结晶。方案 9 章写定「可拆卸复用」：拆下时把结晶
-- 放回背包，这里只删镶嵌行，不改强化等级、不改绑定、不扣金币。
-- 镶嵌位数量是配置里的待定数值（config/crystal.json 的 socket_count），
-- 由角色服校验，库层只挡非正编号，不在 SQL 里写第二套数字。
CREATE TABLE IF NOT EXISTS "角色".crystal_socket (
    character_id    bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    slot            smallint    NOT NULL CHECK (slot BETWEEN 1 AND 200),
    socket          smallint    NOT NULL CHECK (socket > 0),
    crystal_item_id text        NOT NULL,
    -- 装上时把这一格的绑定状态一起记下，拆下时原样还回背包，不让绑定凭空消失。
    bound           boolean     NOT NULL DEFAULT false,
    bound_at        timestamptz,
    installed_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, slot, socket),
    FOREIGN KEY (character_id, slot)
        REFERENCES "角色".inventory (character_id, slot) ON DELETE CASCADE
);

ALTER TABLE "角色".crystal_socket
    DROP CONSTRAINT IF EXISTS crystal_socket_bound_at_matches;
ALTER TABLE "角色".crystal_socket
    ADD CONSTRAINT crystal_socket_bound_at_matches
    CHECK ((bound AND bound_at IS NOT NULL) OR (NOT bound AND bound_at IS NULL));

-- 装上 / 拆下都要抢一个号：同一个号再点一次，一笔不做。
-- 号和强化、转移各自分开，避免一条链路的重放把另一条也挡掉。
CREATE TABLE IF NOT EXISTS "角色".crystal_txn (
    txn_id       text        PRIMARY KEY,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    slot         smallint    NOT NULL,
    socket       smallint    NOT NULL,
    action       text        NOT NULL,
    applied_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS crystal_txn_character
    ON "角色".crystal_txn (character_id, applied_at DESC);
