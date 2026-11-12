#!/usr/bin/env bash
# tools/check/check-p1-5c4.sh —— P1-5c4 传送符绑定发货（方案 12.1 / 技术架构 6 节 /
# 开发架构第 14 节第 48 行）。
#
# 完成标准：买到的传送符进入背包，并且不能交易。
#
#   bash tools/check/check-p1-5c4.sh             编译 + 静态 + 只读入口 + 坏清单（不连库）
#   MMO_P1_5C4_GATEWAY=... MMO_P1_5C4_TOKEN=... MMO_P1_5C4_CHARACTER=<号> \
#     bash tools/check/check-p1-5c4.sh           再加真网关段（买 → 绑 → 不能交易）
#   MMO_P1_5C4_SKIP_BUILD=1 ...                  跳过编译
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
GATEWAY="${MMO_P1_5C4_GATEWAY:-}"
TOKEN="${MMO_P1_5C4_TOKEN:-}"
CHARACTER="${MMO_P1_5C4_CHARACTER:-0}"
SHOP_ITEM="shop_teleport_talisman_1"
TALISMAN="$(python3 -c 'import json; d=json.load(open("config/shop.json", encoding="utf-8")); print(next(i["delivery_item_id"] for i in d["items"] if i["id"] == "shop_teleport_talisman_1"))')"

TMP="$(mktemp -d)"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

echo "== 1. 编译 =="
if [ "${MMO_P1_5C4_SKIP_BUILD:-0}" = "1" ]; then
  printf '  ..   跳过编译（MMO_P1_5C4_SKIP_BUILD=1）\n'
elif "$DOTNET" build src/Character/Character.csproj -c Release -v quiet > "$TMP/build.log" 2>&1; then
  ok "编译 Character"
else
  bad "编译失败"; tail -16 "$TMP/build.log" | detail
fi

echo "== 2. 静态断言 =="
python3 - "$TMP/static.txt" "$TALISMAN" <<'PY'
import json, os, sys

out = open(sys.argv[1], "w", encoding="utf-8")
talisman = sys.argv[2]

def report(key, cond, info=""):
    out.write(("PASS " if cond else "FAIL ") + key + ("" if cond else "  " + info) + "\n")

def read(p):
    return open(p, encoding="utf-8").read() if os.path.exists(p) else ""

items = json.loads(read("config/items.json"))
shop = json.loads(read("config/shop.json"))
table_cs = read("src/Character/ShopTable.cs")

mine = [i for i in items["items"] if i.get("id") == talisman]
report("item_unique", len(mine) == 1, "matches=%d" % len(mine))
report("item_consumable", bool(mine) and mine[0].get("kind") == "consumable",
       mine[0].get("kind") if mine else "missing")
report("item_always_bound", bool(mine) and mine[0].get("bind") == "always",
       mine[0].get("bind") if mine else "missing")

row = [i for i in shop["items"] if i.get("id") == "shop_teleport_talisman_1"]
report("shop_delivery", bool(row) and row[0].get("delivery_item_id") == talisman
       and row[0].get("delivery_count") == 1, str(row[0]) if row else "missing")
report("shop_items_18", len(shop["items"]) == 18, "items=%d" % len(shop["items"]))
report("shop_price", bool(row) and row[0].get("price_yuanbao") == 20,
       str(row[0].get("price_yuanbao")) if row else "missing")

delivered = table_cs.split("DeliveredKinds")[1].split("};")[0]
report("delivered_kind", '"teleport_talisman"' in delivered, "DeliveredKinds 不含传送符")
branch = table_cs.split('"teleport_talisman", StringComparison.Ordinal')[1].split("else if")[0] \
    if '"teleport_talisman", StringComparison.Ordinal' in table_cs else ""
report("branch", "consumable" in branch and "BindRule.Always" in branch,
       "ShopTable 没校验传送符发货物")

report("single_source", sum(1 for p in ("config/items.json", "config/shop.json")
       if talisman in read(p)) == 2, "编号不只出现在 items / shop")
report("no_new_table", not os.path.exists("db/character/teleport.sql"), "新建了表")
report("no_proto", "teleport" not in read("proto/mmo/character.proto").lower(),
       "这一张不该动 proto")
out.close()
PY

while read -r tag key rest; do
  case "$tag" in
    PASS) ok "$key" ;;
    FAIL) bad "$key${rest:+ $rest}" ;;
  esac
done < "$TMP/static.txt"
grep -qE '^(PASS|FAIL) ' "$TMP/static.txt" || { bad "静态断言没跑起来"; tail -12 "$TMP/static.txt" | detail; }

echo "== 3. 只读入口与坏清单 =="
LIST="$("$DOTNET" "$CHARACTER_DLL" --shop-list 2>&1)"
if printf '%s' "$LIST" | grep -q "^shop_item=${SHOP_ITEM} .* delivery=${TALISMAN}x1 " \
   && printf '%s' "$LIST" | head -1 | grep -q 'shop_list_state=ok'; then
  ok "清单里传送符发 ${TALISMAN}x1（$(printf '%s' "$LIST" | head -1 | cut -c1-60)…）"
else
  bad "清单里的传送符不对：$(printf '%s' "$LIST" | grep "$SHOP_ITEM")"
