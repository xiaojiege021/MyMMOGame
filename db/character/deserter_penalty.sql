-- Q9/H4：逃跑惩罚。一个人在一场里主动退（Close 帧）或重连窗口到点没回来，
-- 角色服在这里记一行到期时刻；下一次进本被角色服与战斗服两处挡下。
--
-- 这不是「惩罚流水」也不是货币：不改金币、经验、疲劳、周份额与任何进本记录，
-- 只回答一句「还要等多久」。同一个人重复判罚取较晚的那一个（不叠加、不乘以次数）。
--
-- 时长不在这里：战斗服按 config/reconnect.json 的 deserter_penalty_s 算成毫秒传进来，
-- 角色服按自己的时钟加出来（不传绝对时刻，两台机时钟不一致也不会算错）。
--
-- 幂等：重复跑主迁移不报错（CREATE TABLE IF NOT EXISTS）。同一份 DDL 也追加在
-- db/character/character.sql（角色主迁移，老卡只跑主迁移也要建得出来）。

CREATE TABLE IF NOT EXISTS "角色".deserter_penalty (
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    -- 到期时刻（Unix 毫秒）。这张表的所有判定都只比这一个数。
    until_ms     bigint      NOT NULL,
    -- 判出这件事的那一场与原因，只用于追查，不参与判定。
    session_id   bigint      NOT NULL,
    reason       text        NOT NULL CHECK (reason IN ('voluntary', 'timeout')),
    recorded_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id)
);

CREATE INDEX IF NOT EXISTS deserter_penalty_until
    ON "角色".deserter_penalty (until_ms);
