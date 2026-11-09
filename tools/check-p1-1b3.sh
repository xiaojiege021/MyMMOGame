#!/usr/bin/env bash
# P1-1b3 核对：背包与绑定。
#
# 完成标准（开发架构第 14 节第 22 行）：物品能进格子。绑定物品不能直接交易。
#
# 做这几段：
#   1. 编译网关与角色服
#   2. 静态断言：物品表带绑定规则与堆叠上限；绑定写在服务端不信客户端；
#      库层 bound 与 bound_at 成对；网关不写背包；格子数来自配置
#   3. 建三张表、两个账号，起角色服与网关
#   4. 物品进格子：矿石累加、装备与卷轴到账即绑
#   5. 查库：绑定行 bound 与 bound_at 一起落，不绑的行两者都空
#   6. 读回整包，格子数与物品对得上
#   7. 绑定物品不能直接交易；不绑的矿石可以
#   8. 堆叠上限、同格异物品、格子越界、未知物品、别人的角色、无令牌 都被拒
#
# 用法：bash tools/check-p1-1b3.sh
# 连库段要在有 gamedb 的机器上跑（笔记本）；缺凭据时明确失败。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"

CHAR_PORT=${MMO_P11B3_CHAR_PORT:-62095}
GATEWAY_PORT=${MMO_P11B3_GATEWAY_PORT:-61096}
ACCOUNT="p11b3_user"
OTHER="p11b3_other"
TEST_PASSWORD="p11b3-Pw-77b1"
CHAR_NAME="p11b3_hero"
BAG_SLOTS=40

PASS=0
FAIL=0
TMP="$(mktemp -d)"
CHARACTER_PID=""
GATEWAY_PID=""
HAVE_DB=""

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { sed 's/^/       /'; }

stop_pid() {
  local pid="$1"
  [ -n "$pid" ] || return 0
  if kill -0 "$pid" 2>/dev/null; then
    kill -TERM "$pid" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
}

