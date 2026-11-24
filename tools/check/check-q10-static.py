#!/usr/bin/env python3
"""Q10 / I2 背包操作的静态断言。

只钉「接口与口径在不在」，不钉具体条数与现值（D5）：
  1. proto 上有四个 RPC 与四组消息，字段号不重复；
  2. 角色服的三条写入只改 slot（合并时改目标格数量），不碰 item_id / bound / bound_at；
  3. 排序键是 种类 → 品阶 → 物品编号，品阶顺序与 K5 的六档一致；
  4. 丢弃必须带 confirm；批量使用只认消耗品，没有效果入口时一个都不扣；
  5. 网关四条路由都在，业务拒绝走 ok=false，confirm 不被网关替玩家填 true；
  6. 客户端四屏操作只发请求、只读回包，丢弃走确认弹窗；
  7. 迁移里 crystal_socket 的外键是 ON UPDATE CASCADE（换格时结晶跟着走）。
"""
import json
import os
import re
import sys

root = os.environ["ROOT"]
sys.path.insert(0, os.path.join(root, "tools", "check"))
from lib_read import ui_text_ref_like
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

# 1. 四个 RPC 与四组消息
for rpc in ("MoveItem", "DiscardItem", "SortInventory", "UseItemBatch"):
    want(re.search(r"\brpc %s\(" % rpc, proto) is not None, "proto 缺 rpc " + rpc)

want("message MoveItemRequest" in proto, "proto 缺 MoveItemRequest")
want("message MoveItemResponse" in proto, "proto 缺 MoveItemResponse")
want("message DiscardItemRequest" in proto, "proto 缺 DiscardItemRequest")
want("message DiscardItemResponse" in proto, "proto 缺 DiscardItemResponse")
want("message SortInventoryRequest" in proto, "proto 缺 SortInventoryRequest")
want("message SortInventoryResponse" in proto, "proto 缺 SortInventoryResponse")
want("message UseItemBatchRequest" in proto, "proto 缺 UseItemBatchRequest")
want("message UseItemBatchResponse" in proto, "proto 缺 UseItemBatchResponse")

# 字段号在一个 message 内不许重号（只查新加的八条消息）
for name in ("MoveItemRequest", "MoveItemResponse", "DiscardItemRequest", "DiscardItemResponse",
             "SortInventoryRequest", "SortInventoryResponse", "UseItemBatchRequest",
             "UseItemBatchResponse"):
    body = re.search(r"message %s \{(.*?)\n\}" % name, proto, re.S)
    want(body is not None, "proto 里 " + name + " 读不出来")
    if body is None:
        continue
    numbers = [int(n) for n in re.findall(r"=\s*(\d+)\s*;", body.group(1))]
    want(len(numbers) == len(set(numbers)), name + " 有重复字段号")

# 生成物清单跟 proto 对上了（D16 的闸门还会再跑一次全量）
manifest = json.load(open(os.path.join(root, "proto/manifest.json"), encoding="utf-8"))
want(any("character.proto" in str(entry) for entry in json.dumps(manifest).split('"')),
     "proto/manifest.json 里没有 character.proto")

bag = read("src/Character/BagRepository.cs")

# 2. 三条写入只改 slot；合并只加目标格的数量
want(r"update \"角色\".inventory set slot = $2 where id = $1" in bag,
     "移动 / 排序改格号的语句不在（或改法变了）")
want(r"update \"角色\".inventory set count = count + $2 where id = $1" in bag,
     "合并加数量的语句不在")
want("set item_id" not in bag, "BagRepository 动了 item_id")
want("set bound" not in bag, "BagRepository 动了绑定")
want("bound_at" in bag and "set bound_at" not in bag, "BagRepository 写了绑定时刻")
want(r"insert into \"角色\".inventory" not in bag,
     "BagRepository 在插新行：移动 / 排序只该改格号，不该换行")

# 3. 排序键顺序
kind_order = re.search(r"static readonly string\[\] KindOrder =\s*\[(.*?)\];", bag, re.S)
want(kind_order is not None, "KindOrder 不在")
if kind_order is not None:
    kinds = re.findall(r'"([a-z_]+)"', kind_order.group(1))
    want(kinds and kinds[0] == "equipment", "KindOrder 的第一档不是装备")
    want("material" in kinds and "consumable" in kinds, "KindOrder 少了材料 / 消耗品")
grade_order = re.search(r"static readonly string\[\] GradeOrder =\s*\[(.*?)\];", bag, re.S)
want(grade_order is not None, "GradeOrder 不在")
if grade_order is not None:
    grades = re.findall(r'"([a-z]+)"', grade_order.group(1))
    want(grades == ["white", "green", "blue", "purple", "orange", "red"],
         "品阶顺序与 K5 的六档不一致：" + ", ".join(grades))
want(".OrderBy(r => KindRank(" in bag and ".ThenBy(r => GradeRank(" in bag
     and '.ThenBy(r => r.ItemId, StringComparer.Ordinal)' in bag,
     "排序不是 种类 → 品阶 → 编号")