fi

# 坏清单：发货物不是消耗品 / 不是按张卖 / 没接发货却留了发货物，都该起不来。
# 传送符没有自己的数值配置，编号只在 items.json 与 shop.json；「发成别的消耗品」
# 不在 Load 里挡（和 outfit 这类同一个口径），由静态断言与核对钉住具体编号。
python3 - "$TMP" <<'PY'
import json, os, sys

tmp = sys.argv[1]
base = json.load(open("config/shop.json", encoding="utf-8"))

def case(name, mutate):
    d = json.loads(json.dumps(base))
    row = next(i for i in d["items"] if i["id"] == "shop_teleport_talisman_1")
    mutate(row)
    with open(os.path.join(tmp, "bad-" + name + ".json"), "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False)

case("material_delivery", lambda r: r.update(delivery_item_id="ore_iron"))
case("count_two", lambda r: r.update(delivery_count=2))
case("no_delivery", lambda r: r.update(delivery_item_id="", delivery_count=0))
PY

for bad_case in material_delivery count_two no_delivery; do
  out="$("$DOTNET" "$CHARACTER_DLL" --shop-list --shop-config "$TMP/bad-$bad_case.json" 2>&1)"
  assert_bad_config "$out" "坏清单 ${bad_case}：起不来（$(printf '%s' "$out" | head -1 | cut -c1-44)…）"
done

echo "== 4. 真网关段（买 → 入包 → 绑定 → 不能交易）=="
if [ -z "$GATEWAY" ] || [ -z "$TOKEN" ] || [ "$CHARACTER" = "0" ]; then
  printf '       （跳过：没给 MMO_P1_5C4_GATEWAY / _TOKEN / _CHARACTER）\n'
else
  post() {
    curl -sS --noproxy '*' -m 10 -w '\n%{http_code}' -X POST "$1" \
      -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$2" 2>&1
  }
  get() { curl -sS --noproxy '*' -m 10 -H "Authorization: Bearer $TOKEN" "$1" 2>&1; }
  buy() { post "$GATEWAY/shop/buy" "{\"character_id\":$CHARACTER,\"item_id\":\"$SHOP_ITEM\"}"; }
  bag() { get "$GATEWAY/inventory?character_id=$CHARACTER"; }
  money() { get "$GATEWAY/currency?character_id=$CHARACTER"; }
  check() {
    if [ "$(expect_json "$(body_of "$1")" $2)" = "ok" ]; then ok "$3"; else bad "$3：$1"; fi
  }
  count_of() {
    printf '%s' "$1" | python3 -c 'import json, sys
try: d = json.load(sys.stdin)
except Exception: print(-1); raise SystemExit
print(sum(int(s.get("count") or 0) for s in d.get("slots", []) if s.get("item_id") == sys.argv[1]))' "$TALISMAN"
  }
  yuanbao_of() {
    printf '%s' "$1" | python3 -c 'import json, sys
try: d = json.load(sys.stdin)
except Exception: print(""); raise SystemExit
print(next((b.get("amount") for b in d.get("balances", []) if b.get("kind") == "yuanbao"), ""))'
  }

  before_money="$(yuanbao_of "$(money)")"
  before_count="$(count_of "$(bag)")"

  # ① 买一张：扣 20 元宝、进背包、到账即绑。
  r="$(buy)"; body="$(body_of "$r")"; slot="$(json_field "$body" bag_slot)"
  check "$r" "ok=true delivered=true bound=true delivery_item_id=$TALISMAN charged_yuanbao=20" \
        "买传送符：入包第 ${slot} 格、到账即绑、扣 20 元宝"
  after_money="$(yuanbao_of "$(money)")"
  [ -n "$before_money" ] && [ "$after_money" = "$((before_money - 20))" ] \
    && ok "元宝少 20（$before_money→$after_money）" || bad "元宝不对：$before_money→$after_money"
  [ "$(count_of "$(bag)")" = "$((before_count + 1))" ] \
    && ok "背包传送符 +1" || bad "背包张数不对"

  # ② 不能交易：can-trade 与上架都回 bound_item。
  check "$(post "$GATEWAY/inventory/can-trade" "{\"character_id\":$CHARACTER,\"slot\":$slot}")" \
        "tradable=false reason=bound_item" "这一格不能直接交易（bound_item）"
  check "$(post "$GATEWAY/auction/list" \
          "{\"character_id\":$CHARACTER,\"slot\":$slot,\"count\":1,\"price_gold\":10}")" \
        "ok=false reason=bound_item" "拿传送符上架被拒（bound_item）"

  # ③ 可反复购买：再买一张还是同一格，张数 +1、元宝再少 20。
  r="$(buy)"
  check "$r" "ok=true delivered=true bound=true" "再买一张：仍到账即绑"
  [ "$(count_of "$(bag)")" = "$((before_count + 2))" ] \
    && ok "背包传送符 +2（可反复购买）" || bad "重复购买张数不对"
  after_money="$(yuanbao_of "$(money)")"
  [ "$after_money" = "$((before_money - 40))" ] \
    && ok "两张共扣 40 元宝" || bad "两张的扣款不对：$before_money→$after_money"
fi

finish "P1-5c4（server）"
