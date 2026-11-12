#!/usr/bin/env bash
# tools/check/check-p1-5c8.sh —— P1-5c8 解绑道具核对（方案 12.1 / 技术架构 6 节 /
# 开发架构第 14 节第 47 行）。
#
# 完成标准：买到后不能交易。符和卷轴用它解不开。绑定越久，张数越少。
#
#   bash tools/check/check-p1-5c8.sh             编译 + 静态 + 只读入口 + 坏表（不连库）
#   MMO_P1_5C8_GATEWAY=... MMO_P1_5C8_TOKEN=... MMO_P1_5C8_CHARACTER=<号> \
#     bash tools/check/check-p1-5c8.sh           再加真网关段
#   MMO_P1_5C8_SKIP_BUILD=1 ...                  跳过编译
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY="${MMO_P1_5C8_GATEWAY:-}"
TOKEN="${MMO_P1_5C8_TOKEN:-}"
CHARACTER="${MMO_P1_5C8_CHARACTER:-0}"
SHOP_ITEM="shop_unbind_item_1"
UNBIND="$(python3 -c 'import json;print(json.load(open("config/unbind_item.json", encoding="utf-8"))["unbind"]["item_id"])')"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

# 从「key=value key=value」日志行里按空格切词取一个字段（别用贪婪 sed）。
field_of() {
  printf '%s\n' "$1" | python3 -c 'import sys
want = sys.argv[1] + "="
for line in sys.stdin:
    for word in line.split():
        if word.startswith(want):
            print(word[len(want):]); raise SystemExit
print("")' "$2"
}

echo "== 1. 编译 =="
if [ "${MMO_P1_5C8_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_P1_5C8_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/Character/Character.csproj -c Release -v quiet > "$TMP/build.log" 2>&1 \
     && "$DOTNET" build src/Gateway/Gateway.csproj -c Release -v quiet >> "$TMP/build.log" 2>&1; then
  ok "编译 Character + Gateway"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言 =="
python3 - "$TMP/static.txt" <<'PY'
import json, os, re, sys

out = open(sys.argv[1], "w", encoding="utf-8")

def report(key, cond, info=""):
    out.write(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info) + "\n")

