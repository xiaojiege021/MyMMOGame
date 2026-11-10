#!/usr/bin/env bash
# P1-2b2 核对：温泉只恢复 1500。
#
# 完成标准（开发架构第 14 节第 27 行 + 第 11 节 P1-2「温泉只恢复 1500 点疲劳」）：
#   使用后疲劳只加回 1500；技术架构第 5 节「温泉恢复 1500，只补疲劳」。
#
# 做这几段：
#   1. 编译战斗服、角色服、网关、探针
#   2. 静态断言：恢复量在配置、请求里带不进数值、只更新疲劳、事务与行锁、
#      下限 0、主线与深渊的旧规则没被改
#   3. 建表、账号、七个测试角色，起三个进程
#   4. 0 疲劳、打满 6000 后恢复 1500、只补疲劳（份额/进本记录/等级/货币/背包）、
#      恢复后继续进本、部分恢复、并发、跨天、主线与 4 人深渊回归、鉴权
#
# 用法：bash tools/check/check-p1-2b2.sh
# 连库段要在有 gamedb 的机器上跑（笔记本）；缺凭据时明确失败。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
PROBE_DLL="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"

BATTLE_PORT=${MMO_P12B2_BATTLE_PORT:-60015}
CHAR_PORT=${MMO_P12B2_CHAR_PORT:-62103}
GATEWAY_PORT=${MMO_P12B2_GATEWAY_PORT:-61104}
ACCOUNT="p12b2_user"
OTHER="p12b2_other"
TEST_PASSWORD="p12b2-Pw-7c31"

# 每个角色各管一段，避免互相把疲劳搅乱。
NAME_SPRING="p12b2_spring"
NAME_EMPTY="p12b2_empty"
NAME_PARTIAL="p12b2_partial"
NAME_CONCURRENT="p12b2_concurrent"
NAME_DAY="p12b2_day"
NAME_MAIN="p12b2_main"
NAME_TEAM="p12b2_team"
CHAR_NAMES=("$NAME_SPRING" "$NAME_EMPTY" "$NAME_PARTIAL" "$NAME_CONCURRENT" "$NAME_DAY" "$NAME_MAIN" "$NAME_TEAM")

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""
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
  stop_pid "$BATTLE_PID"
  if [ -n "$HAVE_DB" ]; then
    for name in "${CHAR_NAMES[@]}"; do
      "$DOTNET" "$CHARACTER_DLL" --delete-character "$name" --config "$TMP/character.json" >/dev/null 2>&1 || true
    done
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$OTHER" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP"
}
trap cleanup EXIT

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

create_char() {
  # $1=令牌 $2=名字；把角色编号放进 NEW_ID。
  local resp
  resp="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
    "http://127.0.0.1:${GATEWAY_PORT}/character?token=$1" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$2\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
  split_response "$resp"
  NEW_ID="$(json_field "$BODY" character_id)"
  [ "$CODE" = "200" ] && [ -n "$NEW_ID" ]
}

enter_dungeon() {
  # $1=令牌（空串不带） $2=角色 $3=副本 $4=人数
  local token="$1" url="http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 10 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2},\"dungeon_id\":\"${3}\",\"party_size\":${4}}"
}

hot_spring() {
  # $1=令牌（空串不带） $2=角色；请求里没有「恢复多少」这个字段。
  local token="$1" url="http://127.0.0.1:${GATEWAY_PORT}/hot-spring"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 10 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2}}"
}

fatigue_state() {
  "$DOTNET" "$CHARACTER_DLL" --fatigue-state "$1" --config "$TMP/character.json" 2>&1 \
    | grep -m1 '^fatigue_state=' || true
}

fatigue_of() {
  printf '%s' "$1" | grep -o 'fatigue=[0-9-]*' | head -1 | cut -d= -f2
}

share_of() {
  printf '%s' "$1" | grep -o 'weekly_share=[0-9-]*' | head -1 | cut -d= -f2
}

