#!/usr/bin/env bash
# P1-2b1 核对：深渊按人数扣疲劳，一天到 6000 不能再进。
#
# 完成标准（开发架构第 14 节第 26 行 + 第 11 节 P1-2）：
#   4 人进一次扣 4 倍疲劳；一天内把疲劳打到 6000 后不能再进深渊。
#
# 做这几段：
#   1. 编译战斗服、角色服、网关、探针
#   2. 静态断言：深渊规则在配置、日上限与日界在库层、按人数相乘、
#      扣减与进本记录同一个事务、主线仍不扣、两服读同一份副本表
#   3. 建表、账号、六个测试角色，起三个进程
#   4. 主线回归（不扣疲劳）、单人深渊、四人深渊、累计到上限、
#      恰好到上限、人数超限、跨天归零、并发只有一笔成功、库层直写
#   5. 别人的角色、无令牌、未知副本、战斗服两人同场
#
# 用法：bash tools/check/check-p1-2b1.sh
# 连库段要在有 gamedb 的机器上跑（笔记本）；缺凭据时明确失败。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
PROBE_DLL="$ROOT/tools/probe/BattleProbe/bin/Release/net10.0/BattleProbe.dll"

BATTLE_PORT=${MMO_P12B1_BATTLE_PORT:-60014}
CHAR_PORT=${MMO_P12B1_CHAR_PORT:-62102}
GATEWAY_PORT=${MMO_P12B1_GATEWAY_PORT:-61103}
ACCOUNT="p12b1_user"
OTHER="p12b1_other"
TEST_PASSWORD="p12b1-Pw-7c31"
PARTY="p12b1-party-1"

# 每个角色各管一段，避免互相把疲劳搅乱。
NAME_MAIN="p12b1_main"
NAME_SOLO="p12b1_solo"
NAME_CAP="p12b1_cap"
NAME_P1="p12b1_p1"
NAME_P2="p12b1_p2"
NAME_P3="p12b1_p3"
NAME_P4="p12b1_p4"
CHAR_NAMES=("$NAME_MAIN" "$NAME_SOLO" "$NAME_CAP" "$NAME_P1" "$NAME_P2" "$NAME_P3" "$NAME_P4")

PASS=0
FAIL=0
TMP="$(mktemp -d)"
BATTLE_PID=""
CHARACTER_PID=""
GATEWAY_PID=""
HOLDER_PID=""
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
  stop_pid "$HOLDER_PID"
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
  local resp body code
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

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
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
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
CFG="$ROOT/config/dungeons.json"
SQL="$ROOT/db/character/character.sql"
ENTRY_REPO="$ROOT/src/Character/DungeonEntryRepository.cs"
SVC="$ROOT/src/Character/CharacterGrpcService.cs"

if grep -q '"id": "abyss"' "$CFG" && grep -q '"max_players": 4' "$CFG" \
   && grep -q '"charges_fatigue": true' "$CFG" \
   && grep -q '"takes_weekly_share": false' "$CFG" \
   && grep -q '"fatigue_per_player"' "$CFG"; then
  ok "深渊在配置里：1–4 人、扣疲劳、不占周份额、基础消耗留配置"
else
  bad "深渊的人数或扣减规则不在配置里"
fi

if grep -q '"fatigue_daily_cap": 6000' "$CFG" \
   && grep -q 'fatigue <= 6000' "$SQL" \
   && grep -q 'fatigue_day date NOT NULL DEFAULT CURRENT_DATE' "$SQL"; then
  ok "日上限 6000 在配置与库层，日界 fatigue_day 在角色表"
else
  bad "日上限或日界没有两道闸"
fi

if grep -q 'FatiguePerPlayer \* partySize' "$ENTRY_REPO"; then
  ok "扣疲劳 = 单人基础 × 进本人数"
else
  bad "扣疲劳没有按人数倍增"
fi

