#!/usr/bin/env bash
# tools/check/check-k5.sh —— K5 物品表扩展（开发架构 14.7 顺序 3）。
#
# 完成标准（14.7 的 K5 行，逐条对应下面的断言）：
#   1. items.json 加 grade / set_id / desc（icon 不进配置，v1.172 按第 17 节 17.1 修正）；
#   2. 全表补齐（14.7 写的 33 件是 K4 前的快照，现在是 39 件，一件不少）；
#   3. sword_trade 改长刀、新增 15 级法剑；
#   4. 代码只读；
#   5. 价格仍只在商店 / 配方表。
#
# 口径：
#   - 装备必须有 grade（六档）；材料、结晶、称号、外观这几类可以留空。
#   - set_id 现在全表留空：14.7 与核对表都写「先加 B2 的空字段」，套装件数与效果由 B2 填。
#   - 图标地址不进 items.json：第 17 节 17.1 写定 config 里没有美术字段，
#     客户端按 icon/<物品编号> 自己拼；这里断言「配置里没有这一格」。
#   - 装备部位仍只在 crystal.json 的 equipment_parts 一处，items.json 不写 part。
#   - --item-list 是 K5 加的只读入口，与真实启动共用同一个 ItemTable.Load；
#     坏配置在它这里就必须 config_error。
#
#   bash tools/check/check-k5.sh
#   MMO_K5_SKIP_BUILD=1 bash tools/check/check-k5.sh   跳过编译
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
TMP="$(mktemp -d)"
TITLE="K5（server）"
trap 'rm -rf "$TMP"' EXIT

echo "== 1. 编译 =="
if [ "${MMO_K5_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_K5_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/Character/Character.csproj -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "角色服编译通过"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言：字段、取值与两把 15 级兵器 =="
ROOT="$ROOT" python3 - > "$TMP/static.txt" 2>&1 <<'PY'
import json, os, re

root = os.environ["ROOT"]

def read(p):
    p = os.path.join(root, p)
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

def cfg(p):
    return json.loads(read(p))

def report(key, cond, info=""):
    print(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info))

doc = cfg("config/items.json")
items = doc.get("items") or []
ids = [x.get("id") or "" for x in items]
idset = set(ids)
by_id = {x["id"]: x for x in items if x.get("id")}
GRADES = {"white", "green", "blue", "purple", "orange", "red"}
REQUIRED = ("grade", "set_id", "desc")

# ---------------------------------------------------------------- 1. 表本身
report("schema_v4", doc.get("schema") == "item-table/4" and doc.get("version") == 4,
       "schema=%s version=%s" % (doc.get("schema"), doc.get("version")))
report("items_count_floor", len(ids) >= 39, "只有 %d 件（K4 收口时 38，K5 再加 1 把法剑）" % len(ids))
report("items_id_unique", len(ids) == len(idset), "重复=%s" % ",".join(
    sorted({i for i in ids if ids.count(i) > 1})))

lack = ["%s:%s" % (x.get("id"), ",".join(k for k in REQUIRED if k not in x))
        for x in items if any(k not in x for k in REQUIRED)]
report("four_fields_present", not lack, ";".join(lack[:8]))

bad_grade = [x.get("id") for x in items
             if x.get("grade") not in ("" ,) and x.get("grade") not in GRADES]
report("grade_known", not bad_grade, ",".join(bad_grade[:8]))

no_grade = [x.get("id") for x in items
            if x.get("kind") == "equipment" and not x.get("grade")]
report("equipment_graded", not no_grade, ",".join(no_grade[:8]))

bad_set = ["%s:%s" % (x.get("id"), x.get("set_id")) for x in items
           if x.get("set_id") and (x.get("kind") != "equipment"
                                   or not re.fullmatch(r"set_[a-z0-9_]+", x.get("set_id") or ""))]
report("set_id_shape", not bad_set, ";".join(bad_set[:8]))
# B2 之前全表留空：字段先在，套装件数 / 效果等 B2 定。
nonempty_set = [x.get("id") for x in items if x.get("set_id")]
report("set_id_pending_b2", not nonempty_set,
       "已经填了套装（B2 之前应全空）：%s" % ",".join(nonempty_set[:8]))

# v1.172：图标地址不进配置（第 17 节 17.1），谁把它写回 items.json 就红。
with_icon = [x.get("id") for x in items if "icon" in x]
report("no_icon_field", not with_icon, "这几件还写着 icon：%s" % ",".join(with_icon[:8]))

bad_desc = [x.get("id") for x in items
            if not isinstance(x.get("desc"), str) or len((x.get("desc") or "").strip()) < 4]