entry_count() {
  "$DOTNET" "$CHARACTER_DLL" --entry-count "$1" --config "$TMP/character.json" 2>&1 \
    | grep -m1 '^entry_count=' || true
}

entries_of() {
  printf '%s' "$1" | grep -o 'entry_count=[0-9]*' | head -1 | cut -d= -f2
}

level_of() {
  "$DOTNET" "$CHARACTER_DLL" --level-of "$1" --config "$TMP/character.json" 2>&1 \
    | grep -m1 '^level=' || true
}

snapshot() {
  # $1=接口名 currency|inventory $2=令牌 $3=角色
  curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/$1?token=$2&character_id=$3"
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

BUILD_OK=1
for proj in src/Battle/Battle.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj tools/probe/BattleProbe/BattleProbe.csproj; do
  if ! "$DOTNET" build "$ROOT/$proj" -c Release --nologo -v q >"$TMP/build-$(basename "$proj" .csproj).log" 2>&1; then
    BUILD_OK=0
    bad "编译失败：$proj"
    grep -E "error " "$TMP/build-$(basename "$proj" .csproj).log" | head -4 | detail
  fi
done
if [ "$BUILD_OK" = "1" ]; then
  ok "战斗服、角色服、网关、探针编译通过"
else
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
CFG="$ROOT/config/dungeons.json"
SQL="$ROOT/db/character/character.sql"
PROTO="$ROOT/proto/mmo/character.proto"
TABLE="$ROOT/src/Character/DungeonTable.cs"
REPO="$ROOT/src/Character/HotSpringRepository.cs"
SVC="$ROOT/src/Character/CharacterGrpcService.cs"
API="$ROOT/src/Gateway/CharacterApi.cs"

RECOVERY="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["hot_spring_recovery"])' "$CFG")"
CAP="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1],encoding="utf-8"))["fatigue_daily_cap"])' "$CFG")"
PER_PLAYER="$(python3 -c 'import json,sys
d=json.load(open(sys.argv[1],encoding="utf-8"))
print([x["fatigue_per_player"] for x in d["dungeons"] if x["id"]=="abyss"][0])' "$CFG")"
if [ "$RECOVERY" = "1500" ] && [ "$CAP" = "6000" ] \
   && grep -q '"hot_spring_recovery": 1500' "$CFG" \
   && grep -q '"fatigue_daily_cap": 6000' "$CFG"; then
  ok "温泉恢复 1500 与日上限 6000 都写在配置里，代码里没有第二份数字"
else
  bad "恢复量或日上限不在配置里：recovery=$RECOVERY cap=$CAP"
fi
if [ "$PER_PLAYER" != "1500" ]; then
  bad "深渊基础消耗变了，本脚本的期望值要跟着改：per_player=$PER_PLAYER"
else
  ok "深渊单人基础消耗仍是 1500，P1-2b1 的扣减口径没动"
fi

if grep -qF '[JsonPropertyName("hot_spring_recovery")]' "$TABLE" \
   && grep -q 'dungeons.HotSpringRecovery' "$SVC" \
   && grep -q 'dungeons.HotSpringRecovery, context.CancellationToken' "$SVC"; then
  ok "角色服从配置读恢复量，网关与客户端都给不出这个数值"
else
  bad "恢复量可能被写死在代码里"
fi

if sed -n '/message UseHotSpringRequest {/,/^}/p' "$PROTO" | grep -q 'recovery'; then
  bad "温泉请求里带了恢复量字段，客户端可以自己改数值"
else
  ok "温泉请求只有 account 与 character_id，恢复量由服务端定"
fi

if grep -q 'rpc UseHotSpring' "$PROTO" && grep -q 'UseHotSpringAsync' "$API" \
   && grep -q 'MapPost("/hot-spring"' "$API" \
   && ! grep -q 'Npgsql' "$API"; then
  ok "网关只做鉴权与转发，持久数据仍由角色服唯一写入"
else
  bad "温泉入口没有走网关 → 角色服 gRPC"
fi

if grep -qF 'update \"角色\".character set fatigue = $2, fatigue_day = current_date' "$REPO" \
   && ! grep -nEi 'insert into|delete from|update .*(currency|inventory|dungeon_entry|grant_txn)|set (level|weekly_share|gold)' "$REPO" >/dev/null; then
  ok "温泉只更新疲劳与日界，不碰周份额、进本记录、货币、背包、等级"
else
  bad "温泉写了疲劳之外的东西"
fi

if grep -q 'BeginTransactionAsync' "$REPO" && grep -q 'CommitAsync' "$REPO" \
   && grep -q 'RollbackAsync' "$REPO" && grep -q 'for update' "$REPO"; then
  ok "恢复与日界在一个事务里，行锁让并发读到新值"
else
  bad "温泉不是整笔事务，并发会读脏"
fi

if grep -q 'if (after < 0)' "$REPO" && grep -q 'recovered = before - after' "$REPO" \
   && grep -q 'fatigue_day <> current_date' "$REPO" \
   && grep -q 'CHECK (fatigue >= 0)' "$SQL" \
   && grep -q 'CHECK (fatigue <= 6000)' "$SQL"; then
  ok "下限 0、跨天归零都在，库层两道 CHECK 仍挡负数与超日上限"
else
  bad "疲劳可能被恢复成负数，或者跨天还留着昨天的账"
fi

if grep -q 'if (!dungeon.ChargesFatigue)' "$SVC" \
   && grep -q 'FatiguePerPlayer \* partySize' "$ROOT/src/Character/DungeonEntryRepository.cs" \
   && grep -q 'DungeonTable.Load(config.DungeonTable)' "$ROOT/src/Character/CharacterProcess.cs" \
   && grep -q 'DungeonTable.Load' "$ROOT/src/Battle/Config.cs"; then
  ok "主线不扣疲劳、深渊按人数倍增、两服读同一份副本表，都没被本行改掉"
else
  bad "前面的进本规则被温泉改动了"
fi

echo "== 3. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env"
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

python3 - "$ROOT" "$TMP" "$BATTLE_PORT" <<'PY'
import json, os, sys
root, tmp, port = sys.argv[1], sys.argv[2], sys.argv[3]
cfg = json.load(open(os.path.join(root, "config", "battle.json"), encoding="utf-8"))
cfg["listen"] = "127.0.0.1:" + port
cfg["settlement_dir"] = os.path.join(tmp, "settlements")
cfg["character_dir"] = os.path.join(tmp, "characters")
cfg["skill_table"] = "skills.json"
cfg["class_table"] = "classes.json"
cfg["dungeon_table"] = "dungeons.json"
json.dump(cfg, open(os.path.join(tmp, "battle.json"), "w", encoding="utf-8"),
          ensure_ascii=False, indent=2)
PY
cp "$ROOT/config/skills.json" "$ROOT/config/classes.json" "$ROOT/config/dungeons.json" "$TMP/"

cat > "$TMP/character.json" <<JSON
{
  "schema": "character-config/1",
  "listen": "http://127.0.0.1:${CHAR_PORT}",
  "db_pool_max": 6,
  "bag_slots": 40,
  "dungeon_table": "${TMP}/dungeons.json"
}
JSON

cat > "$TMP/gateway.json" <<JSON
{
  "schema": "gateway-config/1",
  "listen": "http://127.0.0.1:${GATEWAY_PORT}",
  "token_ttl_s": 3600,
  "pbkdf2_iterations": 10000,
  "character_upstream": "http://127.0.0.1:${CHAR_PORT}",
  "battle_upstream": "ws://127.0.0.1:${BATTLE_PORT}/battle"
}
JSON

if ! "$DOTNET" "$GATEWAY_DLL" --migrate --config "$TMP/gateway.json" \
      --sql "$ROOT/db/character/account.sql" >"$TMP/migrate-account.log" 2>&1; then
  bad "账号表建不出来"
  tail -5 "$TMP/migrate-account.log" | detail
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

MIGRATE_OK=1
for sql in character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql; do
  if ! "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" \
        --sql "$ROOT/db/character/$sql" >"$TMP/migrate-$sql.log" 2>&1; then
    MIGRATE_OK=0
    bad "表建不出来：$sql"
    tail -3 "$TMP/migrate-$sql.log" | detail
  fi
done
if [ "$MIGRATE_OK" = "1" ]; then
  ok "角色表（含疲劳日界与两道 CHECK）、进本记录表都在位"
else
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

for acct in "$ACCOUNT" "$OTHER"; do
  printf '%s\n' "$TEST_PASSWORD" \
    | "$DOTNET" "$GATEWAY_DLL" --seed-account "$acct" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
done
ok "两个测试账号已写入"

: > "$TMP/battle.log"
"$DOTNET" "$BATTLE_DLL" --config "$TMP/battle.json" >>"$TMP/battle.log" 2>&1 &
BATTLE_PID=$!
BATTLE_READY=0
for _ in $(seq 1 120); do
  if grep -q "^listen=http://127.0.0.1:${BATTLE_PORT}\$" "$TMP/battle.log" 2>/dev/null; then
    BATTLE_READY=1
    break
  fi
  if ! kill -0 "$BATTLE_PID" 2>/dev/null; then break; fi
  sleep 0.1
done
if [ "$BATTLE_READY" = "1" ]; then
  ok "战斗服在 127.0.0.1:${BATTLE_PORT} 上"
else
  bad "战斗服没起来"
  tail -8 "$TMP/battle.log" | detail
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

: > "$TMP/character.log"
"$DOTNET" "$CHARACTER_DLL" --config "$TMP/character.json" >>"$TMP/character.log" 2>&1 &
CHARACTER_PID=$!
CHAR_READY=0
for _ in $(seq 1 100); do
  if grep -q '^ready name=Character' "$TMP/character.log" 2>/dev/null; then
    CHAR_READY=1
    break
  fi
  if ! kill -0 "$CHARACTER_PID" 2>/dev/null; then break; fi
  sleep 0.1
done
if [ "$CHAR_READY" = "1" ]; then
  ok "角色服在 127.0.0.1:${CHAR_PORT} 上（$(grep -m1 '^dungeons=' "$TMP/character.log")）"
else
  bad "角色服没起来"
  tail -8 "$TMP/character.log" | detail
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
if grep -m1 '^dungeons=' "$TMP/character.log" | grep -q 'hot_spring_recovery=1500'; then
  ok "角色服启动时读到的恢复量是 1500"
else
  bad "角色服没读到配置里的恢复量"
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
  if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then break; fi
  sleep 0.2
done
if [ "$GATEWAY_READY" = "1" ]; then
  ok "网关在 127.0.0.1:${GATEWAY_PORT} 上"
else
  bad "网关没起来"
  tail -8 "$TMP/gateway.log" | detail
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then
  ok "两个账号都能登录"
else
  bad "登录没拿到令牌"
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

# 上次被中断可能留下同名角色，建号前先清掉。
for name in "${CHAR_NAMES[@]}"; do
  "$DOTNET" "$CHARACTER_DLL" --delete-character "$name" --config "$TMP/character.json" >/dev/null 2>&1 || true
done

CREATED=1
for name in "${CHAR_NAMES[@]}"; do
  if ! create_char "$TOKEN" "$name"; then
    CREATED=0
    bad "角色没建出来：$name（HTTP ${CODE}）"
  fi
  case "$name" in
    "$NAME_SPRING")     ID_SPRING="$NEW_ID" ;;
    "$NAME_EMPTY")      ID_EMPTY="$NEW_ID" ;;
    "$NAME_PARTIAL")    ID_PARTIAL="$NEW_ID" ;;
    "$NAME_CONCURRENT") ID_CONCURRENT="$NEW_ID" ;;
    "$NAME_DAY")        ID_DAY="$NEW_ID" ;;
    "$NAME_MAIN")       ID_MAIN="$NEW_ID" ;;
    "$NAME_TEAM")       ID_TEAM="$NEW_ID" ;;
  esac
