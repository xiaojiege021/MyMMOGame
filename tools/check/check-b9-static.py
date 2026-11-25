#!/usr/bin/env python3
"""B9 装备穿戴的静态断言。

只钉「规则与接口在不在、数值有没有写死」，不钉条数与现值（D5）：
  1. proto 上有 EquipItem / UnequipItem 两条 RPC 与四组消息，字段号不重复；
     InventorySlot 追加 equipped_slot、DescribeBattleProfileResponse 追加 equip_attack；
  2. 判定只有一个出口：EquipRules 先查「是不是装备」与「等级」，武器再看职业锁；
  3. 穿戴表的落点：主键 (character_id, slot)、唯一 inventory_id、外键 ON DELETE CASCADE；
  4. 装备固定攻击只来自物品表（items.Find(...).Attack），角色服与战斗服都不写死数；
  5. items.json：每件装备都有 slot 且落在三部位里，attack 非负；非装备不许写这两个字段；
  6. 网关两条路由在，ok=false 业务拒绝原样回包，部位不由网关填；
  7. 客户端只发格号 / 部位，只读回包；背包屏的穿 / 脱标签跟 equipped_slot 走；
  8. 战斗服：副本走职业表 + 装备固定攻击，竞技场模板与战场/城战各自那一层不动。
"""
import json
import os
import re
import sys

root = os.environ["ROOT"]
fails = []


def read(path):
    _sub = {"CharacterGrpcService.cs": "Grpc", "RealmTable.cs": "Realm", "CharacterProcess.cs": "Startup", "Config.cs": "ConfigTables", "Encounter.cs": "Encounter", "Effects.cs": "Effects", "Server.cs": "Server", "CharacterApi.cs": "Api/Character", "SectApi.cs": "Api/Sect", "WorldApi.cs": "Api/World", "SectRepository.cs": "Sect", "SocialGrpcService.cs": "Grpc", "WorldGrpcService.cs": "Grpc", "Program.cs": "Cli"}
    _name = __import__("os").path.basename(path)
    if _name in _sub:
        # 14.8 拆分卡之后：主文件 + 分片目录一起读（断言文本不变，只改读法）。
        _os = __import__("os"); _g = __import__("glob")
        _b = path if _os.path.isabs(path) else _os.path.join(_os.getcwd(), path)
        if not _os.path.exists(_b):
            _d = _os.path.dirname(_os.getcwd())
            while _d and not _os.path.exists(_os.path.join(_d, path)):
                _nd = _os.path.dirname(_d)
                if _nd == _d: break
                _d = _nd
            _b = _os.path.join(_d, path)
        return "".join(open(f, encoding="utf-8").read() for f in
                       [_b] + sorted(_g.glob(_os.path.join(_os.path.dirname(_b), _sub[_name], "*.cs"))))
    with open(os.path.join(root, path), encoding="utf-8") as handle:
        return handle.read()


def want(condition, message):
    if not condition:
        fails.append(message)


proto = read("proto/mmo/character.proto")

# 1. 两条 RPC 与四组消息
for rpc in ("EquipItem", "UnequipItem"):
    want(re.search(r"\brpc %s\(" % rpc, proto) is not None, "proto 缺 rpc " + rpc)
for name in ("EquipItemRequest", "EquipItemResponse", "UnequipItemRequest", "UnequipItemResponse"):
    want("message %s {" % name in proto, "proto 缺 " + name)

for name in ("EquipItemRequest", "EquipItemResponse", "UnequipItemRequest", "UnequipItemResponse"):
    body = re.search(r"message %s \{(.*?)\n\}" % name, proto, re.S)
    want(body is not None, "proto 里 " + name + " 读不出来")
    if body is not None:
        numbers = [int(n) for n in re.findall(r"=\s*(\d+)\s*;", body.group(1))]
        want(len(numbers) == len(set(numbers)), name + " 有重复字段号")

slot_msg = re.search(r"message InventorySlot \{(.*?)\n\}", proto, re.S)
want(slot_msg is not None, "proto 里 InventorySlot 读不出来")
if slot_msg is not None:
    want("equipped_slot" in slot_msg.group(1), "InventorySlot 没有追加 equipped_slot")
    # 老四格的字段号一个不动：1..4 还是 slot / item_id / count / bound。
    for number, field in ((1, "slot"), (2, "item_id"), (3, "count"), (4, "bound")):
        want(re.search(r"\b%s\s*=\s*%d\s*;" % (field, number), slot_msg.group(1)) is not None,
             "InventorySlot 的 %s 不再是 %d 号字段" % (field, number))