cleanup() {
  stop_pid "$GATEWAY_PID"
  stop_pid "$CHARACTER_PID"
  if [ -n "$HAVE_DB" ]; then
    "$DOTNET" "$CHARACTER_DLL" --delete-character "$CHAR_NAME" --config "$TMP/character.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$OTHER" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

login_token() {
  curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/login" \
    -H 'Content-Type: application/json' \
    -d "{\"account\":\"$1\",\"password\":\"${TEST_PASSWORD}\"}" \
    | python3 -c 'import json,sys
try:
    print(json.load(sys.stdin).get("token",""))
except Exception:
    print("")'
}

split_response() {
  BODY="${1%$'\n'*}"
  CODE="${1##*$'\n'}"
}

json_field() {
  printf '%s' "$1" | python3 -c 'import json,sys
try:
    v = json.load(sys.stdin).get("'"$2"'", "")
    print(v)
except Exception:
    print("")'
}

add_item() {
  # $1=令牌 $2=角色 $3=格子 $4=物品 $5=数量
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/inventory/add?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2},\"slot\":${3},\"item_id\":\"${4}\",\"count\":${5}}"
}

can_trade() {
  # $1=令牌 $2=角色 $3=格子
  curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/inventory/can-trade?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2},\"slot\":${3}}"
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-1b3 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Gateway/Gateway.csproj" -c Release --nologo -v q >"$TMP/build-gw.log" 2>&1 \
  && "$DOTNET" build "$ROOT/src/Character/Character.csproj" -c Release --nologo -v q >"$TMP/build-char.log" 2>&1; then
  ok "网关与角色服编译通过"
else
  bad "编译失败"
  grep -E "error " "$TMP/build-gw.log" "$TMP/build-char.log" | head -5 | detail
  echo
  echo "P1-1b3 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
SQL="$ROOT/db/character/inventory.sql"
ITEMS="$ROOT/config/items.json"
SVC="$ROOT/src/Character/CharacterGrpcService.cs"

if grep -q '"bind"' "$ITEMS" && grep -q 'on_pickup' "$ITEMS" && grep -q 'always' "$ITEMS"; then
  ok "物品表带绑定规则：不绑 / 拾取即绑 / 到账即绑"
else
  bad "物品表没有绑定规则"
fi

if grep -q '"stack_max"' "$ITEMS"; then
  ok "物品表带堆叠上限"
else
  bad "物品表没有堆叠上限"
fi

if grep -q 'BindsOnArrival' "$ROOT/src/Character/ItemTable.cs" \
   && grep -q 'item.BindsOnArrival' "$ROOT/src/Character/InventoryRepository.cs"; then
  ok "绑定由物品表决定，客户端说了不算"
else
  bad "绑定不是服务端按物品表算的"
fi

if grep -q 'slot.Bound' "$SVC" && grep -q 'bound_item' "$SVC"; then
  ok "绑定物品在交易判定里被挡住（bound_item）"
else
  bad "绑定物品没有交易判定"
fi

if grep -q 'bound_at IS NOT NULL' "$SQL"; then
  ok "库层要求 bound 与 bound_at 成对，不会出现只绑一半"
else
  bad "库层没有绑定一致性约束"
fi

if grep -rq 'insert into "角色"\.inventory' "$ROOT/src/Gateway" 2>/dev/null; then
  bad "网关直接写了背包，角色服的唯一写入被绕过"
else
  ok "网关不写背包，只转请求"
fi

if grep -q 'bag_slots' "$ROOT/src/Character/CharacterConfig.cs" && grep -q 'BagSlots' "$SVC"; then
  ok "格子数来自配置"
else
  bad "格子数写死在代码里"
fi

echo "== 3. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env"
  echo
  echo "P1-1b3 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

cat > "$TMP/character.json" <<JSON
{
  "schema": "character-config/1",
  "listen": "http://127.0.0.1:${CHAR_PORT}",
  "db_pool_max": 4,
  "bag_slots": ${BAG_SLOTS}
}
JSON

cat > "$TMP/gateway.json" <<JSON
{
  "schema": "gateway-config/1",
  "listen": "http://127.0.0.1:${GATEWAY_PORT}",
  "token_ttl_s": 3600,
  "pbkdf2_iterations": 10000,
  "character_upstream": "http://127.0.0.1:${CHAR_PORT}"
}
JSON

if ! "$DOTNET" "$GATEWAY_DLL" --migrate --config "$TMP/gateway.json" \
      --sql "$ROOT/db/character/account.sql" >"$TMP/migrate-account.log" 2>&1; then
  bad "账号表建不出来"
  tail -5 "$TMP/migrate-account.log" | detail
  echo
  echo "P1-1b3 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

"$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" --sql "$ROOT/db/character/character.sql" >/dev/null 2>&1 || true
if "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" --sql "$SQL" >"$TMP/migrate-inventory.log" 2>&1; then
  ok "账号、角色、背包三张表都在位"
else
  bad "背包表建不出来"
  tail -5 "$TMP/migrate-inventory.log" | detail
  echo
  echo "P1-1b3 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

for acct in "$ACCOUNT" "$OTHER"; do
  printf '%s\n' "$TEST_PASSWORD" \
    | "$DOTNET" "$GATEWAY_DLL" --seed-account "$acct" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
done
ok "两个测试账号已写入"

: > "$TMP/character.log"
"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >>"$TMP/character.log" 2>&1 &
CHARACTER_PID=$!

CHAR_READY=0
for _ in $(seq 1 100); do
  if grep -q '^ready name=Character' "$TMP/character.log" 2>/dev/null; then
    CHAR_READY=1
    break
  fi
  if ! kill -0 "$CHARACTER_PID" 2>/dev/null; then
    break
  fi
  sleep 0.1
done

if [ "$CHAR_READY" = "1" ]; then
  ok "角色服在 127.0.0.1:${CHAR_PORT} 上（$(grep -m1 '^items=' "$TMP/character.log")）"
else
  bad "角色服没起来"
  tail -8 "$TMP/character.log" | detail
  echo
  echo "P1-1b3 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

: > "$TMP/gateway.log"
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >>"$TMP/gateway.log" 2>&1 &
GATEWAY_PID=$!

GATEWAY_READY=0
for _ in $(seq 1 150); do
  if grep -q '^ready name=Gateway' "$TMP/gateway.log" 2>/dev/null; then
    if curl -s -m 2 "http://127.0.0.1:${GATEWAY_PORT}/health" | grep -q '"status":"ok"'; then
      GATEWAY_READY=1
      break
    fi
  fi
  if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then
    break
  fi
  sleep 0.2
done

if [ "$GATEWAY_READY" = "1" ]; then
  ok "网关在 127.0.0.1:${GATEWAY_PORT} 上"
else
  bad "网关没起来"
  tail -8 "$TMP/gateway.log" | detail
  echo
  echo "P1-1b3 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then
  ok "两个账号都能登录"
else
  bad "登录没拿到令牌"
  echo
  echo "P1-1b3 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

CREATE="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/character?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"${CHAR_NAME}\",\"race\":\"mai\",\"class_id\":\"archer\"}")"
split_response "$CREATE"
CHAR_ID="$(json_field "$BODY" character_id)"
if [ "$CODE" = "200" ] && [ -n "$CHAR_ID" ]; then
  ok "测试角色建好：id=${CHAR_ID}"