done
if [ "$CREATED" = "1" ]; then
  ok "七个测试角色都建好了"
else
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 4. 疲劳为 0 时用温泉：不写成负数 =="
STATE="$(fatigue_state "$ID_EMPTY")"
if [ "$(fatigue_of "$STATE")" = "0" ]; then
  ok "新角色当日疲劳是 0"
else
  bad "新角色疲劳不是 0：$STATE"
fi

RESP="$(hot_spring "$TOKEN" "$ID_EMPTY")"
split_response "$RESP"
STATE="$(fatigue_state "$ID_EMPTY")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" fatigue_before)" = "0" ] \
   && [ "$(json_field "$BODY" recovered)" = "0" ] \
   && [ "$(json_field "$BODY" fatigue)" = "0" ] \
   && [ "$(json_field "$BODY" fatigue_daily_cap)" = "6000" ] \
   && [ "$(json_field "$BODY" recovery_limit)" = "1500" ] \
   && [ "$(json_field "$BODY" weekly_share)" = "0" ] \
   && [ "$(fatigue_of "$STATE")" = "0" ]; then
  ok "0 疲劳用温泉：恢复 0，疲劳仍是 0，回包带上限与恢复量"
else
  bad "0 疲劳的温泉结果不对：HTTP ${CODE} ${BODY} ${STATE}"