profile_msg = re.search(r"message DescribeBattleProfileResponse \{(.*?)\n\}", proto, re.S)
want(profile_msg is not None, "proto 里 DescribeBattleProfileResponse 读不出来")
if profile_msg is not None:
    want(re.search(r"equip_attack\s*=\s*9\s*;", profile_msg.group(1)) is not None,
         "DescribeBattleProfileResponse 没有追加 equip_attack = 9")
    want(re.search(r"passive_pct_bps\s*=\s*8\s*;", profile_msg.group(1)) is not None,
         "Q2 的 passive_pct_bps 不再是 8 号字段")

for rpc in ("CanEquip", "ListInventory", "DescribeBattleProfile", "MoveItem", "DiscardItem"):
    want(re.search(r"\brpc %s\(" % rpc, proto) is not None,
         "老 RPC " + rpc + " 被删了（这一卡只追加，不删不改）")

# 2. 判定只有一个出口
rules = read("src/Character/EquipRules.cs")
repo = read("src/Character/EquipRepository.cs")
want("not_equipment" in rules, "EquipRules 没有「不是装备」这一档")
want(re.search(r"if \(level < item\.RequireLevel\)", rules) is not None,
     "EquipRules 没有对全部装备查等级（老实现只在武器分支里查）")
want("ClassCanUseWeapon" in rules and "WeaponLockLevel" in rules,
     "EquipRules 丢了武器锁判定")
service = read("src/Character/CharacterGrpcService.cs")
want("EquipRules.Check(" in service,
     "CanEquip 没有走 EquipRules.Check（出现两套判定）")
want("EquipRules.Check(" in repo,
     "EquipRepository 没有走 EquipRules.Check（出现两套判定）")

# 3. 穿戴表的落点
equip_sql = read("db/character/equip.sql")
want('CREATE TABLE IF NOT EXISTS "角色".character_equip' in equip_sql, "equip.sql 没建 character_equip")
want("PRIMARY KEY (character_id, slot)" in equip_sql, "穿戴表主键不是 (角色, 部位)：同部位能穿两件")
want("UNIQUE (inventory_id)" in equip_sql, "穿戴表没有唯一约束 inventory_id：同一行能穿两个部位")
want("REFERENCES \"角色\".inventory (id) ON DELETE CASCADE" in equip_sql,
     "穿戴表外键没有 ON DELETE CASCADE：卖出装备后槽位会指向已删的行")
want("slot IN ('weapon', 'armor', 'accessory')" in equip_sql, "部位取值与 items.json 的 slot 不是同一套")
main_sql = read("db/character/character.sql")
want('CREATE TABLE IF NOT EXISTS "角色".character_equip' in main_sql,
     "主迁移没有同一份 DDL：只跑主迁移的老卡建不出穿戴表")
migrations = json.load(open(os.path.join(root, "db/migrations.json"), encoding="utf-8"))
character = [s for s in migrations["schemas"] if s["key"] == "character"][0]
files = [entry["file"] for entry in character["entries"]]
want("db/character/equip.sql" in files, "迁移清单没有 equip.sql")
want("db/character/crystal_slot_follow.sql" in files, "Q10 的 v28 那条被挤掉了")
versions = [entry["version"] for entry in character["entries"]]
want(len(versions) == len(set(versions)), "迁移版本号有重复")

# 4. 装备固定攻击只来自物品表
want("items.Find(" in repo and ".Attack ?? 0" in repo,
     "装备攻击不是从物品表求和读的（或代码里写了第二份数值）")
want(re.search(r"total\s*\+=", repo) is not None, "装备攻击没有累加")
want("inventory" in repo and "character_equip" in repo, "EquipRepository 没同时碰穿戴表与背包行")
want("for update" in repo, "穿戴写入没有锁角色行 / 背包行")
want('"slot_occupied"' in repo and '"already_equipped"' in repo,
     "同部位两件的两种拒绝码不全")
want('"not_equipment"' in rules, "穿非装备没有 not_equipment")
want("Insert" in repo or "insert into" in repo, "EquipRepository 没有写入穿戴记录")

