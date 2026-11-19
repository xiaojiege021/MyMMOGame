-- Q10：结晶跟着背包行走，换格不用重装。
--
-- P1-4d 的 crystal_socket 用 (character_id, slot) 指向背包，没有 ON UPDATE 动作：
-- 一改 inventory.slot（Q10 的移动 / 排序只改这一列），库里直接报外键冲突，
-- 带结晶的装备连挪一格都挪不动。结晶本来就「跟着那一格的装备走」，
-- 所以把这条外键改成 ON UPDATE CASCADE：格子变了，镶嵌行跟着走，编号不变。
--
-- 只换外键的动作，不改主键、不加列、不动已应用的 v1–v27。
-- 按定义找那条外键，不按自动生成的名字找：老库上的名字不一定和仓库推的一致（C4 踩过）。
--
-- 这是分卡迁移（reapply=false），只跑一次；已应用的版本不许原地改。

DO $$
DECLARE item record;
BEGIN
    FOR item IN
        SELECT c.conname
          FROM pg_constraint c
         WHERE c.conrelid = '"角色".crystal_socket'::regclass
           AND c.contype = 'f'
           AND c.confrelid = '"角色".inventory'::regclass
    LOOP
        EXECUTE format('ALTER TABLE "角色".crystal_socket DROP CONSTRAINT %I', item.conname);
    END LOOP;
END $$;

ALTER TABLE "角色".crystal_socket
    ADD CONSTRAINT crystal_socket_inventory_fkey
    FOREIGN KEY (character_id, slot)
    REFERENCES "角色".inventory (character_id, slot)
    ON UPDATE CASCADE ON DELETE CASCADE;