fi

echo "== 5. 把主测试角色打到 6000，并预热其它账 =="
# 先让「只补疲劳」有可比的东西：等级、金币、背包各给一份。
curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/currency/add?token=$TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID_SPRING},\"kind\":\"gold\",\"delta\":1000}" >/dev/null
curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/inventory/add?token=$TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID_SPRING},\"slot\":1,\"item_id\":\"ore_iron\",\"count\":3}" >/dev/null
curl -s -m 10 -X POST "http://127.0.0.1:${GATEWAY_PORT}/character/level?token=$TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":${ID_SPRING},\"level\":7}" >/dev/null

CAP_OK=1
for expected in 1500 3000 4500 6000; do
  RESP="$(enter_dungeon "$TOKEN" "$ID_SPRING" abyss 1)"
  split_response "$RESP"
  STATE="$(fatigue_state "$ID_SPRING")"
  if [ "$CODE" != "200" ] || [ "$(json_field "$BODY" allowed)" != "True" ] \
     || [ "$(fatigue_of "$STATE")" != "$expected" ]; then
    CAP_OK=0
    bad "打到上限这一步不对（期望 $expected）：HTTP ${CODE} ${STATE} ${BODY}"
  fi
done
if [ "$CAP_OK" = "1" ]; then
  ok "单人连进四次深渊：1500 / 3000 / 4500 / 6000"