else
  bad "测试角色没建出来（HTTP ${CODE}）"
  echo
  echo "P1-1b3 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 4. 物品进格子 =="
RESP="$(add_item "$TOKEN" "$CHAR_ID" 1 ore_iron 5)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" count)" = "5" ] && [ "$(json_field "$BODY" bound)" = "False" ]; then
  ok "铁矿 5 个进了 1 号格，未绑定"
else
  bad "铁矿没进格（HTTP ${CODE}）：$(printf '%s' "$BODY" | head -c 100)"
fi

RESP="$(add_item "$TOKEN" "$CHAR_ID" 1 ore_iron 3)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" count)" = "8" ]; then
  ok "同格同物品累加：5 + 3 = 8"
else
  bad "累加不对（HTTP ${CODE}）：$(printf '%s' "$BODY" | head -c 100)"
fi

RESP="$(add_item "$TOKEN" "$CHAR_ID" 2 sword_trial 1)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" bound)" = "True" ]; then
  ok "副本装备进 2 号格，拾取即绑"
else
  bad "装备没能进格或没绑定（HTTP ${CODE}）：$(printf '%s' "$BODY" | head -c 100)"
fi

RESP="$(add_item "$TOKEN" "$CHAR_ID" 3 scroll_revive 2)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" bound)" = "True" ]; then
  ok "复活卷轴进 3 号格，到账即绑"
else
  bad "卷轴没能进格或没绑定（HTTP ${CODE}）"
fi

echo "== 5. 查库：绑定落对了 =="
BIND_1="$("$DOTNET" "$CHARACTER_DLL" --binding-of "$CHAR_NAME" 1 --config "$TMP/character.json" 2>&1 | grep -m1 '^binding=')"
BIND_2="$("$DOTNET" "$CHARACTER_DLL" --binding-of "$CHAR_NAME" 2 --config "$TMP/character.json" 2>&1 | grep -m1 '^binding=')"
printf '  %s\n  %s\n' "$BIND_1" "$BIND_2"

if printf '%s' "$BIND_1" | grep -q 'bound=False bound_at_present=False'; then
  ok "矿石那一行不绑，也没有绑定时刻"
else
  bad "矿石那一行绑定状态不对：${BIND_1}"
fi

if printf '%s' "$BIND_2" | grep -q 'bound=True bound_at_present=True'; then
  ok "装备那一行绑定了，绑定时刻一起落库"
else
  bad "装备那一行绑定状态不对：${BIND_2}"
fi

echo "== 6. 读回整包 =="
LIST="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=${TOKEN}&character_id=${CHAR_ID}")"
COUNT_SLOTS="$(printf '%s' "$LIST" | python3 -c 'import json,sys
try:
    print(len(json.load(sys.stdin).get("slots", [])))
except Exception:
    print("-1")')"
BAG="$(printf '%s' "$LIST" | python3 -c 'import json,sys
try:
    print(json.load(sys.stdin).get("bag_slots", ""))
except Exception:
    print("")')"

if [ "$COUNT_SLOTS" = "3" ]; then
  ok "背包读回 3 格"
else
  bad "背包读回的格子数不对：${COUNT_SLOTS}"
fi

if [ "$BAG" = "$BAG_SLOTS" ]; then
  ok "格子总数来自配置：${BAG}"
else
  bad "格子总数不对：${BAG:-空}"
fi

echo "== 7. 绑定物品不能直接交易 =="
RESP="$(can_trade "$TOKEN" "$CHAR_ID" 2)"
split_response "$RESP"
if [ "$(json_field "$BODY" tradable)" = "False" ] && [ "$(json_field "$BODY" reason)" = "bound_item" ]; then
  ok "绑定的装备：不能直接交易（bound_item）"