# 5. items.json 的装备定义
items = json.load(open(os.path.join(root, "config/items.json"), encoding="utf-8"))
slots = set()
equipment = 0
for one in items["items"]:
    kind = one.get("kind", "material")
    if kind == "equipment":
        equipment += 1
        slot = one.get("slot", "")
        want(slot in ("weapon", "armor", "accessory"),
             "装备 %s 的 slot 不在三部位里：%s" % (one.get("id"), slot))
        slots.add(slot)
        want(int(one.get("attack", 0)) >= 0, "装备 %s 的 attack 是负数" % one.get("id"))
    else:
        want("slot" not in one, "非装备 %s 写了 slot" % one.get("id"))
        want("attack" not in one, "非装备 %s 写了 attack" % one.get("id"))
want(equipment > 0, "items.json 里一件装备都没有")
want(slots == {"weapon", "armor", "accessory"}, "三部位没有都用到：" + ",".join(sorted(slots)))

# 6. 网关两条路由
gateway = read("src/Gateway/CharacterApi.cs")
for route in ("/character/equip", "/character/unequip"):
    want('"%s"' % route in gateway, "网关缺路由 " + route)
want("ok = equip.Ok" in gateway and "equip_attack = equip.EquipAttack" in gateway,
     "穿上路由没有把 ok 与装备攻击原样回给客户端")
want("ok = unequip.Ok" in gateway and "bag_slot = unequip.BagSlot" in gateway,
     "脱下路由没有把 ok 与格号原样回给客户端")
want("EquipSlot = \"weapon\"" not in gateway and "equip_slot\":\"weapon" not in gateway,
     "网关替客户端填了部位")

# 7. 客户端
net = read("client/MMOGame/Assets/Net/CharacterBag.cs")
for token in ("EquipBody(", "UnequipBody(", "ParseEquip(", "ParseUnequip("):
    want(token in net, "网络层缺 " + token)
want("equipped_slot" in net, "网络层没读回包里的 equipped_slot")
screen = read("client/MMOGame/Assets/HotUpdate/Ui/UiBagScreen.cs")
want("config/" not in screen, "背包屏读了配置")
want("WornSlot(" in screen and "EquippedSlot" in screen,
     "背包屏没有按回包的 equipped_slot 决定穿 / 脱")
want("flow.EquipItem(" in screen and "flow.UnequipItem(" in screen,
     "背包屏没有接两条穿戴动作")
flow = read("client/MMOGame/Assets/HotUpdate/Ui/UiUpgradeFlow.cs")
want('"/character/equip"' in flow and '"/character/unequip"' in flow,
     "流程层没有接两条穿戴路由")
want("ParseEquip(text)" in flow and "ParseUnequip(text)" in flow,
     "流程层没有复用回包拆装")

# 8. 战斗服：只给副本那一层加装备攻击
battle = read("src/Battle/Encounter.cs")
# A9：职业攻击这一截带上加点（Class.Attack(Bonus)），两层优先级与装备那一截一个字没动；
# 这里仍同时钉「副本 = 职业攻击 + 装备固定攻击」与「战场 / 模板优先」两件事，口径不放松。
want("Combat?.Attack ?? Template?.Attack" in battle
     and "Class.Attack(Bonus) + (Profile?.EquipAttack ?? 0)" in battle,
     "副本攻击没有接装备固定攻击（或竞技场模板 / 战场那一层被改了）")
profile = read("src/Battle/BattleProfileClient.cs")
want("int EquipAttack = 0" in profile and "reply.EquipAttack" in profile,
     "战斗服快照没有 equip_attack")
# B2：套装加成并进同一格，填的很可能是 worn.TotalAttack；两种写法都认，钉的是「填了这一格」。
want("DescribeBattleProfileResponse" in service
     and re.search(r"EquipAttack\s*=\s*\(int\)worn\.(TotalAttack|Attack)", service) is not None,
     "角色服的战斗快照没有填 equip_attack")
want("WornTotalsAsync" in service, "角色服的战斗快照没有走已穿装备汇总（B10 起一次读完三样）")

if fails:
    for one in fails:
        print("b9_static=fail " + one)
    sys.exit(1)

print("b9_static=ok proto=2rpc+2fields rules=one-exit equip_table=cascade "
      "attack=from-items gateway=2routes client=reads-reply battle=dungeon-only")