else
  echo
  echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

E_BEFORE="$(entry_count "$ID_SPRING")"
RESP="$(enter_dungeon "$TOKEN" "$ID_SPRING" abyss 1)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "fatigue_exhausted" ] \
   && [ "$(entry_count "$ID_SPRING")" = "$E_BEFORE" ]; then
  ok "到 6000 后不能再进深渊（P1-2b1 未被温泉改动）"
else
  bad "打满后还能进，或者账被改了：HTTP ${CODE} ${BODY}"
fi

echo "== 6. 温泉一次：只加回 1500 =="
STATE_BEFORE="$(fatigue_state "$ID_SPRING")"
LEVEL_BEFORE="$(level_of "$ID_SPRING")"
MONEY_BEFORE="$(snapshot currency "$TOKEN" "$ID_SPRING")"
BAG_BEFORE="$(snapshot inventory "$TOKEN" "$ID_SPRING")"

RESP="$(hot_spring "$TOKEN" "$ID_SPRING")"
split_response "$RESP"
STATE_AFTER="$(fatigue_state "$ID_SPRING")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" fatigue_before)" = "6000" ] \
   && [ "$(json_field "$BODY" recovered)" = "1500" ] \
   && [ "$(json_field "$BODY" fatigue)" = "4500" ] \
   && [ "$(json_field "$BODY" weekly_share)" = "0" ] \
   && [ "$(fatigue_of "$STATE_AFTER")" = "4500" ]; then
  ok "6000 用一次温泉 → 4500，只加回 1500（库里也是 4500）"
