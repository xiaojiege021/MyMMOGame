-- P1-5c7：限时经验道具的剩余时间。只建表，不建数据库，不改 schema 属主。
--
-- 方案 12.1 写定「时间走配置，到点消失。同时只生效一个，再用一张只刷新剩余时间，
-- 倍率不叠」；技术架构第 104 行把「限时经验道具的剩余时间」点名放在角色 schema。
-- 所以这一行是持久数据：换一场、换进程、角色服重启都还读得到它，到期判定用
-- 数据库当前时刻（expires_at > now()），不用客户端或战斗服的时间。
--
-- 表里故意不存倍率。倍率只有 config/exp_booster.json 里那一个，结算时读配置；
-- 主键是 character_id，同一个角色最多一行，再用一张只 update 这一行的 expires_at，
-- 结构上就没法把倍率叠起来、也没法同时生效两个。
CREATE TABLE IF NOT EXISTS "角色".exp_booster (
    character_id bigint      PRIMARY KEY REFERENCES "角色".character (id) ON DELETE CASCADE,
    expires_at   timestamptz NOT NULL,
    used_at      timestamptz NOT NULL DEFAULT now(),
    last_txn_id  text        NOT NULL
);

-- 「剩余时间」就是 expires_at - now()，不另存一列，免得两处时间对不上。
-- 表里没有行、或这一行已经过期，都等价于没生效，结算按原倍率。
-- 过期的行留在库里当流水，不物理删除；「到点消失」是效果消失，由判定保证。
