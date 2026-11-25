#!/usr/bin/env bash
# B9 核对：装备穿戴。
#
# 完成标准（开发架构 14.5 第 3 行 / 任务队列 #34）：
#   能穿 / 脱；同部位一件；职业与等级不符被拒；穿上后副本伤害读装备；
#   卖装备不会把强化留在格子里。
#
# 两种模式：
#   bash tools/check/check-b9.sh                        静态段（不连库，CI 跑这一段）
#   MMO_B9_HAS_DB=1 bash tools/check/check-b9.sh        加真库段（要 gamedb）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
PROBE_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
GLOBAL_DLL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"
HAS_DB="${MMO_B9_HAS_DB:-0}"

latest_version() {
  python3 - "$ROOT/db/migrations.json" "$1" <<'PY2'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
print([s for s in d["schemas"] if s["key"] == sys.argv[2]][0]["entries"][-1]["version"])
PY2
}

TITLE="B9"

CHAR_PORT=${MMO_B9_CHAR_PORT:-62145}
GATEWAY_PORT=${MMO_B9_GATEWAY_PORT:-61146}
GLOBAL_PORT=${MMO_B9_GLOBAL_PORT:-64046}
ACCOUNT="p4b9_wear"
TEST_PASSWORD="p4b9-Pw-7c31"
CHAR_W="p4b9a$RANDOM"
CHAR_M="p4b9b$RANDOM"
CHARACTER_PID=""
GATEWAY_PID=""
GLOBAL_PID=""
TOKEN=""
WAR=""
MAGE=""
TMP="$(mktemp -d)"