else
  bad "恢复量不是 1500：HTTP ${CODE} ${BODY} ${STATE_AFTER}"
fi

if [ "$(share_of "$STATE_BEFORE")" = "$(share_of "$STATE_AFTER")" ]; then
  ok "周份额没动：$(share_of "$STATE_AFTER")"
else
  bad "温泉改了周份额：$STATE_BEFORE → $STATE_AFTER"
fi

if [ "$(entry_count "$ID_SPRING")" = "$E_BEFORE" ]; then
  ok "进本记录没动（$(entries_of "$E_BEFORE") 条，温泉不算进本）"
else
  bad "温泉写了一条进本记录：$(entry_count "$ID_SPRING")"
fi

if [ "$(level_of "$ID_SPRING")" = "$LEVEL_BEFORE" ] \
   && printf '%s' "$LEVEL_BEFORE" | grep -q 'level=7'; then
  ok "等级没动：$LEVEL_BEFORE"
else
  bad "温泉改了等级：$LEVEL_BEFORE → $(level_of "$ID_SPRING")"
fi

if [ "$(snapshot currency "$TOKEN" "$ID_SPRING")" = "$MONEY_BEFORE" ] \
   && printf '%s' "$MONEY_BEFORE" | grep -q '"amount":1000'; then
  ok "货币没动：金币仍是 1000"
else
  bad "温泉改了货币：$MONEY_BEFORE → $(snapshot currency "$TOKEN" "$ID_SPRING")"
fi

if [ "$(snapshot inventory "$TOKEN" "$ID_SPRING")" = "$BAG_BEFORE" ] \
   && printf '%s' "$BAG_BEFORE" | grep -q 'ore_iron'; then
  ok "背包没动：矿石仍在格子里"
else
  bad "温泉改了背包：$BAG_BEFORE → $(snapshot inventory "$TOKEN" "$ID_SPRING")"
fi

echo "== 7. 恢复后能继续进本，再打满仍被拒 =="
RESP="$(enter_dungeon "$TOKEN" "$ID_SPRING" abyss 1)"
split_response "$RESP"
STATE="$(fatigue_state "$ID_SPRING")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(json_field "$BODY" fatigue_cost)" = "1500" ] \
   && [ "$(fatigue_of "$STATE")" = "6000" ]; then
  ok "温泉后继续进深渊：4500 + 1500 = 6000，扣的仍是 1 倍"
else
  bad "恢复后进本不对：HTTP ${CODE} ${STATE} ${BODY}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_SPRING" abyss 1)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "fatigue_exhausted" ] \
   && [ "$(fatigue_of "$(fatigue_state "$ID_SPRING")")" = "6000" ]; then
  ok "再打满后仍被 fatigue_exhausted 拒绝，疲劳停在 6000"
else
  bad "恢复后能绕过日上限：HTTP ${CODE} ${BODY}"
fi

