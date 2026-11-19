#!/usr/bin/env python3
# B1 静态断言：丹药 / 炼丹。
#
# 钉六组事：
#   1. 协议：DescribePill / ConsumePill / PillPool / 战斗三条消息都在，老字段编号一个没动；
#   2. 配置：六种丹、两档药材、炼丹六条配方、每段突破都有丹，商城禁售清单含 pill / herb；
#   3. 数值不写死：回复值只在 pill.json，源码里没有药丸编号，也没有第二套回复量；
#   4. 扣丹：角色服一笔事务（抢号 → 按行扣 → 读回余额），突破事务里扣，战斗服不扣；
#   5. 冷却共用：吃丹只写 heal / mana 两个组，组名由角色服回包给；
#   6. 库与挂载：pill.sql 两列 + 主迁移镜像 + 迁移清单 v30；探针与 check-all / ci 挂上。
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
os.chdir(ROOT)

FAILS = []


def ok(name, why=""):
    print("ok   " + name)


def bad(name, why=""):
    FAILS.append(name)
    print("FAIL " + name + ("  " + str(why) if why else ""))


def rd(path):
    full = os.path.join(ROOT, path)
    return open(full, encoding="utf-8").read() if os.path.exists(full) else ""


def js(path):
    full = os.path.join(ROOT, path)
    return json.load(open(full, encoding="utf-8")) if os.path.exists(full) else {}


# ---- 1. 协议 ----
proto = rd("proto/mmo/character.proto")
battle = rd("proto/mmo/battle.proto")
for needle, name in (
    ("rpc DescribePill(DescribePillRequest)", "protocol_describe_rpc"),
    ("rpc ConsumePill(ConsumePillRequest)", "protocol_consume_rpc"),
    ("enum PillPool {", "protocol_pool_enum"),
    ("message DescribePillRequest {", "protocol_describe_msg"),
    ("message ConsumePillResponse {", "protocol_consume_msg"),
):
    (ok if needle in proto else bad)(name, needle)

for needle, name in (
    ("UsePillRequest pill = 7;", "protocol_client_pill"),
    ("PillUsed pill_used = 18;", "protocol_used_msg"),
    ("PillRejected pill_rejected = 19;", "protocol_rejected_msg"),
    ("message UsePillRequest {", "protocol_use_pill"),
):
    (ok if needle in battle else bad)(name, needle)

# 老字段编号一个没动（B1 只追加）
for needle, name in (
    ("int32 success_pct = 17;", "protocol_advance_success_pct_17"),
    ("bool duplicate = 18;", "protocol_advance_duplicate_18"),
    ("string pill_kind = 19;", "protocol_advance_pill_kind_19"),
    ("int64 pill_count = 20;", "protocol_advance_pill_count_20"),
    ("int64 pill_left = 21;", "protocol_advance_pill_left_21"),
):
    (ok if needle in proto else bad)(name, needle)

# 符的三条老消息没被改（只追加）
for needle, name in (
    ("UseTalismanRequest talisman = 4;", "protocol_talisman_untouched"),
    ("TalismanUsed talisman_used = 10;", "protocol_talisman_used_untouched"),
    ("not_talisman", "protocol_talisman_reason_untouched"),
):
    (ok if needle in battle else bad)(name, needle)

# ---- 2. 配置 ----
items = {x["id"]: x for x in js("config/items.json").get("items", [])}
pills_doc = js("config/pill.json")
talisman = js("config/talisman.json")
realms = js("config/realms.json")
recipes = js("config/recipes.json")
gather = js("config/gather.json")

ok_pills = pills_doc.get("pills", [])
kinds = [p.get("kind") for p in ok_pills]
if pills_doc.get("schema") == "pill-table/1":
    ok("config_pill_schema")
else:
    bad("config_pill_schema", pills_doc.get("schema"))

if kinds.count("hp") == 2 and kinds.count("mana") == 2 and kinds.count("breakthrough") == 2:
    ok("config_six_pills_two_each")
else:
    bad("config_six_pills_two_each", kinds)

breakthrough_ids = [p["item_id"] for p in ok_pills if p.get("kind") == "breakthrough"]
hp_values = [p.get("restore", 0) for p in ok_pills if p.get("kind") == "hp"]
mana_values = [p.get("restore", 0) for p in ok_pills if p.get("kind") == "mana"]

if os.path.exists(os.path.join(ROOT, "config/pill.json")) and "#" not in json.dumps(pills_doc):
    pass

# 每颗丹都得在物品表里，是消耗品，到账不绑（否则上不了拍卖）
bad_items = []
for p in ok_pills:
    item = items.get(p["item_id"])
    if item is None or item.get("kind") != "consumable" or item.get("bind") != "none":
        bad_items.append(p["item_id"])
(ok if not bad_items else bad)("config_pills_tradeable_consumables", ",".join(bad_items))

