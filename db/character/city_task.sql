-- Q5/J2：主城修炼 / 境界任务的进度、流水与城池灵石抽成。
--
-- 方案 9.5 / 12 写定「灵石矿脉归主城辖区，采出的灵石大头做成修炼任务和境界任务的
-- 奖励，掌管方按城池税收抽成，比例走配置」，没给任务名与数值：任务、次数、门槛、
-- 奖励只在 config/city_tasks.json；这里只存事实值。
--
-- 1) city_task_progress：一个角色 × 一条任务 × 一个周期（业务日 / 业务周首）的已完成次数。
--    period_key 只由 config/calendar.json 的 CalendarTable 算出来（日常 DayKey、
--    周常 WeekStartDate），SQL 里不出现 CURRENT_DATE / date_trunc；重置就是换一个
--    period_key 重新从 0 起算，不需要清库、不需要定时任务。
--    次数上限（max_per_period）在配置里；库层只保证非负，不写第二套上限数字。
-- 2) city_task_ledger：每完成一笔、每条奖励币种一行，记这一笔发了哪种灵石多少。
--    「重复不双发」由同一笔事务里的「角色」.grant_txn 主键挡（钱的唯一发放入口），
--    这张流水是审计：谁、哪座城、哪条任务、哪个周期、发了多少。
-- 3) 「社交」.city_stone_tax_ledger：城池抽成的灵石流水，**与金币账本
--    「社交」.city_tax_ledger 分开两张表**——两者单位不同，加在一起没有意义；
--    与拍卖销毁税的「交易」.auction_settlement.tax_gold 也是分开的。
--    gross_stone 是这一笔发给玩家的灵石，tax_stone 是按库里「社交」.city.tax_pct
--    向下取整算出的抽成（只记账、不从玩家钱包扣，与 N9 的金币口径一致）。
--    写入方是角色服：钱只在角色服写，抽成与钱同一笔事务、同一个事务号。
--
-- 同一笔完成事务同时抢 grant_txn、写两处流水、加灵石、进度 +1，全部提交；
-- 任何一步失败整笔回滚，不会出现「灵石到账了、次数没加」或「次数加了、没记账」。
--
-- 幂等：重复跑主迁移不报错（CREATE TABLE IF NOT EXISTS）。同一份 DDL 也整份追加在
-- db/character/character.sql 里（老卡只跑主迁移）。

CREATE TABLE IF NOT EXISTS "角色".city_task_progress (
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    task_id      text        NOT NULL,
    -- 业务日（日常）或业务周首（周常），由 CalendarTable 算，不是数据库当天。
    period_key   date        NOT NULL,
    city_id      text        NOT NULL,
    done_count   int         NOT NULL DEFAULT 0 CHECK (done_count >= 0),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (character_id, task_id, period_key)
);

-- 面板按「角色 + 当前周期」取这一周期的各条次数；主键前缀是 character_id，
-- 这条索引补 period_key 的过滤。
CREATE INDEX IF NOT EXISTS city_task_progress_period
    ON "角色".city_task_progress (character_id, period_key);

CREATE TABLE IF NOT EXISTS "角色".city_task_ledger (
    txn_id       text        NOT NULL,
    character_id bigint      NOT NULL REFERENCES "角色".character (id) ON DELETE CASCADE,
    task_id      text        NOT NULL,
    city_id      text        NOT NULL,
    period       text        NOT NULL CHECK (period IN ('daily', 'weekly')),
    period_key   date        NOT NULL,
    -- 这一笔发的灵石档位与数量，来自 config/city_tasks.json 的 rewards。
    currency     text        NOT NULL,
    amount       bigint      NOT NULL CHECK (amount > 0),
    recorded_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (txn_id, currency)
);

CREATE INDEX IF NOT EXISTS city_task_ledger_character
    ON "角色".city_task_ledger (character_id, recorded_at DESC);

CREATE TABLE IF NOT EXISTS "社交".city_stone_tax_ledger (
    txn_id      text        NOT NULL,
    -- 灵石档位。与金币账本分开记，单位不同不能相加。
    currency    text        NOT NULL,
    city_id     text        NOT NULL REFERENCES "社交".city (city_id) ON DELETE CASCADE,
    source      text        NOT NULL CHECK (source IN ('task_grant', 'mining', 'other')),
    gross_stone bigint      NOT NULL CHECK (gross_stone > 0),
    tax_stone   bigint      NOT NULL CHECK (tax_stone >= 0),
    recorded_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (txn_id, currency),
    CHECK (tax_stone <= gross_stone)
);

CREATE INDEX IF NOT EXISTS city_stone_tax_ledger_city
    ON "社交".city_stone_tax_ledger (city_id, recorded_at);
