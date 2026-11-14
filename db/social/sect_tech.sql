-- P3-3a3 宗门科技事实表。
-- 方案 11.1 只写「贡献 / 建设 / 科技三条线，科技只在城战生效」，没写定科技档位数
-- 与升级规则；这里只存等级事实值，不存加成系数（系数在 config/sect_tech.json，
-- 由战斗服折算），不写商城 / 税收 / 领地字段。等级上限不写死在 SQL，由配置硬校验。
-- 社交服是这张表的写入方；战斗服只经 gRPC 读，不直连社交 schema。
CREATE TABLE IF NOT EXISTS "社交".sect_tech (
    sect_id     bigint      PRIMARY KEY REFERENCES "社交".sect (id) ON DELETE CASCADE,
    level       int         NOT NULL DEFAULT 0 CHECK (level >= 0),
    updated_at  timestamptz NOT NULL DEFAULT now()
);