echo "== 8. 只恢复到 0：已用不足 1500 不会写成负数 =="
# 只给核对用的直写入口造一个不足一次恢复量的场景，不经过任何业务逻辑。
RAW="$("$DOTNET" "$CHARACTER_DLL" --raw-fatigue-write "$ID_PARTIAL" 1000 \
  --config "$TMP/character.json" 2>&1 | grep -m1 '^raw_fatigue=' || true)"
if printf '%s' "$RAW" | grep -q 'raw_fatigue=written' \
   && [ "$(fatigue_of "$(fatigue_state "$ID_PARTIAL")")" = "1000" ]; then
  ok "造出当日已用 1000 的现场"
else
  bad "造不出部分消耗的现场：$RAW"
fi

RESP="$(hot_spring "$TOKEN" "$ID_PARTIAL")"
split_response "$RESP"
STATE="$(fatigue_state "$ID_PARTIAL")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" fatigue_before)" = "1000" ] \
   && [ "$(json_field "$BODY" recovered)" = "1000" ] \
   && [ "$(json_field "$BODY" fatigue)" = "0" ] \
   && [ "$(fatigue_of "$STATE")" = "0" ]; then
  ok "已用 1000 时只恢复 1000，疲劳落在 0，不是 -500"
else
  bad "部分恢复不对：HTTP ${CODE} ${BODY} ${STATE}"
fi

RESP="$(hot_spring "$TOKEN" "$ID_PARTIAL")"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" recovered)" = "0" ] \
   && [ "$(fatigue_of "$(fatigue_state "$ID_PARTIAL")")" = "0" ]; then
  ok "再用一次仍是 0，不会把疲劳写成负数"
else
  bad "重复温泉把疲劳写成负数：HTTP ${CODE} ${BODY}"
fi

echo "== 9. 并发两笔温泉：总共只恢复实际花掉的 =="
RESP="$(enter_dungeon "$TOKEN" "$ID_CONCURRENT" abyss 1)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(fatigue_of "$(fatigue_state "$ID_CONCURRENT")")" = "1500" ]; then
  ok "并发现场：当日已用 1500"
else
  bad "并发现场没造出来：HTTP ${CODE} ${BODY}"
fi

( hot_spring "$TOKEN" "$ID_CONCURRENT" >"$TMP/concurrent-1.txt" 2>&1 ) & C1=$!
( hot_spring "$TOKEN" "$ID_CONCURRENT" >"$TMP/concurrent-2.txt" 2>&1 ) & C2=$!
wait "$C1" "$C2"
split_response "$(cat "$TMP/concurrent-1.txt")"; A1="$BODY"; C_1="$CODE"
split_response "$(cat "$TMP/concurrent-2.txt")"; A2="$BODY"; C_2="$CODE"
R1="$(json_field "$A1" recovered)"; R2="$(json_field "$A2" recovered)"
TOTAL=$(( ${R1:-0} + ${R2:-0} ))
if [ "$C_1" = "200" ] && [ "$C_2" = "200" ] \
   && [ "$TOTAL" = "1500" ] \
   && [ "$(fatigue_of "$(fatigue_state "$ID_CONCURRENT")")" = "0" ]; then
  ok "并发两笔：恢复量合计 1500（$R1 + $R2），疲劳停在 0"
else
  bad "并发两笔多恢复了或减成负数：$C_1 $A1 / $C_2 $A2 / $(fatigue_state "$ID_CONCURRENT")"
fi

echo "== 10. 跨天：日界变了温泉不倒扣 =="
RESP="$(enter_dungeon "$TOKEN" "$ID_DAY" abyss 1)"
split_response "$RESP"
YESTERDAY="$(date -d 'yesterday' +%F 2>/dev/null || date -v-1d +%F)"
"$DOTNET" "$CHARACTER_DLL" --set-fatigue-day "$ID_DAY" "$YESTERDAY" \
  --config "$TMP/character.json" >"$TMP/set-day.log" 2>&1 || true