report("desc_present", not bad_desc, ",".join(bad_desc[:8]))

report("no_part_key", all("part" not in x for x in items),
       "items.json 又写了第二份装备部位（应只在 crystal.json）")
report("no_price_key", all("price" not in x and "currency" not in x for x in items),
       "物品表里出现了价格 / 币种（应只在商店 / 配方表）")

# ---------------------------------------------------------------- 2. 两把兵器的去向
trade = by_id.get("sword_trade") or {}
report("sword_trade_long_saber",
       trade.get("name") == "行商长刀" and trade.get("kind") == "equipment"
       and trade.get("weapon_kind") == "saber" and trade.get("require_level") == 15
       and trade.get("bind") == "none" and bool(trade.get("grade")),
       "sword_trade=%s" % json.dumps(trade, ensure_ascii=False))
report("sword_trade_not_sword", trade.get("weapon_kind") != "sword",
       "sword_trade 还写着 sword")

spirit = [x for x in items if x.get("weapon_kind") == "spirit_sword"]
report("spirit_sword_15",
       len(spirit) == 1 and spirit[0].get("kind") == "equipment"
       and spirit[0].get("require_level") == 15 and spirit[0].get("bind") == "none"
       and "法剑" in (spirit[0].get("name") or "") and bool(spirit[0].get("grade")),
       "法剑=%s" % json.dumps(spirit, ensure_ascii=False))

fifteen = [x.get("id") for x in items
           if x.get("kind") == "equipment" and x.get("weapon_kind")
           and x.get("require_level") == 15]
report("two_15_level_weapons", len(fifteen) >= 2,
       "15 级武器=%s" % ",".join(fifteen))

trial = by_id.get("sword_trial") or {}
report("sword_trial_kept",
       trial.get("weapon_kind") == "sword" and trial.get("require_level") == 10,
       "10 级旧剑被改了：%s" % json.dumps(trial, ensure_ascii=False))

# ---------------------------------------------------------------- 3. 部位单一来源
crystal = cfg("config/crystal.json")
parts = crystal.get("equipment_parts") or {}
equipment = [x["id"] for x in items if x.get("kind") == "equipment"]
missing_part = [i for i in equipment if i not in parts]
extra_part = [i for i in parts if i not in idset or by_id.get(i, {}).get("kind") != "equipment"]
report("equipment_parts_single_source", not missing_part and not extra_part,
       "缺=%s 多=%s" % (missing_part[:6], extra_part[:6]))
report("spirit_sword_part", parts.get("spirit_sword_trade") == "weapon",
       "spirit_sword_trade 没在 crystal.json 登记部位")

# ---------------------------------------------------------------- 4. 代码只读
item_cs = read("src/Character/ItemTable.cs")
report("itemtable_reads_fields",
       all(('"%s"' % k) in item_cs for k in REQUIRED),
       "ItemTable 没有读全 grade / set_id / desc")
# 配置里不写图标地址，ItemTable 也不许再认这一格（v1.172）。
report("itemtable_no_icon_field", '"icon"' not in item_cs,
       "ItemTable 又映射了 icon 字段")
report("itemtable_validates",
       all(k in item_cs for k in ("grade 不认识", "必须写 grade", "只有装备能写 set_id",
                                  "缺 desc")),
       "ItemTable 缺少新字段的加载期校验")
report("itemtable_no_item_branches", not any('"%s"' % i in item_cs for i in idset),
       "ItemTable 里出现了具体物品编号")
report("probe_item_list", 'case "--item-list"' in read("tools/probe/CliProbe/CliParser.cs"),
       "probe 没有 --item-list 只读入口")

# ---------------------------------------------------------------- 5. 价格仍在商店 / 配方
npc = cfg("config/npc_shop.json")
shop = cfg("config/shop.json")
npc_rows = [(s.get("id"), it) for s in (npc.get("shops") or npc.get("npc_shops") or [])
            for it in (s.get("items") or [])]
if not npc_rows:
    # 文件结构没变时是 items 平铺；两种都认，只为断言价格与引用。
    npc_rows = [(s.get("id"), it) for s in [npc] for it in (s.get("items") or [])
                if isinstance(it, dict) and it.get("item_id")]
bad_npc_price = ["%s/%s" % (sid, it.get("item_id")) for sid, it in npc_rows
                 if not isinstance(it.get("price"), int) or it.get("price") < 0
                 or it.get("item_id") not in idset]
report("npc_shop_price_sources", not bad_npc_price,
       "npc_shop 里价格 / 引用不对：%s" % ";".join(bad_npc_price[:6]))