if grep -q 'BeginTransactionAsync' "$ENTRY_REPO" \
   && grep -q 'CommitAsync' "$ENTRY_REPO" \
   && grep -q 'RollbackAsync' "$ENTRY_REPO" \
   && grep -q 'dungeon_entry (character_id, dungeon_id, party_size)' "$ENTRY_REPO"; then
  ok "扣疲劳、日界与进本记录在同一个事务里，被拒能整笔回滚"
else
  bad "进本扣减不是整笔事务"
fi

if grep -q 'fatigue_day <> current_date' "$ENTRY_REPO"; then
  ok "跨天按数据库 current_date 归零，上限是「一天」而不是永久累计"
else
  bad "没有日界判断，6000 之后会永远进不去"
fi

if grep -q 'if (!dungeon.ChargesFatigue)' "$SVC" \
   && grep -q '"charges_fatigue": false' "$CFG"; then
  ok "主线仍走不扣疲劳的分支（P1-2a 不回退）"
else
  bad "主线可能被扣疲劳"
fi

if grep -q 'DungeonTable.Load(config.DungeonTable)' "$ROOT/src/Character/CharacterProcess.cs" \
   && grep -q 'DungeonTable.Load' "$ROOT/src/Battle/Config.cs" \
   && grep -q 'dungeons.FatigueDailyCap' "$SVC"; then
  ok "两服仍读同一份副本表，日上限从配置读而不是写死在角色服里"
else
  bad "副本表或日上限口径分叉"
fi

echo "== 3. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env"
  echo
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
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
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
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
  ok "角色表（含日界与上限 CHECK）、进本记录表都在位"
else
  echo
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
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
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
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
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
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
  if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then break; fi
  sleep 0.2
done
if [ "$GATEWAY_READY" = "1" ]; then
  ok "网关在 127.0.0.1:${GATEWAY_PORT} 上"
else
  bad "网关没起来"
  tail -8 "$TMP/gateway.log" | detail
  echo
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then
  ok "两个账号都能登录"
else
  bad "登录没拿到令牌"
  echo
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
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
    "$NAME_MAIN") ID_MAIN="$NEW_ID" ;;
    "$NAME_SOLO") ID_SOLO="$NEW_ID" ;;
    "$NAME_CAP")  ID_CAP="$NEW_ID" ;;
    "$NAME_P1")   ID_P1="$NEW_ID" ;;
    "$NAME_P2")   ID_P2="$NEW_ID" ;;
    "$NAME_P3")   ID_P3="$NEW_ID" ;;
    "$NAME_P4")   ID_P4="$NEW_ID" ;;
  esac
done
if [ "$CREATED" = "1" ]; then
  ok "七个测试角色都建好了"
else
  echo
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 4. 主线回归：进本不扣疲劳 =="
RESP="$(enter_dungeon "$TOKEN" "$ID_MAIN" main 1)"
split_response "$RESP"
STATE="$(fatigue_state "$ID_MAIN")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(json_field "$BODY" fatigue_cost)" = "0" ] \
   && [ "$(fatigue_of "$STATE")" = "0" ] && [ "$(share_of "$STATE")" = "0" ]; then
  ok "主线 1 人进本允许，疲劳与周份额仍是 0、cost=0"
else
  bad "主线进本动了账：HTTP ${CODE} ${STATE}"
fi

echo "== 5. 单人深渊：扣 1 倍，累加看得见 =="
RESP="$(enter_dungeon "$TOKEN" "$ID_SOLO" abyss 1)"
split_response "$RESP"
STATE="$(fatigue_state "$ID_SOLO")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(json_field "$BODY" fatigue_cost)" = "1500" ] \
   && [ "$(fatigue_of "$STATE")" = "1500" ] && [ "$(share_of "$STATE")" = "0" ] \
   && [ -n "$(json_field "$BODY" entry_id)" ] && [ "$(json_field "$BODY" entry_id)" != "0" ]; then
  ok "单人进一次：扣 1500（1 倍），不占周份额，留了进本记录"