else
  bad "绑定的装备竟然能交易：$(printf '%s' "$BODY" | head -c 80)"
fi

RESP="$(can_trade "$TOKEN" "$CHAR_ID" 3)"
split_response "$RESP"
if [ "$(json_field "$BODY" tradable)" = "False" ] && [ "$(json_field "$BODY" reason)" = "bound_item" ]; then
  ok "绑定的卷轴：不能直接交易（bound_item）"
else
  bad "绑定的卷轴竟然能交易"
fi

RESP="$(can_trade "$TOKEN" "$CHAR_ID" 1)"
split_response "$RESP"
if [ "$(json_field "$BODY" tradable)" = "True" ]; then
  ok "不绑的矿石：可以交易"
else
  bad "不绑的矿石也不能交易：$(printf '%s' "$BODY" | head -c 80)"
fi

RESP="$(can_trade "$TOKEN" "$CHAR_ID" 9)"
split_response "$RESP"
if [ "$(json_field "$BODY" tradable)" = "False" ] && [ "$(json_field "$BODY" reason)" = "slot_empty" ]; then
  ok "空格子：不可交易（slot_empty）"
else
  bad "空格子的判定不对：$(printf '%s' "$BODY" | head -c 80)"
fi

echo "== 8. 该拒的都拒了 =="
reject_add() {
  # $1=说明 $2=期望错误码 $3=角色 $4=格子 $5=物品 $6=数量
  local label="$1" want="$2" char="$3" slot="$4" item="$5" count="$6" resp
  resp="$(add_item "$TOKEN" "$char" "$slot" "$item" "$count")"
  split_response "$resp"
  local err
  err="$(json_field "$BODY" error)"
  if [ "$CODE" = "400" ] && [ "$err" = "$want" ]; then
    ok "${label}：HTTP 400 ${err}"
  else
    bad "${label}：期望 400 ${want}，实际 ${CODE} ${err}"
  fi
}

reject_add "装备堆 2 个" stack_overflow "$CHAR_ID" 5 sword_trial 2
reject_add "矿石一次 1000 个" stack_overflow "$CHAR_ID" 6 ore_iron 1000
reject_add "同格放别的物品" slot_occupied "$CHAR_ID" 1 herb_ling 1
reject_add "格子 0" slot_out_of_range "$CHAR_ID" 0 ore_iron 1
reject_add "格子超过 bag_slots" slot_out_of_range "$CHAR_ID" $(( BAG_SLOTS + 1 )) ore_iron 1
reject_add "未知物品" unknown_item "$CHAR_ID" 7 dragon_scale 1

# 别人的角色动不了。
RESP="$(add_item "$OTHER_TOKEN" "$CHAR_ID" 8 ore_iron 1)"
split_response "$RESP"
if [ "$CODE" = "404" ] && [ "$(json_field "$BODY" error)" = "character_not_found" ]; then
  ok "别人的角色：404 character_not_found"
else
  bad "别人的角色也能塞东西：HTTP ${CODE}"
fi

# 不带令牌。
RESP="$(add_item "" "$CHAR_ID" 8 ore_iron 1)"
split_response "$RESP"
if [ "$CODE" = "401" ]; then
  ok "不带令牌：HTTP 401"
else
  bad "不带令牌也能塞东西：HTTP ${CODE}"
fi

# 被拒的几次不能在库里留下东西：还是 3 格。
LIST="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/inventory?token=${TOKEN}&character_id=${CHAR_ID}")"
AFTER_SLOTS="$(printf '%s' "$LIST" | python3 -c 'import json,sys
try:
    print(len(json.load(sys.stdin).get("slots", [])))
except Exception:
    print("-1")')"
if [ "$AFTER_SLOTS" = "3" ]; then
  ok "被拒的这几次没有多出格子：仍是 3 格"
else
  bad "被拒的请求也写了背包：${AFTER_SLOTS} 格"
fi

if grep -q "$TEST_PASSWORD" "$TMP/gateway.log" 2>/dev/null; then
  bad "网关日志里出现了口令"
else
  ok "网关日志里没有口令"
fi

echo
echo "P1-1b3 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