cleanup() {
  stop_pid "$GATEWAY_PID"
  stop_pid "$GLOBAL_PID"
  stop_pid "$CHARACTER_PID"
  if [ -n "${HAVE_DB:-}" ]; then
    for one in "$CHAR_W" "$CHAR_M"; do
      "$DOTNET" "$PROBE_DLL" --delete-character "$one" --config "$TMP/character.json" >/dev/null 2>&1 || true
    done
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

echo "== 1. 编译与静态断言 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Battle/Battle.csproj src/Global/Global.csproj \
            src/Gateway/Gateway.csproj tools/probe/CliProbe/CliProbe.csproj; do
  name="$(basename "$proj" .csproj)"
  "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$name.log" 2>&1 || {
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E 'error ' "$TMP/build-$name.log" | head -4 | detail
  }
done
[ "$BUILD_OK" = "1" ] && ok "角色服、战斗服、全局服务、网关与探针编译通过"

if ROOT="$ROOT" python3 "$ROOT/tools/check/check-b9-static.py" >"$TMP/static.out" 2>&1; then
  ok "两条 RPC 在、判定只有一个出口、穿戴表跟着背包行走、装备攻击只来自物品表"
  sed 's/^/       /' "$TMP/static.out"
else
  bad "静态断言失败"
  tail -8 "$TMP/static.out" | detail
fi

# 副本伤害第一段真的把「装备固定攻击」吃进来：同一套 effects 配置下，
# 基础攻击随传进去的攻击线性变高（B10 会在这条链上再接强化与结晶）。
DMG_BASE="$("$DOTNET" "$ROOT/src/Battle/bin/Release/net10.0/Battle.dll" --damage-preview \
  --attack 200 --multiplier 1 --defense 0 --config config/battle.json 2>&1)"
DMG_EQUIP="$("$DOTNET" "$ROOT/src/Battle/bin/Release/net10.0/Battle.dll" --damage-preview \
  --attack 220 --multiplier 1 --defense 0 --config config/battle.json 2>&1)"
B="$(printf '%s' "$DMG_BASE" | sed -n 's/^damage_stage=base value=\(.*\)/\1/p')"
E="$(printf '%s' "$DMG_EQUIP" | sed -n 's/^damage_stage=base value=\(.*\)/\1/p')"
if [ -n "$B" ] && [ -n "$E" ] && python3 -c "import sys;sys.exit(0 if float('$E')>float('$B') else 1)"; then
  ok "副本伤害第一段吃装备固定攻击：基础攻击 ${B} → ${E}（+20 装备攻击）"
else
  bad "基础攻击没有随装备攻击变高：[$B] → [$E]"
fi

if [ "$HAS_DB" != "1" ] || { [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; }; then
  printf '  ..   跳过真库段（MMO_B9_HAS_DB 未开或缺 gamedb 凭据）\n'
  finish "$TITLE"
fi
echo "== 2. 迁移与临时配置 =="
HAVE_DB=1
"$DOTNET" "$PROBE_DLL" --migrate --config config/character.json >"$TMP/migrate.log" 2>&1
if grep -q '^migrate=ok' "$TMP/migrate.log"; then
  ok "迁移清单跑通（角色 → 社交 → 交易）"
else
  bad "迁移失败"; tail -8 "$TMP/migrate.log" | detail; finish "$TITLE"
fi

STATUS="$("$DOTNET" "$PROBE_DLL" --schema-status --config config/character.json 2>&1)"
# 版本号现取清单：本卡加了 v29，但不写死，后面再加迁移不会在这里红。
ROLE_V="$(latest_version character)"
if printf '%s' "$STATUS" | grep -q "schema_status=ok schema=角色 .*latest_version=${ROLE_V} "; then
  ok "版本表记到 角色 v${ROLE_V}（含本卡的穿戴表），没有 drift"
else
  bad "版本表不是 角色 v${ROLE_V}"; printf '%s' "$STATUS" | grep schema_status | detail; finish "$TITLE"
fi

ROOT="$ROOT" TMP="$TMP" CHAR_PORT="$CHAR_PORT" GATEWAY_PORT="$GATEWAY_PORT" \
GLOBAL_PORT="$GLOBAL_PORT" python3 - <<'PY'
import json, os
tmp = os.environ['TMP']
c = {
    'schema': 'character-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': 'config/dungeons.json', 'exp_table': 'config/exp.json',
    'enhance_table': 'config/enhance.json', 'transfer_table': 'config/transfer.json',
    'auction_table': 'config/auction.json',
}
json.dump(c, open(tmp + '/character.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
g = {
    'schema': 'global-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['GLOBAL_PORT'],
    'db_pool_max': 6,
    'character_upstream': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
}
json.dump(g, open(tmp + '/global.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
a = {
    'schema': 'gateway-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['GATEWAY_PORT'],
    'token_ttl_s': 7200, 'pbkdf2_iterations': 210000,
    'battle_upstream': 'ws://127.0.0.1:60001/battle',
    'character_upstream': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'world_upstream': 'http://127.0.0.1:63108',
    'global_upstream': 'http://127.0.0.1:' + os.environ['GLOBAL_PORT'],
}
json.dump(a, open(tmp + '/gateway.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$ACCOUNT" \
  --seed-slots 8 --config config/gateway.json >>"$TMP/seed.log" 2>&1
ok "测试账号已播种"

echo "== 3. 起角色服、全局服务与网关 =="
"$DOTNET" "$PROBE_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
"$DOTNET" "$GLOBAL_DLL" --config "$TMP/global.json" >"$TMP/global.log" 2>&1 & GLOBAL_PID=$!
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & GATEWAY_PID=$!
if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
   && wait_ready "$TMP/global.log" '^ready name=Global' "全局服务" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关"; then
  ok "三个进程起来"
else
  tail -12 "$TMP/character.log" | detail; finish "$TITLE"
fi

post() {
  curl -s -m 10 -w '\n%{http_code}' -X POST "http://127.0.0.1:${GATEWAY_PORT}$1?token=${TOKEN}" \
    -H 'Content-Type: application/json' -d "$2"
}

get_bag() {
  curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=${TOKEN}&character_id=$1"
}

expect_field() {
  local want="$1" label="$2" got
  # 布尔按 JSON 的小写打出来：老写法 print(True) 出来是 Python 的 True，
  # 于是 expect_field true 一律假红（A9 顺手修，判据不变）。
  got="$(printf '%s' "$3" | python3 -c "import json,sys
key=sys.argv[1]
try:
    v = json.load(sys.stdin).get(key, '')
    print('true' if v is True else 'false' if v is False else ('' if v is None else v))
except Exception: print('')" "$4")"
  if [ "$got" = "$want" ]; then ok "$label"; else bad "${label}：$4=[$got] 期望=[$want]"; fi
}

# 不看回包，按格从库里读回：背包行 id 与强化等级（C4 的 enhance_row）。
row_of() {
  "$DOTNET" "$PROBE_DLL" --enhance-row "$1" "$2" --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^enhance_row=ok .* inventory_id=\([0-9]*\) item=[^ ]* level=\([0-9]*\)/\1 \2/p'
}

# 不看回包，直接读穿戴表：`worn=部位:格号:物品,...  attack=N`。
equip_row() {
  "$DOTNET" "$PROBE_DLL" --equip-row "$1" --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^equip_row=ok //p'
}

# 某一格现在穿着哪个部位：只认 /inventory 回包里的 equipped_slot。
worn_of() {
  get_bag "$1" | python3 -c 'import json,sys
slot=int(sys.argv[1])
try: data=json.load(sys.stdin)
except Exception: print("-"); raise SystemExit
for one in data.get("slots", []):
    if int(one.get("slot", 0)) == slot:
        print(one.get("equipped_slot", "") or "-"); break
else:
    print("-")' "$2"
}

mkchar() {
  local name="$1" cls="$2"
  curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/character?token=${TOKEN}" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$name\",\"race\":\"mai\",\"class_id\":\"$cls\"}" \
    | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("character_id",""))
except Exception: print("")'
}

lv() { [ -n "${1:-}" ] || return 0; "$DOTNET" "$PROBE_DLL" --raw-level-write "$1" "$2" --config "$TMP/character.json" >/dev/null 2>&1; }

echo "== 4. 造料：战士（剑 / 长刀 / 法袍 / 矿）与法师（法杖 / 法剑） =="
TOKEN="$(login_token "$ACCOUNT")"
[ -n "$TOKEN" ] || { bad "登录没拿到令牌"; finish "$TITLE"; }
WAR="$(mkchar "$CHAR_W" warrior)"
MAGE="$(mkchar "$CHAR_M" mage)"
[ -n "$WAR" ] && [ -n "$MAGE" ] || { bad "角色没建起来"; finish "$TITLE"; }
ok "两个角色已建：战士=$WAR 法师=$MAGE"
post /inventory/add "{\"character_id\":$WAR,\"slot\":7,\"item_id\":\"sword_trial\",\"count\":1}" >/dev/null
post /inventory/add "{\"character_id\":$WAR,\"slot\":9,\"item_id\":\"sword_trade\",\"count\":1}" >/dev/null
post /inventory/add "{\"character_id\":$WAR,\"slot\":11,\"item_id\":\"robe_trial\",\"count\":1}" >/dev/null
post /inventory/add "{\"character_id\":$WAR,\"slot\":13,\"item_id\":\"ore_iron\",\"count\":5}" >/dev/null
post /inventory/add "{\"character_id\":$MAGE,\"slot\":7,\"item_id\":\"staff_trade\",\"count\":1}" >/dev/null
post /inventory/add "{\"character_id\":$MAGE,\"slot\":9,\"item_id\":\"spirit_sword_trade\",\"count\":1}" >/dev/null

echo "== 5. 等级门槛与武器锁：穿之前先被拒 =="
lv "$WAR" 12
RESP="$(post /character/equip "{\"character_id\":$WAR,\"slot\":9}")"
expect_field level_not_enough "12 级战士穿 15 级长刀：level_not_enough" "$(body_of "$RESP")" reason
expect_field "" "被拒时不写穿戴表（equip_slot 为空）" "$(body_of "$RESP")" equip_slot
RESP="$(post /character/equip "{\"character_id\":$WAR,\"slot\":13}")"
expect_field not_equipment "矿石不是装备：not_equipment" "$(body_of "$RESP")" reason

RESP="$(post /character/equip "{\"character_id\":$WAR,\"slot\":7}")"
expect_field true "12 级战士能穿 10 级剑（不到 15 级全职业）" "$(body_of "$RESP")" ok
expect_field weapon "回包给部位 weapon" "$(body_of "$RESP")" equip_slot
expect_field 20 "装备固定攻击读物品表：20" "$(body_of "$RESP")" equip_attack
[ "$(worn_of "$WAR" 7)" = "weapon" ] && ok "背包回包在这一格标了 weapon" || bad "回包没标穿戴：$(worn_of "$WAR" 7)"
[ "$(equip_row "$WAR")" = "worn=weapon:7:sword_trial attack=20" ] \
  && ok "库里真有一行穿戴：weapon → 第 7 格" || bad "穿戴表不对：$(equip_row "$WAR")"

RESP="$(post /character/equip "{\"character_id\":$WAR,\"slot\":7}")"
expect_field already_equipped "同一件再点一次：already_equipped，不重复写" "$(body_of "$RESP")" reason

echo "== 6. 同部位一件：换另一件被拒，先脱再穿 =="
lv "$WAR" 45
RESP="$(post /character/equip "{\"character_id\":$WAR,\"slot\":9}")"
expect_field slot_occupied "武器部位已穿剑，再穿长刀：slot_occupied" "$(body_of "$RESP")" reason
RESP="$(post /character/unequip "{\"character_id\":$WAR,\"equip_slot\":\"weapon\"}")"
expect_field true "先脱下" "$(body_of "$RESP")" ok
expect_field 7 "脱下不换格：还是第 7 格" "$(body_of "$RESP")" bag_slot
expect_field 0 "脱下后装备攻击归零" "$(body_of "$RESP")" equip_attack
[ "$(worn_of "$WAR" 7)" = "-" ] && ok "回包里这一格不再标穿戴" || bad "脱下后回包还标着部位：$(worn_of "$WAR" 7)"

RESP="$(post /character/equip "{\"character_id\":$WAR,\"slot\":9}")"
expect_field true "45 级战士穿 15 级长刀：ok" "$(body_of "$RESP")" ok
expect_field 40 "长刀的攻击 40" "$(body_of "$RESP")" equip_attack
RESP="$(post /character/equip "{\"character_id\":$WAR,\"slot\":11}")"
expect_field true "法袍是护甲部位，和武器不冲突" "$(body_of "$RESP")" ok
expect_field 60 "两件一起穿：装备攻击 40+20" "$(body_of "$RESP")" equip_attack
[ "$(equip_row "$WAR")" = "worn=armor:11:robe_trial,weapon:9:sword_trade attack=60" ] \
  && ok "库里两行：armor 与 weapon 各一件" || bad "穿戴表不对：$(equip_row "$WAR")"

RESP="$(post /character/unequip "{\"character_id\":$WAR,\"equip_slot\":\"accessory\"}")"
expect_field slot_empty "这个部位没穿东西：slot_empty（业务拒绝，200）" "$(body_of "$RESP")" reason
if [ "$(code_of "$RESP")" = "200" ]; then ok "业务拒绝走 200 + ok=false"; else
  bad "空部位被按参数错拒：HTTP $(code_of "$RESP")"; fi

RESP="$(post /character/equip "{\"character_id\":$WAR,\"slot\":41}")"
if [ "$(code_of "$RESP")" = "400" ]; then ok "越过容量的格号按参数错拒（400）"; else
  bad "越界格号没有按参数错拒：HTTP $(code_of "$RESP")"; fi
RESP="$(post /character/equip "{\"character_id\":$WAR,\"slot\":30}")"
expect_field slot_empty "空格：slot_empty" "$(body_of "$RESP")" reason

echo "== 7. 职业不符：法师的兵器战士摸不得 =="
lv "$MAGE" 45
post /inventory/add "{\"character_id\":$MAGE,\"slot\":5,\"item_id\":\"sword_trade\",\"count\":1}" >/dev/null
RESP="$(post /character/equip "{\"character_id\":$MAGE,\"slot\":5}")"
expect_field class_mismatch "法师穿 15 级长刀：class_mismatch" "$(body_of "$RESP")" reason
RESP="$(post /character/equip "{\"character_id\":$MAGE,\"slot\":7}")"
expect_field true "法师穿法杖：ok" "$(body_of "$RESP")" ok
RESP="$(post /character/equip "{\"character_id\":$MAGE,\"slot\":9}")"
expect_field slot_occupied "法剑同属武器部位：slot_occupied" "$(body_of "$RESP")" reason

echo "== 8. 换格 / 卖装备：强化跟着背包行，不会留在格子里 =="
"$DOTNET" "$PROBE_DLL" --raw-enhance-write "$WAR" 9 5 --config "$TMP/character.json" >/dev/null 2>&1
ROW_BEFORE="$(row_of "$WAR" 9)"
[ -n "$ROW_BEFORE" ] && ok "第 9 格的长刀先写到 +5（穿着状态下）" || bad "强化写不进去"
RESP="$(post /inventory/move "{\"character_id\":$WAR,\"from_slot\":9,\"to_slot\":21}")"
expect_field true "穿着时长刀换格：移动成功" "$(body_of "$RESP")" ok
[ "$(worn_of "$WAR" 21)" = "weapon" ] \
  && ok "换格后穿戴仍指着那一件（第 21 格）" || bad "换格后穿戴丢了：$(worn_of "$WAR" 21)"
[ "$(row_of "$WAR" 21 | awk '{print $2}')" = "5" ] \
  && ok "换格后强化还是 +5（等级跟着背包行）" || bad "强化丢了：$(row_of "$WAR" 21)"

# 卖装备 = 这一行没了：穿戴记录跟着 CASCADE，强化也一起走。
RESP="$(post /inventory/discard "{\"character_id\":$WAR,\"slot\":21,\"count\":1,\"confirm\":true,\"txn_id\":\"b9-sell-1\"}")"
expect_field true "穿着时长刀被卖掉（丢光这一格）" "$(body_of "$RESP")" ok
[ "$(equip_row "$WAR")" = "worn=armor:11:robe_trial attack=20" ] \
  && ok "卖掉的装备不再占武器部位（穿戴记录跟着行删）" || bad "槽位还指着已删的装备：$(equip_row "$WAR")"
[ -z "$(row_of "$WAR" 21)" ] && ok "强化行跟着背包行走掉，没有悬空等级" || bad "强化留在格子里了"
post /inventory/add "{\"character_id\":$WAR,\"slot\":21,\"item_id\":\"sword_trade\",\"count\":1}" >/dev/null
[ "$(row_of "$WAR" 21 | awk '{print $2}')" = "0" ] \
  && ok "新进这一格的装备强化是 +0（上一件的等级没留下）" || bad "强化串到新装备上了"

echo "== 9. 穿上后副本读装备（角色服快照的事实值） =="
RESP="$(post /character/equip "{\"character_id\":$WAR,\"slot\":21}")"
expect_field 40 "重新穿上长刀：装备攻击 40" "$(body_of "$RESP")" equip_attack
[ "$(equip_row "$WAR")" = "worn=armor:11:robe_trial,weapon:21:sword_trade attack=60" ] \
  && ok "战斗服读到的 equip_attack 就是这 60（同一口数据）" || bad "穿戴表不对：$(equip_row "$WAR")"

finish "$TITLE"