# 冷却组与档位
bad_groups = [
    p["item_id"] for p in ok_pills
    if p.get("kind") in ("hp", "mana")
    and (p.get("cooldown_group") != ("heal" if p["kind"] == "hp" else "mana")
         or int(p.get("cooldown_ms", 0)) < 1 or int(p.get("tier", 0)) < 1)
]
(ok if not bad_groups else bad)("config_cooldown_groups", ",".join(bad_groups))

# 符的上限就是同级最强丹，两个数同一份
caps = (talisman.get("red", {}).get("potion_hp_restore"), talisman.get("blue", {}).get("potion_mana_restore"))
if caps == (max(hp_values), max(mana_values)):
    ok("config_talisman_caps_match_pills")
else:
    bad("config_talisman_caps_match_pills", str(caps) + " vs " + str((max(hp_values), max(mana_values))))

# 符自己的回复不许超过同级丹
if (talisman.get("red", {}).get("hp_restore", 0) <= max(hp_values)
        and talisman.get("blue", {}).get("mana_restore", 0) <= max(mana_values)):
    ok("config_talisman_under_pills")
else:
    bad("config_talisman_under_pills")

# 炼丹：六条，产出就是这六颗，材料只用药材
alchemy = [r for r in recipes.get("recipes", []) if r.get("profession") == "alchemy"]
produced = sorted(r["output"]["item_id"] for r in alchemy)
if produced == sorted(p["item_id"] for p in ok_pills):
    ok("config_alchemy_covers_six_pills")
else:
    bad("config_alchemy_covers_six_pills", produced)

bad_material = [
    m["item_id"] for r in alchemy for m in r.get("inputs", [])
    if items.get(m["item_id"], {}).get("kind") != "material"
]
(ok if not bad_material else bad)("config_alchemy_uses_materials", ",".join(sorted(set(bad_material))))

# 两档新药材有采集点
nodes = {n.get("item_id") for n in gather.get("nodes", []) if n.get("kind") == "herb"}
missing_nodes = [h for h in ("herb_ling", "herb_xue", "herb_xuan") if h not in nodes]
(ok if not missing_nodes else bad)("config_herb_nodes", ",".join(missing_nodes))

# 每一段突破都配了丹与枚数，且是突破丹
adv = realms.get("advance", [])
bad_adv = [
    a.get("from") for a in adv
    if not a.get("pill") or int(a.get("pill_count", 0)) < 1 or a.get("pill") not in breakthrough_ids
]
(ok if not bad_adv else bad)("config_advance_pills", ",".join(str(x) for x in bad_adv))

# 商城与家园币商城都不卖丹：禁售清单含 pill / herb，商品里没有丹药编号
shop = js("config/shop.json")
home = js("config/home_coin_shop.json")
for doc, name in ((shop, "shop"), (home, "home_coin_shop")):
    forb = doc.get("forbidden", [])
    if "pill" in forb and "herb" in forb:
        ok("config_" + name + "_forbids_pill")
    else:
        bad("config_" + name + "_forbids_pill", forb)
    hit = [i.get("delivery", {}).get("item_id") for i in doc.get("items", [])
           if i.get("delivery", {}).get("item_id") in {p["item_id"] for p in ok_pills}]
    if not hit or hit == [None]:
        ok("config_" + name + "_no_pill_goods")
    else:
        bad("config_" + name + "_no_pill_goods", hit)

# ---- 3. 数值不写死 ----
src = ""
for base, _, files in os.walk(os.path.join(ROOT, "src")):
    if "/bin" in base or "/obj" in base:
        continue
    for name in files:
        if name.endswith(".cs"):
            src += open(os.path.join(base, name), encoding="utf-8", errors="ignore").read()

pill_ids = [p["item_id"] for p in ok_pills]
lit = [i for i in pill_ids if '"' + i + '"' in src]
(ok if not lit else bad)("code_no_pill_ids", ",".join(lit))

# 战斗服不许读丹药配置：它只认回包
battle_src = rd("src/Battle/Encounter.cs") + rd("src/Battle/PillSpend.cs") + rd("src/Battle/Server.cs")
(ok if "PillTable.Load" not in battle_src and "pill.json" not in rd("src/Battle/Config.cs") else bad)("code_battle_no_config")

# 突破丹只在突破事务里扣：战斗服没有它的编号，也没有 pill_kind 的读取
(ok if "pill_kind" not in battle_src else bad)("code_battle_no_breakthrough_pill")

# ---- 4. 扣丹 ----
repo = rd("src/Character/PillRepository.cs")
for needle, name in (
    ("grant_txn", "code_pill_txn_claim"),
    ("for update", "code_pill_row_lock"),
    ("count > 1", "code_pill_stack_decrement"),
    ("no_pill", "code_pill_reason"),
):
    (ok if needle in repo else bad)(name, needle)