else
  bad "单人进本没按 1 倍扣：HTTP ${CODE} ${STATE} ${BODY}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_SOLO" abyss 1)"
split_response "$RESP"
STATE="$(fatigue_state "$ID_SOLO")"
if [ "$CODE" = "200" ] && [ "$(fatigue_of "$STATE")" = "3000" ] \
   && [ "$(entries_of "$(entry_count "$ID_SOLO")")" = "2" ]; then
  ok "单人再进一次：1500 → 3000，两条进本记录"
else
  bad "累加不对：${STATE} $(entry_count "$ID_SOLO")"
fi

echo "== 6. 人数 5 被拒，账不动 =="
BEFORE="$(fatigue_state "$ID_SOLO")"
E_BEFORE="$(entry_count "$ID_SOLO")"
RESP="$(enter_dungeon "$TOKEN" "$ID_SOLO" abyss 5)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "party_too_large" ]; then
  ok "5 人进深渊：party_too_large"
else
  bad "5 人没被拒：HTTP ${CODE} $(json_field "$BODY" reason)"
fi
if [ "$(fatigue_state "$ID_SOLO")" = "$BEFORE" ] \
   && [ "$(entry_count "$ID_SOLO")" = "$E_BEFORE" ]; then
  ok "被拒的那次没扣疲劳，也没留进本记录"
else
  bad "被拒的那次改了账：$(fatigue_state "$ID_SOLO") $(entry_count "$ID_SOLO")"
fi

echo "== 7. 4 人进一次：每人扣 4 倍 =="
TEAM_OK=1
for id in "$ID_P1" "$ID_P2" "$ID_P3" "$ID_P4"; do
  RESP="$(enter_dungeon "$TOKEN" "$id" abyss 4)"
  split_response "$RESP"
  STATE="$(fatigue_state "$id")"
  if [ "$CODE" != "200" ] || [ "$(json_field "$BODY" allowed)" != "True" ] \
     || [ "$(json_field "$BODY" fatigue_cost)" != "6000" ] \
     || [ "$(fatigue_of "$STATE")" != "6000" ] \
     || [ "$(share_of "$STATE")" != "0" ] \
     || [ "$(json_field "$BODY" fatigue_daily_cap)" != "6000" ]; then
    TEAM_OK=0
    bad "4 人队里 id=$id 没按 4 倍扣：HTTP ${CODE} ${STATE} ${BODY}"
  fi
done
if [ "$TEAM_OK" = "1" ]; then
  ok "四个角色各扣 6000（1500×4），周份额都是 0，恰好到上限允许"
else
  echo
  echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 8. 到 6000 后再进：整笔拒绝 =="
BEFORE="$(fatigue_state "$ID_P1")"
E_BEFORE="$(entry_count "$ID_P1")"
RESP="$(enter_dungeon "$TOKEN" "$ID_P1" abyss 1)"
split_response "$RESP"
STATE="$(fatigue_state "$ID_P1")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "fatigue_exhausted" ] \
   && [ "$(json_field "$BODY" fatigue_cost)" = "0" ] \
   && [ "$(fatigue_of "$STATE")" = "6000" ] \
   && [ "$(entry_count "$ID_P1")" = "$E_BEFORE" ] \
   && [ "$STATE" = "$BEFORE" ]; then
  ok "到上限后再进：fatigue_exhausted，疲劳仍 6000，不扣不记"
else
  bad "到上限还能进，或者账被改了：HTTP ${CODE} ${STATE} ${BODY}"
fi

echo "== 9. 一天内累计打到 6000 =="
CAP_OK=1
for expected in 1500 3000 4500 6000; do
  RESP="$(enter_dungeon "$TOKEN" "$ID_CAP" abyss 1)"
  split_response "$RESP"
  STATE="$(fatigue_state "$ID_CAP")"
  if [ "$CODE" != "200" ] || [ "$(json_field "$BODY" allowed)" != "True" ] \
     || [ "$(fatigue_of "$STATE")" != "$expected" ]; then
    CAP_OK=0
    bad "累计这一步不对（期望 $expected）：HTTP ${CODE} ${STATE} ${BODY}"
  fi
