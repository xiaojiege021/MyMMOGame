#!/usr/bin/env bash
# C4 核对：强化跟着装备走，不跟格子。
#
# 完成标准（开发架构 14.5 第 2 行 + 任务队列 #32）：
#   同一件装备换格、卖出、上架后强化不丢不串；转移按装备而不是格子；+12 装备可以上架。
#
# 两种模式：
#   bash tools/check/check-c4.sh                                  静态段（不连库，CI 跑这一段）
#   MMO_C4_HAS_DB=1 bash tools/check/check-c4.sh                  加真库段（要 gamedb）
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
export ROOT
source "$ROOT/tools/check/lib-common.sh"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
CHARACTER_DLL="$ROOT/tools/probe/CliProbe/bin/Release/net10.0/CliProbe.dll"
GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
GLOBAL_DLL="$ROOT/src/Global/bin/Release/net10.0/Global.dll"
HAS_DB="${MMO_C4_HAS_DB:-0}"

latest_version() {
  python3 - "$ROOT/db/migrations.json" "$1" <<'PY2'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
print([s for s in d["schemas"] if s["key"] == sys.argv[2]][0]["entries"][-1]["version"])
PY2
}

TITLE="C4"

CHAR_PORT=${MMO_C4_CHAR_PORT:-62132}
GATEWAY_PORT=${MMO_C4_GATEWAY_PORT:-61133}
GLOBAL_PORT=${MMO_C4_GLOBAL_PORT:-64033}
WORLD_PORT=${MMO_C4_WORLD_PORT:-63107}
ACCOUNT="p4c4_seller"
OTHER="p4c4_buyer"
TEST_PASSWORD="p4c4-Pw-2b31"
CHAR_NAMES=("p4c4_a" "p4c4_b")
CHARACTER_PID=""
GATEWAY_PID=""
GLOBAL_PID=""
TOKEN=""
OTHER_TOKEN=""
ID=""
OTHER_ID=""
NEW_ID=""
TMP="$(mktemp -d)"

