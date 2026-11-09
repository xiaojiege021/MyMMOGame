-- P1-1a1：账号表。只建表，不建数据库，不改 schema 属主。
-- 口令只存哈希、盐和迭代次数，不存明文；改口令策略不用改表结构。
-- 账号按原样匹配（不折叠大小写），注册那一行再定账号规则。
CREATE TABLE IF NOT EXISTS "角色".account (
    account        text        PRIMARY KEY,
    password_hash  bytea       NOT NULL,
    password_salt  bytea       NOT NULL,
    iterations     integer     NOT NULL CHECK (iterations >= 10000),
    disabled       boolean     NOT NULL DEFAULT false,
    created_at     timestamptz NOT NULL DEFAULT now()
);
