-- Q12/K3：角色名登记表。全服跨账号唯一就靠这张表的 name_key 主键。
--
-- 为什么另起一张表：`character` 上现有的唯一索引是 (account, name)，只保证
-- 同一个账号内不重名；改成在 character 上加全局唯一索引会把「改名释放旧名」
-- 变成一次 UPDATE，旧名无法被立刻回收。这张表一行 = 一个正在被占用的名字，
-- 改名就是删旧行 + 插新行，旧名随后可被别人创建。
--
-- name_key 由角色服算好再写（NFKC 归一 + 去首尾空白 + 大小写不敏感），
-- 库里不重复实现归一：SQL 只有 lower/btrim，做不出 NFKC，写两套就会漂。
-- name 存显示用原名，改名后仍然按原样展示。
--
-- 一个角色只能占一个名字（character_id 唯一），改名时两个动作在同一笔事务里。
--
-- 幂等：重复跑主迁移不报错（CREATE TABLE IF NOT EXISTS）。同一份 DDL 也追加在
-- db/character/character.sql（角色主迁移，老卡只跑主迁移也要建得出来）。
--
-- 旧库升级：本迁移只建表，不回填——回填要用与代码同一套归一，交给
-- `CliProbe --name-backfill`（幂等，逐条报冲突），迁移里不写第二套归一。

CREATE TABLE IF NOT EXISTS "角色".character_name (
    -- 归一后的名字，唯一性只认它。
    name_key      text        PRIMARY KEY,
    -- 显示用原名（改名后仍是这一格）。
    name          text        NOT NULL,
    character_id  bigint      NOT NULL
                  REFERENCES "角色".character (id) ON DELETE CASCADE,
    registered_at timestamptz NOT NULL DEFAULT now()
);

-- 一个角色只占一个名字：改名换行时不会留下第二个名字。
-- 这个唯一索引同时充当「按角色号反查名字」的索引，不再另建一条。
CREATE UNIQUE INDEX IF NOT EXISTS character_name_character
    ON "角色".character_name (character_id);