# 4. 丢弃的二次确认与批量使用的门槛
want("not_confirmed" in bag, "丢弃没有「不带确认就拒绝」这一档")
want("grant_txn" in bag and "on conflict (txn_id) do nothing" in bag,
     "丢弃没有抢事务号（同号重放会二次丢）")

service = read("src/Character/CharacterGrpcService.cs")
want('const string UsableKind = "consumable";' in service,
     "批量使用没有限定只认消耗品")
want('"no_effect"' in service, "批量使用没有 no_effect 这一档")
want('"effect_single_instance"' in service,
     "批量使用没有「同时只生效一个」这一档")
for reason in ('"same_slot"', '"slot_empty"', '"stack_overflow"', '"row_attached"', '"bag_full"'):
    want(reason in bag, "移动缺拒绝码 " + reason)

# 四个 RPC 的格号上限都从配置来，不在代码里写死背包格数。
# B7+B8 起格数按角色现取（BagExpandRepository.SlotsAsync），所以只钉「格数一定来自
# 配置或按角色取的那一处」，不钉具体函数名——写死函数名的话，下一张换来源就又假红。
SLOT_SOURCE = re.compile(r"config\.BagSlots|\.SlotsAsync\(")
for method in ("MoveItem", "DiscardItem", "UseItemBatch"):
    body = re.search(r"public override async Task<\w+> %s\((.*?)\n    \}" % method, service, re.S)
    want(body is not None, "角色服缺 " + method + " 实现")
    if body is not None:
        want(SLOT_SOURCE.search(body.group(1)) is not None,
             method + " 没有从配置 / 按角色的来源取格数")
# 来源再往下追一层：真正的格数算法只有一处，且它读的只有 character.json。
bagrepo = read("src/Character/BagExpandRepository.cs")
want("config.BagSlots" in bagrepo and "config.BagSlotsMax" in bagrepo and "config.BagExpandStep" in bagrepo,
     "背包容量算法没读 character.json 的 bag_slots / bag_expand_step / bag_slots_max（格数写死了）")

# 5. 网关四条路由
gateway = read("src/Gateway/CharacterApi.cs")
for route in ("/inventory/move", "/inventory/discard", "/inventory/sort", "/inventory/use-batch"):
    want('"%s"' % route in gateway, "网关缺路由 " + route)
want("Confirm = body.Confirm ?? false" in gateway,
     "网关替客户端把 confirm 填成 true 了（二次确认就白做了）")
want('ok = move.Ok' in gateway and 'ok = discard.Ok' in gateway
     and 'ok = sort.Ok' in gateway and 'ok = use.Ok' in gateway,
     "四条路由没有把 ok 原样回给客户端")

# 6. 客户端：只发请求、只读回包
flow = read("client/MMOGame/Assets/Net/../HotUpdate/Ui/UiUpgradeFlow.cs")
for token in ("CharacterBag.ParseMove", "CharacterBag.ParseDiscard",
              "CharacterBag.ParseSort", "CharacterBag.ParseUseBatch"):
    want(token in flow, "流程没有复用 " + token)

screen = read("client/MMOGame/Assets/HotUpdate/Ui/UiBagScreen.cs")
want("config/" not in screen, "背包屏读了配置")
want('"slots"' not in screen, "背包屏自己拆 JSON")
want("ShowConfirm(" in screen, "丢弃没有走确认弹窗")
# D9 之后按钮文案在 config/ui_text/UiBagScreen.json，屏上只留常量引用。
want(ui_text_ref_like(root, "UiBagScreen", "丢弃") is not None, "背包屏没有丢弃按钮")

net = read("client/MMOGame/Assets/Net/CharacterBag.cs")
for token in ("MoveBody", "DiscardBody", "SortBody", "UseBatchBody"):
    want(token + "(" in net, "网络层缺 " + token)
want('(confirm ? "true" : "false")' in net, "丢弃请求体没有把 confirm 带出去")

# 7. 迁移：结晶外键跟着格号走
migrations = json.load(open(os.path.join(root, "db/migrations.json"), encoding="utf-8"))
character = [s for s in migrations["schemas"] if s["key"] == "character"][0]
files = [entry["file"] for entry in character["entries"]]
want("db/character/crystal_slot_follow.sql" in files, "迁移清单没有 v28 那条")
follow = read("db/character/crystal_slot_follow.sql")
want("ON UPDATE CASCADE" in follow, "v28 没有把外键改成 ON UPDATE CASCADE")
main = read("db/character/character.sql")
want("crystal_socket_inventory_fkey" in main and "ON UPDATE CASCADE" in main,
     "主迁移建的结晶外键不是 ON UPDATE CASCADE（新库与老库会长得不一样）")

if fails:
    for one in fails:
        print("q10_static=fail " + one)
    sys.exit(1)

print("q10_static=ok proto=4rpc bag=slot-only sort=kind-grade-id confirm=required "
      "use=consumable-only migration=cascade")