done
if [ "$CAP_OK" = "1" ]; then
  ok "单人连进四次：1500 / 3000 / 4500 / 6000，第四次恰好到上限允许"
fi

E_BEFORE="$(entry_count "$ID_CAP")"
RESP="$(enter_dungeon "$TOKEN" "$ID_CAP" abyss 1)"
split_response "$RESP"
STATE="$(fatigue_state "$ID_CAP")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "fatigue_exhausted" ] \
   && [ "$(fatigue_of "$STATE")" = "6000" ] \
   && [ "$(entry_count "$ID_CAP")" = "$E_BEFORE" ]; then
  ok "第五次进不去：一天到 6000 就不能再进深渊"
else
  bad "第五次没被挡住：HTTP ${CODE} ${STATE} ${BODY}"
fi

echo "== 10. 库层：绕过接口写 6001 被挡 =="
RAW="$("$DOTNET" "$CHARACTER_DLL" --raw-fatigue-write "$ID_CAP" 6001 \
  --config "$TMP/character.json" 2>&1 | grep -m1 '^raw_fatigue=' || true)"
STATE="$(fatigue_state "$ID_CAP")"
if printf '%s' "$RAW" | grep -q 'raw_fatigue=rejected' \
   && printf '%s' "$RAW" | grep -q 'sqlstate=23514' \
   && [ "$(fatigue_of "$STATE")" = "6000" ]; then
  ok "直写 6001 被库 CHECK 拒（23514），库里仍是 6000：$RAW"
else
  bad "库层没挡住越界值：$RAW ${STATE}"
fi

echo "== 11. 跨天：日界变了从 0 起算 =="
YESTERDAY="$(date -d 'yesterday' +%F 2>/dev/null || date -v-1d +%F)"
"$DOTNET" "$CHARACTER_DLL" --set-fatigue-day "$ID_CAP" "$YESTERDAY" \
  --config "$TMP/character.json" >"$TMP/set-day.log" 2>&1 || true
RESP="$(enter_dungeon "$TOKEN" "$ID_CAP" abyss 1)"
split_response "$RESP"
STATE="$(fatigue_state "$ID_CAP")"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ] \
   && [ "$(fatigue_of "$STATE")" = "1500" ] \
   && printf '%s' "$STATE" | grep -q "day=$(date +%F)"; then
  ok "把日界改到昨天后再进：疲劳归零重算成 1500，日界刷成今天"
else
  bad "跨天没有归零：HTTP ${CODE} ${STATE} ${BODY}"
fi

echo "== 12. 并发两笔冲同一上限，只有一笔成 =="
# 当前 1500，两笔各 3 人（4500）：一笔到 6000，另一笔应被拒。
( enter_dungeon "$TOKEN" "$ID_CAP" abyss 3 >"$TMP/concurrent-1.txt" 2>&1 ) & C1=$!
( enter_dungeon "$TOKEN" "$ID_CAP" abyss 3 >"$TMP/concurrent-2.txt" 2>&1 ) & C2=$!
wait "$C1" "$C2"
R1="$(cat "$TMP/concurrent-1.txt")"
R2="$(cat "$TMP/concurrent-2.txt")"
split_response "$R1"; A1="$BODY"; C_1="$CODE"
split_response "$R2"; A2="$BODY"; C_2="$CODE"
ALLOWED=0
EXHAUSTED=0
for pair in "$C_1|$A1" "$C_2|$A2"; do
  code="${pair%%|*}"; body="${pair#*|}"
  if [ "$code" = "200" ] && [ "$(json_field "$body" allowed)" = "True" ]; then
    ALLOWED=$((ALLOWED + 1))
  fi
  if [ "$code" = "200" ] && [ "$(json_field "$body" reason)" = "fatigue_exhausted" ]; then
    EXHAUSTED=$((EXHAUSTED + 1))
  fi
