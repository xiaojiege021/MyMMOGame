-- Q4/J1：宗门日常 / 周常的进度、流水与个人贡献。
--
-- 方案 11.1 写定「升级耗建设。建设和贡献来自宗门秘境和宗门任务，不从商城买」，
-- 没给任务数量与数值：任务、次数、奖励只在 config/sect_tasks.json；这里只存事实值。
-- 三张表都只在「社交」schema，社交服不碰角色 schema、不碰交易 schema。
--
-- 1) sect_task_progress：一个角色 × 一条任务 × 一个周期（业务日 / 业务周首）的已完成次数。
--    period_key 只由 config/calendar.json 的 CalendarTable 算出来（日常 DayKey、
--    周常 WeekStartDate），SQL 里不出现 CURRENT_DATE / date_trunc；重置就是换一个
--    period_key 重新从 0 起算，不需要清库、不需要定时任务。
--    次数上限（max_per_period）在配置里；库层只保证非负，不写第二套上限数字。
-- 2) sect_task_ledger：每完成一笔一行，txn_id 主键挡同一事务号重放。
--    contribution / construction 是这一笔实际加给个人与宗门的数量（来自配置）。
-- 3) sect_contribution：个人在某个宗门的累计贡献。按（宗门, 角色）记，退出再入不迁移；
--    建设在 "社交".sect.construction 上，个人贡献不参与升级消耗。
--
-- 同一笔完成事务同时写进度 +1、插流水、加个人贡献、加宗门建设，四个写在同一笔
-- 事务里；任何一步失败整笔回滚，不会出现「次数加了但建设没加」。
--
-- 幂等：重复跑主迁移不报错（CREATE TABLE IF NOT EXISTS）。a1–a4 的 sect /
-- sect_member / sect_upgrade / sect_transfer 结构一个不动。

CREATE TABLE IF NOT EXISTS "社交".sect_task_progress (
    character_id bigint      NOT NULL,
    task_id      text        NOT NULL,
    -- 业务日（日常）或业务周首（周常），由 CalendarTable 算，不是数据库当天。
    period_key   date        NOT NULL,
    sect_id      bigint      NOT NULL REFERENCES "社交".sect (id) ON DELETE CASCADE,
    done_count   int         NOT NULL DEFAULT 0 CHECK (done_count >= 0),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, task_id, period_key)
);

-- 面板按「角色 + 当前周期」取这一周期的各条次数；主键前缀是 character_id，
-- 这条索引补 period_key 的过滤。
CREATE INDEX IF NOT EXISTS sect_task_progress_period
    ON "社交".sect_task_progress (character_id, period_key);

CREATE TABLE IF NOT EXISTS "社交".sect_task_ledger (
    txn_id       text        PRIMARY KEY,
    sect_id      bigint      NOT NULL REFERENCES "社交".sect (id) ON DELETE CASCADE,
    character_id bigint      NOT NULL,
    task_id      text        NOT NULL,
    period       text        NOT NULL CHECK (period IN ('daily', 'weekly')),
    period_key   date        NOT NULL,
    -- 这一笔实际加给个人 / 宗门的数量，来自 config/sect_tasks.json 的 rewards。
    contribution bigint      NOT NULL CHECK (contribution >= 0),
    construction bigint      NOT NULL CHECK (construction >= 0),
    recorded_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS sect_task_ledger_sect
    ON "社交".sect_task_ledger (sect_id, recorded_at);

CREATE TABLE IF NOT EXISTS "社交".sect_contribution (
    sect_id      bigint      NOT NULL REFERENCES "社交".sect (id) ON DELETE CASCADE,
    character_id bigint      NOT NULL,
    contribution bigint      NOT NULL DEFAULT 0 CHECK (contribution >= 0),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (sect_id, character_id)
);