cleanup() {
  stop_pid "$GATEWAY_PID"
  stop_pid "$GLOBAL_PID"
  stop_pid "$CHARACTER_PID"
  if [ -n "${HAVE_DB:-}" ]; then
    for name in "${CHAR_NAMES[@]}"; do
      "$DOTNET" "$CHARACTER_DLL" --delete-character "$name" --config "$TMP/character.json" >/dev/null 2>&1 || true
    done
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$OTHER" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

echo "== 1. 编译与静态断言 =="
BUILD_OK=1
for proj in src/Character/Character.csproj src/Global/Global.csproj src/Gateway/Gateway.csproj \
            tools/probe/CliProbe/CliProbe.csproj; do
  name="$(basename "$proj" .csproj)"
  "$DOTNET" build "$proj" -c Release --nologo -v q >"$TMP/build-$name.log" 2>&1 || {
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E 'error ' "$TMP/build-$name.log" | head -4 | detail
  }
done
[ "$BUILD_OK" = "1" ] && ok "角色服、全局服务、网关编译通过"

if ROOT="$ROOT" python3 "$ROOT/tools/check/check-c4-static.py" >"$TMP/static.out" 2>&1; then
  ok "等级挂 inventory_id、挂单记 item_id、协议面未动、数值仍只在配置"
  sed 's/^/       /' "$TMP/static.out"
else
  bad "静态断言失败"
  tail -6 "$TMP/static.out" | detail
fi

if [ "$HAS_DB" != "1" ] || { [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; }; then
  printf '  ..   跳过真库段（MMO_C4_HAS_DB 未开或缺 gamedb 凭据）\n'
  finish "$TITLE"
fi

echo "== 2. 迁移与临时配置 =="
HAVE_DB=1
"$DOTNET" "$CHARACTER_DLL" --migrate --config config/character.json >"$TMP/migrate.log" 2>&1
if [ $? -eq 0 ] && grep -q '^migrate=ok' "$TMP/migrate.log"; then
  ok "迁移清单跑通（角色 → 社交 → 交易）"
else
  bad "迁移失败"; tail -8 "$TMP/migrate.log" | detail; finish "$TITLE"
fi
STATUS="$("$DOTNET" "$CHARACTER_DLL" --schema-status --config config/character.json 2>&1)"
# 版本号现取清单里的最新值：后面每加一条迁移都会往后走，写死就会在别人卡上红。
ROLE_V="$(latest_version character)"
TRADE_V="$(latest_version trade)"
if printf '%s' "$STATUS" | grep -q "schema_status=ok schema=角色 .*latest_version=${ROLE_V} " \
   && printf '%s' "$STATUS" | grep -q "schema_status=ok schema=交易 .*latest_version=${TRADE_V} "; then
  ok "版本表记到 角色 v${ROLE_V} / 交易 v${TRADE_V}，没有 drift"
else
  bad "版本表不是 角色 v${ROLE_V} / 交易 v${TRADE_V}"; printf '%s' "$STATUS" | grep schema_status | detail; finish "$TITLE"
fi

ROOT="$ROOT" TMP="$TMP" CHAR_PORT="$CHAR_PORT" GATEWAY_PORT="$GATEWAY_PORT" \
GLOBAL_PORT="$GLOBAL_PORT" WORLD_PORT="$WORLD_PORT" python3 - <<'PY'
import json, os
root, tmp = os.environ['ROOT'], os.environ['TMP']
e = json.load(open(root + '/config/enhance.json', encoding='utf-8'))
for tier in e['tiers']:
    tier['success_rate'] = 1.0
json.dump(e, open(tmp + '/enhance.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
c = {
    'schema': 'character-config/1',
    'listen': 'http://127.0.0.1:' + os.environ['CHAR_PORT'],
    'db_pool_max': 6, 'bag_slots': 40,
    'dungeon_table': 'config/dungeons.json', 'exp_table': 'config/exp.json',
    'enhance_table': tmp + '/enhance.json', 'transfer_table': 'config/transfer.json',
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
    'world_upstream': 'http://127.0.0.1:' + os.environ['WORLD_PORT'],
    'global_upstream': 'http://127.0.0.1:' + os.environ['GLOBAL_PORT'],
}
json.dump(a, open(tmp + '/gateway.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)
PY
for acct in "$ACCOUNT" "$OTHER"; do
  printf '%s\n' "$TEST_PASSWORD" | "$DOTNET" "$GATEWAY_DLL" --seed-account "$acct" --seed-slots 8 --config config/gateway.json >>"$TMP/seed.log" 2>&1
done
ok "两个测试账号已播种，强化表临时配成全成功"

echo "== 3. 起角色服、全局服务与网关 =="
"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >"$TMP/character.log" 2>&1 & CHARACTER_PID=$!
"$DOTNET" "$GLOBAL_DLL" --config "$TMP/global.json" >"$TMP/global.log" 2>&1 & GLOBAL_PID=$!
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >"$TMP/gateway.log" 2>&1 & GATEWAY_PID=$!
if wait_ready "$TMP/character.log" '^ready name=Character' "角色服" \
   && wait_ready "$TMP/global.log" '^ready name=Global' "全局服务" \
   && wait_ready "$TMP/gateway.log" '^ready name=Gateway' "网关"; then
  ok "三个进程起来"
else
  tail -12 "$TMP/character.log" | detail; finish "$TITLE"
fi

add_item() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/inventory/add?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$2,\"slot\":$3,\"item_id\":\"$4\",\"count\":$5}"
}

add_gold() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/currency/add?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$2,\"kind\":\"gold\",\"delta\":$3}"
}

enhance_slot() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character/enhance?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$2,\"slot\":$3,\"stone_id\":\"$4\"}"
}

transfer_slot() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character/transfer?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$2,\"from_slot\":$3,\"to_slot\":$4}"
}

list_auction() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/auction/list?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$2,\"slot\":$3,\"count\":$4,\"price_gold\":$5}"
}

buy_auction() {
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/auction/buy?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":$2,\"listing_id\":$3}"
}

# 不看回包，按格从库里读回：背包行 id 与强化等级。
row_of() {
  "$DOTNET" "$CHARACTER_DLL" --enhance-row "$1" "$2" --config "$TMP/character.json" 2>/dev/null \
    | sed -n 's/^enhance_row=ok .* inventory_id=\([0-9]*\) item=[^ ]* level=\([0-9]*\)/\1 \2/p'
}

expect_row() {
  local want="$1" label="$2" got
  got="$(row_of "$3" "$4")"
  if [ "$got" = "$want" ]; then ok "$label"; else bad "${label}：row=[$got] 期望=[$want]"; fi
}

# 只看等级：新装备也有背包行 id，不能要求整行是 0 0。
expect_level() {
  local want="$1" label="$2" got
  got="$(row_of "$3" "$4" | awk '{print $2}')"
  if [ "$got" = "$want" ]; then ok "$label"; else bad "${label}：level=[$got] 期望=[$want]"; fi
}

expect_empty() {
  local got
  got="$(row_of "$1" "$2" | awk '{print $1}')"
  if [ "$got" = "0" ] || [ -z "$got" ]; then ok "$3"; else bad "${3}：这一格还有背包行 id=[$got]"; fi
}

echo "== 4. 造料：两件装备、三档石头、转移石、金币 =="
TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
create_char "$TOKEN" "p4c4_a" && ID="$NEW_ID" || bad "建卖方角色失败"
create_char "$OTHER_TOKEN" "p4c4_b" && OTHER_ID="$NEW_ID" || bad "建买方角色失败"
[ -n "$ID" ] && [ -n "$OTHER_ID" ] || finish "$TITLE"
SEED=1
for spec in "1 ring_trial 1" "2 sword_trial 1" "3 stone_enhance_low 60" \
            "4 stone_enhance_mid 60" "5 stone_enhance_high 60" "6 stone_transfer 5"; do
  set -- $spec
  [ "$(code_of "$(add_item "$TOKEN" "$ID" "$1" "$2" "$3")")" = "200" ] || { SEED=0; bad "发 $2 失败"; }
done
[ "$(code_of "$(add_gold "$TOKEN" "$ID" 100000)")" = "200" ] || { SEED=0; bad "卖方金币没发到"; }
[ "$(code_of "$(add_gold "$OTHER_TOKEN" "$OTHER_ID" 100000)")" = "200" ] || { SEED=0; bad "买方金币没发到"; }
[ "$SEED" = "1" ] && ok "两件装备、三档石头各 60、转移石 5、双方各 100000 金币到位"
[ "$SEED" = "1" ] || finish "$TITLE"
expect_level "0" "新装备是 +0，还没有强化记录" "$ID" 1

echo "== 5. 把 1 号格点到 +12 =="
for lv in 1 2 3 4 5 6; do enhance_slot "$TOKEN" "$ID" 1 stone_enhance_low >/dev/null; done
for lv in 7 8 9; do enhance_slot "$TOKEN" "$ID" 1 stone_enhance_mid >/dev/null; done
for lv in 10 11 12; do enhance_slot "$TOKEN" "$ID" 1 stone_enhance_high >/dev/null; done
R12="$(row_of "$ID" 1)"
if [ "$(printf '%s' "$R12" | awk '{print $2}')" = "12" ]; then
  ok "+12 装备就位，等级挂在背包行 id=$(printf '%s' "$R12" | awk '{print $1}') 上"
else
  bad "没点到 +12：row=[$R12]"; finish "$TITLE"
fi
INV12="$(printf '%s' "$R12" | awk '{print $1}')"

echo "== 6. +12 装备上架、成交后等级跟着到买方 =="
LRESP="$(list_auction "$TOKEN" "$ID" 1 1 1000)"
BODY="$(body_of "$LRESP")"
LISTING="$(json_field "$BODY" listing_id)"
if [ "$(code_of "$LRESP")" = "200" ] && [ "$(json_field "$BODY" ok)" = "true" ]; then
  ok "+12 装备可以上架（listing=$LISTING）"
else
  bad "上架被拒：$(printf '%s' "$BODY" | head -c 160)"; finish "$TITLE"
fi
# 成交后角色服按挂单记的 item_id 复核卖方那一格，再把那一行的等级交给买方新行。
if [ "$(code_of "$(buy_auction "$OTHER_TOKEN" "$OTHER_ID" "$LISTING")")" = "200" ]; then
  ok "买方买下挂单"
else
  bad "买方没买到"; finish "$TITLE"
fi
expect_empty "$ID" 1 "卖方那一格空了，等级没有留在格子上"
expect_level "12" "买方的这一行拿到 +12" "$OTHER_ID" 1
INV_B="$(row_of "$OTHER_ID" 1 | awk '{print $1}')"
if [ -n "$INV_B" ] && [ "$INV_B" != "0" ] && [ "$INV_B" != "$INV12" ]; then
  ok "买方的背包行是新的（id=$INV_B），等级是按行搬过去的"
else
  bad "买方的背包行 id 不对：[$INV_B]（卖方原来是 $INV12）"
fi
add_item "$TOKEN" "$ID" 1 ring_trial 1 >/dev/null
expect_level "0" "同一格放进另一件装备读到 +0，不串上一件的 +12" "$ID" 1

echo "== 7. 换格：等级跟着背包行走，不跟格子 =="
enhance_slot "$TOKEN" "$ID" 2 stone_enhance_low >/dev/null
INV2="$(row_of "$ID" 2 | awk '{print $1}')"
"$DOTNET" "$CHARACTER_DLL" --move-slot "$ID" 2 10 --config "$TMP/character.json" >/dev/null 2>&1
expect_row "$INV2 1" "挪到 10 号格后，还是同一行、还是 +1" "$ID" 10
expect_empty "$ID" 2 "2 号格空了，没有留下等级"

echo "== 8. 卖出（扣光）：删行后等级跟着走，不给下一件 =="
"$DOTNET" "$CHARACTER_DLL" --raw-delete-slot "$ID" 10 --config "$TMP/character.json" >/dev/null 2>&1
add_item "$TOKEN" "$ID" 10 sword_trial 1 >/dev/null
expect_level "0" "10 号格的新装备读到 +0，不继承卖掉那件的 +1" "$ID" 10

echo "== 9. 转移按装备：来源清空、目标覆盖，绑定照旧 =="
# 10 号格刚放进来的新装备是 +0；把 2 号格（上一段换成新装备也是 +0）先点一级做目标。
enhance_slot "$TOKEN" "$ID" 10 stone_enhance_low >/dev/null
TRESP="$(transfer_slot "$TOKEN" "$ID" 10 1)"
if [ "$(code_of "$TRESP")" = "200" ] && [ "$(json_field "$(body_of "$TRESP")" ok)" = "true" ]; then
  ok "转移走的是两行背包（10 → 1）"
else
  bad "转移失败：$(body_of "$TRESP" | head -c 160)"
fi
expect_level "0" "来源那一行变回 +0" "$ID" 10
if [ "$(row_of "$ID" 1 | awk '{print $2}')" = "1" ]; then
  ok "目标那一行拿到 +1"
else
  bad "目标等级不对：$(row_of "$ID" 1)"
fi

finish "$TITLE"