done
STATE="$(fatigue_state "$ID_CAP")"
if [ "$ALLOWED" = "1" ] && [ "$EXHAUSTED" = "1" ] \
   && [ "$(fatigue_of "$STATE")" = "6000" ]; then
  ok "并发两笔各扣 4500：一成一败（fatigue_exhausted），疲劳停在 6000"
else
  bad "并发没守住上限：成功 $ALLOWED 笔、拒绝 $EXHAUSTED 笔，${STATE}"
  printf '%s\n' "$R1" "$R2" | head -8 | detail
fi

echo "== 13. 别人的角色、无令牌、未知副本 =="
RESP="$(enter_dungeon "$TOKEN" "$ID_CAP" abyss 5)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" max_players)" = "4" ]; then
  ok "深渊人数上限是 4（响应里也回给客户端）"
else
  bad "人数上限回包不对：HTTP ${CODE} ${BODY}"
fi

RESP="$(enter_dungeon "$OTHER_TOKEN" "$ID_SOLO" abyss 1)"
split_response "$RESP"
if [ "$CODE" = "404" ] && [ "$(json_field "$BODY" error)" = "character_not_found" ]; then
  ok "别人的角色：404 character_not_found"
else
  bad "别人的角色也能扣疲劳：HTTP ${CODE}"
fi

RESP="$(enter_dungeon "" "$ID_SOLO" abyss 1)"
split_response "$RESP"
if [ "$CODE" = "401" ]; then
  ok "不带令牌：HTTP 401"
else
  bad "不带令牌也能进本：HTTP ${CODE}"
fi

RESP="$(enter_dungeon "$TOKEN" "$ID_SOLO" no_such_dungeon 1)"
split_response "$RESP"
if [ "$CODE" = "400" ] && [ "$(json_field "$BODY" error)" = "unknown_dungeon" ]; then
  ok "未知副本：400 unknown_dungeon"
else
  bad "未知副本没被拒：HTTP ${CODE}"
fi

echo "== 14. 战斗服：深渊同一场能进两个人 =="
GW_URL="ws://127.0.0.1:${GATEWAY_PORT}/battle?token=${TOKEN}&dungeon=abyss&party=${PARTY}"
"$DOTNET" "$PROBE_DLL" --url "${GW_URL}&class=warrior&char=1" \
  --case p12a_join --expect open --hold-ms 8000 >"$TMP/holder.txt" 2>&1 &
HOLDER_PID=$!
HOLDER_OK=0
for _ in $(seq 1 60); do
  if grep -q 'joined=ok' "$TMP/holder.txt" 2>/dev/null; then
    HOLDER_OK=1
    break
  fi
  sleep 0.1
done
if [ "$HOLDER_OK" = "1" ]; then
  ok "第一个人进深渊本成功"
else
  bad "第一个人没进去"
  head -6 "$TMP/holder.txt" 2>/dev/null | detail
fi

SECOND="$("$DOTNET" "$PROBE_DLL" --url "${GW_URL}&class=warrior&char=2" \
  --case p12a_join --expect open 2>&1)"
if printf '%s' "$SECOND" | grep -q '^verdict=ok' \
   && printf '%s' "$SECOND" | grep -q 'result=opened'; then
  ok "第二个人也能进同一场（深渊不是主线的 1 人上限）"
else
  bad "第二个人被挡住了，或者没有开场回包"
  printf '%s\n' "$SECOND" | head -4 | detail
fi
stop_pid "$HOLDER_PID"
HOLDER_PID=""

if grep -q "$TEST_PASSWORD" "$TMP/gateway.log" 2>/dev/null; then
  bad "网关日志里出现了口令"
else
  ok "网关日志里没有口令"
fi

echo
echo "P1-2b1 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