shop_priced = [x for x in (shop.get("items") or []) if isinstance(x.get("price"), int)]
report("shop_price_sources", len(shop_priced) >= 18 and not any(
    (x.get("delivery") or {}).get("mode") == "item"
    and (x.get("delivery") or {}).get("item_id") not in idset for x in shop_priced),
       "商城价格 / 发货引用不对")

ci = read("tools/check/ci.sh")
report("ci_registers_k5", re.search(r'^CHECKS="[\s\S]*\bk5\b', ci, re.M) is not None,
       "ci.sh 的 CHECKS 里没有 k5")
allsh = read("tools/check/check-all.sh")
report("check_all_registers_k5", re.search(r"^\s*k5\)", allsh, re.M) is not None
       and re.search(r"\bk5\b", allsh) is not None, "check-all.sh 没挂 k5")

print("INFO items=%d equipment=%d parts=%d 15_level_weapons=%d"
      % (len(ids), len(equipment), len(parts), len(fifteen)))
print("PASS static_end")
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
    INFO) printf '  ..   %s\n' "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
if ! grep -q '^PASS static_end' "$TMP/static.txt"; then
  bad "静态断言没跑完"; grep -vE '^(PASS|FAIL|INFO) ' "$TMP/static.txt" | tail -6 | detail
fi

echo "== 3. 只读入口：新字段真的被代码读到 =="
IL="$("$DOTNET" "$CHARACTER_DLL" --item-list 2>&1)"; ilrc=$?
ITEM_COUNT="$(python3 -c 'import json; print(len(json.load(open("config/items.json"))["items"]))')"
[ "$ilrc" = "0" ] && grep -q "^item_table_state=ok version=4 items=${ITEM_COUNT}\$" <<< "$IL" \
  && ok "物品表 v4 读出 ${ITEM_COUNT} 件（不连库）" \
  || bad "只读入口不对（rc=${ilrc}）：$(head -2 <<< "$IL" | tr '\n' ' ')"
if [ "$(grep -c '^item=' <<< "$IL")" = "$ITEM_COUNT" ]; then
  ok "物品清单条数与表一致（${ITEM_COUNT}）"
else
  bad "物品清单条数不对：$(grep -c '^item=' <<< "$IL") / ${ITEM_COUNT}"
fi
grep -q '^item=sword_trade kind=equipment grade=green set=- name=行商长刀 ' <<< "$IL" \
  && ok "sword_trade 读出长刀（saber / 15 级）" || bad "sword_trade 读出不对：$(grep '^item=sword_trade' <<< "$IL")"
grep -q '^item=spirit_sword_trade kind=equipment grade=green set=- name=行商法剑 ' <<< "$IL" \
  && ok "新增 15 级法剑读出（spirit_sword）" || bad "法剑读出不对：$(grep '^item=spirit_sword_trade' <<< "$IL")"
grep -q '^item=ore_iron kind=material grade=- ' <<< "$IL" \
  && ok "材料没有品阶（grade=-，空字段不被当成配置错误）" || bad "材料行不对：$(grep '^item=ore_iron' <<< "$IL")"

WR="$("$DOTNET" "$CHARACTER_DLL" --weapon-rules 2>&1)"; wrc=$?
[ "$wrc" = "0" ] && ok "武器只读入口仍能读" || bad "--weapon-rules 不对（rc=${wrc}）"
grep -q 'weapon=sword_trade kind=saber require_level=15' <<< "$WR" \
  && ok "行商长刀在兵器表里是 saber" || bad "行商长刀兵器种类不对"
grep -q 'weapon=spirit_sword_trade kind=spirit_sword require_level=15' <<< "$WR" \
  && ok "法剑在兵器表里是 spirit_sword" || bad "法剑兵器种类不对"
grep -q 'weapon=sword_trial kind=sword require_level=10' <<< "$WR" \
  && ok "10 级旧剑保留（15 级以下全职业）" || bad "10 级旧剑被改没了"

CS="$("$DOTNET" "$CHARACTER_DLL" --crystal-state 2>&1)"; csrc=$?
[ "$csrc" = "0" ] && grep -q 'crystal_parts=.*spirit_sword_trade:weapon' <<< "$CS" \
  && ok "结晶部位表读出法剑（部位仍只在这一处）" \
  || bad "结晶部位表没读到法剑（rc=${csrc}）：$(head -1 <<< "$CS" | cut -c1-80)"

echo "== 4. 真配置整链加载 =="
STARTUP="$TMP/startup.log"
"$DOTNET" "$CHARACTER_DLL" --config config/character.json > "$STARTUP" 2>&1 &
START_PID=$!
for _ in $(seq 1 300); do
  grep -qE '^config_error=|^db_missing=1|Now listening on' "$STARTUP" 2>/dev/null && break
  sleep 0.1