def read(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

F = {
    "config": "config/unbind_item.json", "table": "src/Character/UnbindTable.cs",
    "repo": "src/Character/UnbindRepository.cs", "svc": "src/Character/CharacterGrpcService.cs",
    "program": "src/Character/Program.cs", "process": "src/Character/CharacterProcess.cs",
    "config_cs": "src/Character/CharacterConfig.cs", "shop_cs": "src/Character/ShopTable.cs",
    "api": "src/Gateway/UnbindApi.cs", "gateway": "src/Gateway/GatewayProcess.cs",
    "proto": "proto/mmo/character.proto", "items": "config/items.json", "shop": "config/shop.json",
}
report("files", all(read(p) for p in F.values()),
       "missing " + ",".join(k for k, p in F.items() if not read(p)))

cfg = json.loads(read(F["config"])); items = json.loads(read(F["items"]))
shop = json.loads(read(F["shop"]))
unbind = cfg.get("unbind", {}); tiers = unbind.get("tiers") or []
item_id = unbind.get("item_id", "")
ordered = sorted(tiers, key=lambda t: -t.get("min_bound_hours", 0))

report("config", cfg.get("schema") == "unbind-table/1" and cfg.get("version", 0) >= 1
       and len(tiers) >= 1 and any(t.get("min_bound_hours") == 0 for t in tiers),
       "schema/version/tiers/0h 档")
report("tiers_monotonic", all(ordered[i].get("cost", 0) >= ordered[i - 1].get("cost", 0)
                              for i in range(1, len(ordered))), "cost 随时长不单调")

mine = [i for i in items["items"] if i.get("id") == item_id]
report("item", len(mine) == 1 and mine[0].get("kind") == "consumable"
       and mine[0].get("bind") == "always", str(mine[0]) if mine else "missing")

row = [i for i in shop["items"] if i.get("id") == "shop_unbind_item_1"]
report("shop", shop.get("version", 0) >= 7 and len(shop["items"]) == 18 and bool(row)
       and row[0].get("delivery_item_id") == item_id and row[0].get("delivery_count") == 1,
       str(row[0]) if row else "missing")

shop_cs = read(F["shop_cs"])
report("shop_rules", '"unbind_item"' in shop_cs.split("DeliveredKinds")[1].split("};")[0]
       and "unbind.item_id" in shop_cs, "DeliveredKinds / 发货物一致性")

proto = read(F["proto"])
report("proto", "rpc UseUnbindItem" in proto
       and all(k in proto for k in ("target_item_id", "age_hours", "floor_hit", "cost = 6")),
       "rpc / 消息字段")

svc = read(F["svc"]); repo = read(F["repo"])
reasons = ("not_unbind_item", "not_equipment", "not_bound", "slot_empty",
           "no_unbind_item", "not_enough_unbind")
report("service", "unbindTable.Find" in svc and all(r in svc for r in reasons),
       "缺 " + ",".join(r for r in reasons if r not in svc))
report("repository", "BeginTransactionAsync" in repo and "grant_txn" in repo
       and "set bound = false, bound_at = null" in repo and "EquipmentKind" in repo,
       "事务 / 幂等 / 清绑定 / 认装备")
report("no_attr_write", re.search(r"set\s+(level|exp|damage|spirit|attack|hp)\s*=", repo) is None,
       "动了属性 / 等级 / 经验")
report("no_new_table", "create table" not in repo.lower()
       and not os.path.exists("db/character/unbind.sql"), "新建了表")

report("gateway", '"/unbind/use"' in read(F["api"]) and "UnbindApi.Map" in read(F["gateway"]),
       "路由 / 注册")
program = read(F["program"])
report("readonly", all(k in program for k in
       ("--unbind-list", "--unbind-quote", "--unbind-state", "--raw-bound-at")),
       "只读 / 核对入口")
report("load_shared", program.count("UnbindTable.Load") >= 2
       and read(F["process"]).count("UnbindTable.Load") >= 1, "没共用 Load")
report("config_wired", "unbind_table" in read(F["config_cs"]), "CharacterConfig 没接")

code = read(F["table"]) + repo + svc + read(F["api"])
report("no_hardcoded", not re.search(r"\b(720|168)\b", code)
       and "sword_trial" not in code and "talisman_red" not in code, "写死了档位 / 编号")
report("single_source", sum(1 for p in ("config/items.json", "config/shop.json",
       "config/unbind_item.json") if item_id in read(p)) == 3, "道具编号不只出现在三份配置")
out.close()
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
grep -qE '^(PASS|FAIL) ' "$TMP/static.txt" || { bad "静态断言没跑起来"; tail -12 "$TMP/static.txt" | detail; }

echo "== 3. 只读入口与坏表 =="
LIST="$("$DOTNET" "$CHARACTER_DLL" --unbind-list 2>&1)"
if printf '%s' "$LIST" | grep -q '^unbind_list_state=ok' \
   && [ "$(field_of "$LIST" item)" = "$UNBIND" ]; then
  ok "解绑表读得出来：$(printf '%s' "$LIST" | head -1 | cut -c1-88)"
else
  bad "解绑表读不出来：$LIST"
fi

python3 - "$TMP" <<'PY'
import json, os, sys
tmp = sys.argv[1]
base = json.load(open("config/unbind_item.json", encoding="utf-8"))

def case(name, mutate):
    d = json.loads(json.dumps(base)); mutate(d)
    with open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False)

case("bad_schema", lambda d: d.update(schema="wrong-table/1"))
case("zero_version", lambda d: d.update(version=0))
case("no_unbind", lambda d: d.pop("unbind", None))
case("empty_item", lambda d: d["unbind"].update(item_id=""))
case("unknown_item", lambda d: d["unbind"].update(item_id="no_such_item"))
case("material_item", lambda d: d["unbind"].update(item_id="ore_iron"))
case("same_as_red", lambda d: d["unbind"].update(item_id="talisman_red"))
case("same_as_scroll", lambda d: d["unbind"].update(item_id="scroll_revive"))
case("same_as_booster", lambda d: d["unbind"].update(item_id="exp_booster"))
case("empty_tiers", lambda d: d["unbind"].update(tiers=[]))
case("neg_hours", lambda d: d["unbind"]["tiers"][0].update(min_bound_hours=-1))
case("zero_cost", lambda d: d["unbind"]["tiers"][0].update(cost=0))
case("no_zero_tier", lambda d: d["unbind"].update(tiers=[{"min_bound_hours": 1, "cost": 1}]))
case("non_monotonic", lambda d: d["unbind"].update(
    tiers=[{"min_bound_hours": 1000, "cost": 3}, {"min_bound_hours": 0, "cost": 1}]))