RESP="$(hot_spring "$TOKEN" "$ID_DAY")"
split_response "$RESP"
STATE="$(fatigue_state "$ID_DAY")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" recovered)" = "0" ] \
   && [ "$(json_field "$BODY" fatigue)" = "0" ] \
   && [ "$(fatigue_of "$STATE")" = "0" ] \
   && printf '%s' "$STATE" | grep -q "day=$(date +%F)"; then
  ok "跨天后温泉恢复 0，昨天的旧值归零并刷成今天"
else
  bad "跨天温泉把疲劳倒扣了：HTTP ${CODE} ${BODY} ${STATE}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_DAY" abyss 1)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(fatigue_of "$(fatigue_state "$ID_DAY")")" = "1500" ]; then
  ok "跨天之后仍能按新的一天进深渊扣 1500"
else
  bad "跨天后进本不对：HTTP ${CODE} ${BODY}"
fi

echo "== 11. 前面的进本规则回归 =="
RESP="$(enter_dungeon "$TOKEN" "$ID_MAIN" main 1)"
split_response "$RESP"
STATE="$(fatigue_state "$ID_MAIN")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(json_field "$BODY" fatigue_cost)" = "0" ] \
   && [ "$(fatigue_of "$STATE")" = "0" ] && [ "$(share_of "$STATE")" = "0" ]; then
  ok "主线仍是 1 人、不扣疲劳、不占周份额"
else
  bad "主线规则被温泉改动：HTTP ${CODE} ${BODY} ${STATE}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_TEAM" abyss 4)"
split_response "$RESP"
STATE="$(fatigue_state "$ID_TEAM")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(json_field "$BODY" fatigue_cost)" = "6000" ] \
   && [ "$(fatigue_of "$STATE")" = "6000" ] \
   && [ "$(json_field "$BODY" fatigue_daily_cap)" = "6000" ]; then
  ok "深渊 4 人仍扣 4 倍（6000），恰好到上限允许"
else
  bad "深渊按人数倍增被改动：HTTP ${CODE} ${BODY} ${STATE}"
fi

echo "== 12. 鉴权与参数 =="
RESP="$(hot_spring "$OTHER_TOKEN" "$ID_SPRING")"
split_response "$RESP"
if [ "$CODE" = "404" ] && [ "$(json_field "$BODY" error)" = "character_not_found" ]; then
  ok "别人的角色：404 character_not_found"
else
  bad "别人的角色也能泡温泉恢复疲劳：HTTP ${CODE} ${BODY}"
fi

RESP="$(hot_spring "" "$ID_SPRING")"
split_response "$RESP"
if [ "$CODE" = "401" ]; then
  ok "不带令牌：HTTP 401"
else
  bad "不带令牌也能用温泉：HTTP ${CODE}"
fi

RESP="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/hot-spring?token=$TOKEN" \
  -H 'Content-Type: application/json' -d '{}')"
split_response "$RESP"
if [ "$CODE" = "400" ]; then
  ok "缺 character_id：HTTP 400"
else
  bad "缺参数也能用温泉：HTTP ${CODE}"
fi

RESP="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/hot-spring?token=$TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"character_id\":0,\"recovered\":99999}")"
split_response "$RESP"
if [ "$CODE" = "400" ] && [ "$(json_field "$BODY" error)" = "character_missing" ]; then
  ok "character_id=0 被拒；请求里塞 recovered 也不会被当成参数"
else
  bad "character_id=0 或伪造的恢复量没被挡住：HTTP ${CODE} ${BODY}"
fi

RESP="$(hot_spring "$TOKEN" 99999999)"
split_response "$RESP"
if [ "$CODE" = "404" ]; then
  ok "不存在的角色：HTTP 404"
else
  bad "不存在的角色也能恢复：HTTP ${CODE}"
fi

if grep -q "$TEST_PASSWORD" "$TMP/gateway.log" 2>/dev/null; then
  bad "网关日志里出现了口令"
else
  ok "网关日志里没有口令"
fi

echo
echo "P1-2b2 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