done
stop_pid "$START_PID"
if grep -q '^config_error=' "$STARTUP"; then
  bad "真配置整链加载失败：$(head -1 "$STARTUP")"
elif grep -q "^items=${ITEM_COUNT} item_table_version=4 " "$STARTUP"; then
  ok "真配置整链加载通过（物品表 v4 / ${ITEM_COUNT} 件）"
else
  bad "启动没读到物品表 v4：$(grep '^items=' "$STARTUP" | head -1)"
fi

echo "== 5. 坏配置都起不来 =="
newcase() {
  local name="$1"
  mkdir -p "$TMP/bad/$name"
  cp -R config "$TMP/bad/$name/config"
  printf '%s' "$TMP/bad/$name"
}

run_bad() {
  local case_dir="$1" flag="$2"
  ( cd "$case_dir" && "$DOTNET" "$CHARACTER_DLL" "$flag" 2>&1 )
}

CASE="$(newcase unknown_grade)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/items.json")
d = json.load(open(p, encoding="utf-8"))
next(x for x in d["items"] if x["id"] == "sword_trade")["grade"] = "legendary"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
assert_bad_config "$(run_bad "$CASE" --item-list)" "品阶不在六档：起不来"

CASE="$(newcase equipment_no_grade)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/items.json")
d = json.load(open(p, encoding="utf-8"))
next(x for x in d["items"] if x["id"] == "robe_trial").pop("grade", None)
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
assert_bad_config "$(run_bad "$CASE" --item-list)" "装备没写品阶：起不来"

CASE="$(newcase set_on_material)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/items.json")
d = json.load(open(p, encoding="utf-8"))
next(x for x in d["items"] if x["id"] == "ore_iron")["set_id"] = "set_trial"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
assert_bad_config "$(run_bad "$CASE" --item-list)" "材料写了 set_id：起不来"

CASE="$(newcase bad_set_id)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/items.json")
d = json.load(open(p, encoding="utf-8"))
next(x for x in d["items"] if x["id"] == "sword_trade")["set_id"] = "trial-set"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
assert_bad_config "$(run_bad "$CASE" --item-list)" "set_id 形状不对：起不来"

CASE="$(newcase empty_desc)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/items.json")
d = json.load(open(p, encoding="utf-8"))
next(x for x in d["items"] if x["id"] == "herb_ling")["desc"] = ""
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
assert_bad_config "$(run_bad "$CASE" --item-list)" "说明为空：起不来"

CASE="$(newcase missing_part)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
p = os.path.join(os.environ["CASE"], "config/crystal.json")
d = json.load(open(p, encoding="utf-8"))
d["equipment_parts"].pop("spirit_sword_trade", None)
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
assert_bad_config "$(run_bad "$CASE" --crystal-state)" "法剑没登记部位：起不来"

echo "== 6. 新增物品只改配置 =="
CASE="$(newcase add_item)"
ROOT="$ROOT" CASE="$CASE" python3 - <<'PY'
import json, os
base = os.path.join(os.environ["CASE"], "config")
items = json.load(open(os.path.join(base, "items.json"), encoding="utf-8"))
items["items"].append({
    "id": "k5_probe_blade", "name": "核对用长刀", "kind": "equipment",
    "grade": "blue", "set_id": "", "weapon_kind": "saber", "require_level": 20,
    "stack_max": 1, "bind": "none",
    "desc": "核对用的一件装备，不进仓库。"})
json.dump(items, open(os.path.join(base, "items.json"), "w", encoding="utf-8"), ensure_ascii=False)
crystal = json.load(open(os.path.join(base, "crystal.json"), encoding="utf-8"))
crystal["equipment_parts"]["k5_probe_blade"] = "weapon"
json.dump(crystal, open(os.path.join(base, "crystal.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY
ADDED="$(run_bad "$CASE" --item-list)"
if head -1 <<< "$ADDED" | grep -q "items=$((ITEM_COUNT + 1))" \
   && grep -q '^item=k5_probe_blade kind=equipment grade=blue .*name=核对用长刀 ' <<< "$ADDED"; then
  ok "加一件装备只改配置：清单 ${ITEM_COUNT} → $((ITEM_COUNT + 1))，品阶照配置读"
else
  bad "新装备没生效：$(head -2 <<< "$ADDED" | tr '\n' ' ')"
fi
ADDED_CS="$(run_bad "$CASE" --crystal-state)"
grep -q 'k5_probe_blade:weapon' <<< "$ADDED_CS" \
  && ok "新装备的部位也只改 crystal.json 就生效" || bad "新装备部位没生效"

finish "$TITLE"