case("duplicate_hours", lambda d: d["unbind"].update(
    tiers=[{"min_bound_hours": 0, "cost": 2}, {"min_bound_hours": 0, "cost": 1}]))
PY

for bad_case in bad_schema zero_version no_unbind empty_item unknown_item material_item \
                same_as_red same_as_scroll same_as_booster empty_tiers neg_hours \
                zero_cost no_zero_tier non_monotonic duplicate_hours; do
  out="$("$DOTNET" "$CHARACTER_DLL" --unbind-list \
        --unbind-config "$TMP/bad-$bad_case.json" 2>&1)"
  assert_bad_config "$out" "坏表 ${bad_case}：起不来（$(printf '%s' "$out" | head -1 | cut -c1-44)…）"
done

echo "== 4. 真网关段（买 → 绑 → 解绑 → 拒绝 → 年龄档位）=="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （跳过：没给 MMO_P1_5C8_GATEWAY / _TOKEN / _CHARACTER）\n'
else
  post() {
    curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" \
      -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$2" 2>&1
  }
  get() { curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" "$1" 2>&1; }
  buy() { post "$GATEWAY/shop/buy" "{\"character_id\":$CHARACTER,\"item_id\":\"$1\"}"; }
  unbind() { post "$GATEWAY/unbind/use" "{\"character_id\":$CHARACTER,\"slot\":$1,\"item_id\":\"$2\"}"; }
  add_item() { post "$GATEWAY/inventory/add" "{\"character_id\":$CHARACTER,\"slot\":$1,\"item_id\":\"$2\",\"count\":$3}"; }
  can_trade() { post "$GATEWAY/inventory/can-trade" "{\"character_id\":$CHARACTER,\"slot\":$1}"; }
  bag() { get "$GATEWAY/inventory?character_id=$CHARACTER"; }
  money() { get "$GATEWAY/currency?character_id=$CHARACTER"; }
  state() { "$DOTNET" "$CHARACTER_DLL" --unbind-state "$CHARACTER" "$1" 2>&1; }
  quote() { "$DOTNET" "$CHARACTER_DLL" --unbind-quote "$CHARACTER" "$1" 2>&1; }
  age() { "$DOTNET" "$CHARACTER_DLL" --raw-bound-at "$CHARACTER" "$1" "$2" 2>&1; }
  # 回包断言：$1=响应 $2=key=val... $3=说明
  check() {
    if [ "$(expect_json "$(body_of "$1")" $2)" = "ok" ]; then ok "$3"; else bad "$3：$1"; fi
  }
  count_of() {
    printf '%s' "$1" | python3 -c 'import json, sys
try: d = json.load(sys.stdin)
except Exception: print(-1); raise SystemExit
print(sum(int(s.get("count") or 0) for s in d.get("slots", []) if s.get("item_id") == sys.argv[1]))' "$UNBIND"
  }
  yuanbao_of() {
    printf '%s' "$1" | python3 -c 'import json, sys
try: d = json.load(sys.stdin)
except Exception: print(""); raise SystemExit
print(next((b.get("amount") for b in d.get("balances", []) if b.get("kind") == "yuanbao"), ""))'
  }

  before_money="$(yuanbao_of "$(money)")"
  before_count="$(count_of "$(bag)")"

  # ① 买到后不能交易：扣元宝、入包、到账即绑、can-trade 拒绝。
  r="$(buy "$SHOP_ITEM")"; body="$(body_of "$r")"; slot="$(json_field "$body" bag_slot)"
  check "$r" "ok=true delivered=true bound=true delivery_item_id=$UNBIND" \
        "买解绑道具：入包第 ${slot} 格、到账即绑"
  after_money="$(yuanbao_of "$(money)")"
  [ -n "$before_money" ] && [ "$after_money" = "$((before_money - 120))" ] \
    && ok "元宝少 120（$before_money→$after_money）" || bad "元宝不对：$before_money→$after_money"
  [ "$(count_of "$(bag)")" = "$((before_count + 1))" ] \
    && ok "背包解绑道具 +1" || bad "背包张数不对"
  check "$(can_trade "$slot")" "tradable=false reason=bound_item" "买到的解绑道具不能交易（bound_item）"

  # ② 符和卷轴用它解不开：各放一件，解绑道具去解，绑定态不许动。
  add_item 11 talisman_red 1 > /dev/null
  add_item 12 scroll_revive 1 > /dev/null
  for pair in "11 红符" "12 卷轴"; do
    set -- $pair
    r="$(unbind "$1" "$UNBIND")"
    check "$r" "ok=false reason=not_equipment" "$2 用它解不开（not_equipment）"
    [ "$(field_of "$(state "$1")" bound)" = "true" ] \
      && ok "$2 的绑定态没动" || bad "$2 的绑定态被改了"
  done

  # ③ 绑定越久张数越少：0 小时 3 张、200 小时 2 张、900 小时到下限 1 张。
  add_item 5 sword_trial 1 > /dev/null
  age 5 0 > /dev/null;    cost0="$(field_of "$(quote 5)" cost)"
  age 5 200 > /dev/null;  cost200="$(field_of "$(quote 5)" cost)"
  age 5 900 > /dev/null;  q900="$(quote 5)"; cost900="$(field_of "$q900" cost)"
  [ "$cost0" -gt "$cost200" ] && [ "$cost200" -gt "$cost900" ] \
    && [ "$(field_of "$q900" floor_hit)" = "true" ] \
    && ok "张数随绑定时间递减：0h=${cost0} 200h=${cost200} 900h=${cost900}（下限）" \
    || bad "年龄档位不对：0h=$cost0 200h=$cost200 900h=$cost900"

  # 张数不够：现在只有 1 张，0 小时那档要 3 张；拒绝后绑定态与张数都不许动。
  age 5 0 > /dev/null
  r="$(unbind 5 "$UNBIND")"
  check "$r" "ok=false reason=not_enough_unbind" "张数不够：not_enough_unbind"
  [ "$(field_of "$(state 5)" bound)" = "true" ] && ok "张数不够时绑定态没动" || bad "绑定态被改了"

  # 补到 3 张，把年龄挪到 900 小时（下限 1 张）真解开，解绑后可交易。
  add_item 13 "$UNBIND" 2 > /dev/null
  age 5 900 > /dev/null
  r="$(unbind 5 "$UNBIND")"
  check "$r" "ok=true cost=1 target_item_id=sword_trial" "900 小时那档用 1 张解开"
  [ "$(field_of "$(state 5)" bound)" = "false" ] && ok "解绑后 bound=false" || bad "解绑后仍绑定"
  check "$(can_trade 5)" "tradable=true" "解绑后可交易"

  r="$(unbind 5 "$UNBIND")"
  check "$r" "ok=false reason=not_bound" "同一件再解一次：not_bound"

  # 空格子 / 编号不对。
  check "$(unbind 7 "$UNBIND")" "ok=false reason=slot_empty" "空格子：slot_empty"
  check "$(unbind 5 talisman_red)" "ok=false reason=not_unbind_item" "拿符当解绑道具：not_unbind_item"

  # 把剩下的 2 张用完，再验「背包没有解绑道具」。
  add_item 8 sword_trial 1 > /dev/null;  age 8 900 > /dev/null
  check "$(unbind 8 "$UNBIND")" "ok=true cost=1" "第二件用 1 张"
  add_item 9 sword_trial 1 > /dev/null;  age 9 900 > /dev/null
  check "$(unbind 9 "$UNBIND")" "ok=true cost=1" "第三件用掉最后 1 张"
  add_item 10 sword_trial 1 > /dev/null
  check "$(unbind 10 "$UNBIND")" "ok=false reason=no_unbind_item" "背包没有解绑道具：no_unbind_item"
fi

finish "P1-5c8（server）"