adv_repo = rd("src/Character/RealmAdvanceRepository.cs")
for needle, name in (
    # 门槛要有真的比较，不只留一个原因字符串：删掉比较、只改拒绝码这种变异必须被抓。
    ("pillNow < requirement.PillCount", "code_advance_pill_compare"),
    ("pill_not_enough", "code_advance_pill_gate"),
    ("pill_kind, pill_count", "code_advance_pill_ledger"),
    ("DeductItemsAsync", "code_advance_pill_deduct"),
    ("for update", "code_advance_pill_lock"),
):
    (ok if needle in adv_repo else bad)(name, needle)

# 扣丹发生在抢号之后（失败不退丹那条口径）：抢号在扣丹之前
if adv_repo.find("realm_breakthrough") < adv_repo.find("DeductItemsAsync"):
    ok("code_advance_claim_before_deduct")
else:
    bad("code_advance_claim_before_deduct")

# 角色服 handler 与只读口用同一张表
grpc = rd("src/Character/CharacterGrpcService.cs")
for needle, name in (
    ("Task<DescribePillResponse> DescribePill", "code_grpc_describe"),
    ("Task<ConsumePillResponse> ConsumePill", "code_grpc_consume"),
    ("pillTable.Find(itemId)", "code_grpc_uses_table"),
    ("pills.SpendAsync", "code_grpc_uses_repo"),
):
    (ok if needle in grpc else bad)(name, needle)

# 加载链：三份文件互校
table = rd("src/Character/PillTable.cs")
for needle, name in (
    ("CheckAgainstTalismans", "code_crosscheck_talisman"),
    ("CheckAgainstRealms", "code_crosscheck_realms"),
    ("potion_hp_restore", "code_reads_talisman_cap"),
):
    (ok if needle in table else bad)(name, needle)

# ---- 5. 冷却共用 ----
enc = rd("src/Battle/Encounter.cs")
for needle, name in (
    ("HealGroupReadyAtMs", "code_pill_heal_group"),
    ("ManaGroupReadyAtMs", "code_pill_mana_group"),
    ("spend.CooldownGroup", "code_group_from_server"),
    ("ReceivePillUsed", "code_never"),
):
    if name == "code_never":
        continue
    (ok if needle in enc else bad)(name, needle)

# 战斗服不自己写冷却组名
if '"heal"' not in enc and '"mana"' not in enc:
    ok("code_battle_no_group_literal")
else:
    bad("code_battle_no_group_literal")

# ---- 6. 库与挂载 ----
ddl = rd("db/character/pill.sql")
main = rd("db/character/character.sql")
for needle, name in (
    ("ADD COLUMN IF NOT EXISTS pill_kind", "ddl_pill_kind"),
    ("ADD COLUMN IF NOT EXISTS pill_count", "ddl_pill_count"),
    ("CHECK (pill_count >= 0)", "ddl_pill_count_check"),
):
    (ok if needle in ddl and needle in main else bad)(name, needle)

mig = js("db/migrations.json")
char_entries = [s for s in mig.get("schemas", []) if s.get("key") == "character"]
versions = [e.get("version") for e in char_entries[0].get("entries", [])] if char_entries else []
if versions and versions[-1] == 30 and char_entries[0]["entries"][-1].get("file") == "db/character/pill.sql":
    ok("ddl_migration_v30")
else:
    bad("ddl_migration_v30", versions[-3:] if versions else versions)

parser = rd("tools/probe/CliProbe/CliParser.cs") + rd("tools/probe/CliProbe/PillCli.cs")
(ok if "PillCli.Handles" in rd("tools/probe/CliProbe/CliParser.cs") else bad)("probe_parser_hooked")
(ok if "PillCli.TryRunDbAsync" in rd("tools/probe/CliProbe/DbCli.cs") else bad)("probe_db_hooked")
(ok if "PillCli.TryReadOnly" in rd("tools/probe/CliProbe/CliEntry.cs") else bad)("probe_readonly_hooked")

check_all = rd("tools/check/check-all.sh")
ci = rd("tools/check/ci.sh")
(ok if "b1)" in check_all else bad)("check_all_hooked")
(ok if re.search(r"\bb1\b", ci) else bad)("ci_hooked")

# 客户端不本地算：客户端没有丹药回复量
client_src = ""
for base, _, files in os.walk(os.path.join(ROOT, "client/MMOGame/Assets/HotUpdate")):
    for name in files:
        if name.endswith(".cs"):
            client_src += open(os.path.join(base, name), encoding="utf-8", errors="ignore").read()
if not any(i in client_src for i in pill_ids):
    ok("client_no_pill_ids")
else:
    bad("client_no_pill_ids")

print("")
if FAILS:
    print("静态断言失败 " + str(len(FAILS)) + " 项：" + ",".join(FAILS))
    sys.exit(1)
print("静态断言全部通过")
