#!/usr/bin/env bash
# P1-2a 核对：主线单人进本。
#
# 完成标准（开发架构第 14 节第 25 行）：第二个人被拒绝。疲劳和周份额不变。
#
# 做这几段：
#   1. 编译战斗服、角色服、网关、探针
#   2. 静态断言：人数上限与扣减规则在 config/dungeons.json；两个服读同一份；
#      战斗服有 EnterRejected 这条回包；角色服主线不写疲劳与份额
#   3. 建表、账号、角色，起三个进程
#   4. 进本接口：1 人允许且回 entry_id；疲劳与份额前后都是 0
#   5. 2 人请求被拒（party_too_large）；未知副本、别人的角色、无令牌都被拒
#   6. 战斗服：第一个人进本成功，同一 party 的第二个人拿到 EnterRejected
#
# 用法：bash tools/check-p1-2a.sh
# 连库段要在有 gamedb 的机器上跑（笔记本）；缺凭据时明确失败。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"
BATTLE_DLL="$ROOT/src/Battle/bin/Release/net10.0/Battle.dll"
PROBE_DLL="$ROOT/tools/BattleProbe/bin/Release/net10.0/BattleProbe.dll"

BATTLE_PORT=${MMO_P12A_BATTLE_PORT:-60013}
CHAR_PORT=${MMO_P12A_CHAR_PORT:-62101}
GATEWAY_PORT=${MMO_P12A_GATEWAY_PORT:-61102}
ACCOUNT="p12a_user"
OTHER="p12a_other"
TEST_PASSWORD="p12a-Pw-44f0"
CHAR_NAME="p12a_hero"
PARTY="p12a-party-1"

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

enter_dungeon() {
  # $1=令牌（空串不带） $2=角色 $3=副本 $4=人数
  local token="$1" url="http://127.0.0.1:${GATEWAY_PORT}/dungeon/enter"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 10 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2},\"dungeon_id\":\"${3}\",\"party_size\":${4}}"
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

BUILD_OK=1
for proj in src/Battle/Battle.csproj src/Character/Character.csproj src/Gateway/Gateway.csproj tools/BattleProbe/BattleProbe.csproj; do
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
  echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
CFG="$ROOT/config/dungeons.json"
BATTLE_CFG="$ROOT/src/Battle/Config.cs"
BATTLE_SRV="$ROOT/src/Battle/Server.cs"
CHAR_SVC="$ROOT/src/Character/CharacterGrpcService.cs"

if grep -q '"id": "main"' "$CFG" && grep -q '"max_players": 1' "$CFG" \
   && grep -q '"charges_fatigue": false' "$CFG" && grep -q '"takes_weekly_share": false' "$CFG"; then
  ok "主线在配置里：1 人、不扣疲劳、不占周份额"
else
  bad "主线的人数或扣减规则不在配置里"
fi

if grep -q 'DungeonTable.Load' "$BATTLE_CFG" && grep -q 'DungeonTable.Load' "$ROOT/src/Character/CharacterProcess.cs"; then
  ok "战斗服与角色服读同一份副本表"
else
  bad "两个服各读各的，人数口径会分叉"
fi

if grep -q 'EnterRejected' "$BATTLE_SRV" && grep -q 'party_full' "$BATTLE_SRV"; then
  ok "战斗服满员时回 EnterRejected，不是无声断开"
else
  bad "战斗服满员时没有回执"
fi

if grep -q 'ChargesFatigue' "$CHAR_SVC" && grep -q 'TakesWeeklyShare' "$CHAR_SVC"; then
  ok "角色服进本时读扣减规则，主线不扣不占"
else
  bad "角色服没有读扣减规则"
fi

# 主线进本不扣疲劳：P1-2b1 起深渊会扣，但扣减只在 ChargesFatigue 分支里。
if grep -q 'if (!dungeon.ChargesFatigue)' "$CHAR_SVC" \
   && grep -q 'RecordEntryAsync' "$CHAR_SVC" \
   && grep -q '"charges_fatigue": false' "$CFG"; then
  ok "主线走不扣疲劳的分支，深渊的扣减碰不到主线"
else
  bad "主线的进本分支可能扣疲劳"
fi

echo "== 3. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env"
  echo
  echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

# 战斗服配置：固定端口，其余用仓库里的表。
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
  "db_pool_max": 4,
  "bag_slots": 40
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
  echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

for sql in character.sql dungeon_entry.sql inventory.sql currency.sql transaction.sql; do
  "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" \
    --sql "$ROOT/db/character/$sql" >/dev/null 2>&1 || true
done
MIGRATE_OK=1
for sql in character.sql dungeon_entry.sql; do
  if ! "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" \
        --sql "$ROOT/db/character/$sql" >"$TMP/migrate-$sql.log" 2>&1; then
    MIGRATE_OK=0
    tail -3 "$TMP/migrate-$sql.log" | detail
  fi
done
if [ "$MIGRATE_OK" = "1" ]; then
  ok "角色表（含疲劳与份额列）与进本记录表都在位"
else
  bad "有表建不出来"
  echo
  echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
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
  if ! kill -0 "$BATTLE_PID" 2>/dev/null; then
    break
  fi
  sleep 0.1
done

if [ "$BATTLE_READY" = "1" ]; then
  ok "战斗服在 127.0.0.1:${BATTLE_PORT} 上"
else
  bad "战斗服没起来"
  tail -8 "$TMP/battle.log" | detail
  echo
  echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
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
  if ! kill -0 "$CHARACTER_PID" 2>/dev/null; then
    break
  fi
  sleep 0.1
done

if [ "$CHAR_READY" = "1" ]; then
  ok "角色服在 127.0.0.1:${CHAR_PORT} 上（$(grep -m1 '^dungeons=' "$TMP/character.log")）"
else
  bad "角色服没起来"
  tail -8 "$TMP/character.log" | detail
  echo
  echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
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
  echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then
  ok "两个账号都能登录"
else
  bad "登录没拿到令牌"
  echo
  echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

# 上一次被中断可能留下同名角色。
"$DOTNET" "$CHARACTER_DLL" --delete-character "$CHAR_NAME" --config "$TMP/character.json" >/dev/null 2>&1 || true

CREATE="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/character?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"${CHAR_NAME}\",\"race\":\"mai\",\"class_id\":\"warrior\"}")"
split_response "$CREATE"
CHAR_ID="$(json_field "$BODY" character_id)"
if [ "$CODE" = "200" ] && [ -n "$CHAR_ID" ]; then
  ok "测试角色建好：id=${CHAR_ID}"
else
  bad "测试角色没建出来（HTTP ${CODE}）"
  echo
  echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 4. 进本接口：1 人允许，账不变 =="
RESERVES_BEFORE="$("$DOTNET" "$CHARACTER_DLL" --reserves-of "$CHAR_ID" --config "$TMP/character.json" 2>&1 | grep -m1 '^reserves=')"
ENTRY_BEFORE="$("$DOTNET" "$CHARACTER_DLL" --entry-count "$CHAR_ID" --config "$TMP/character.json" 2>&1 | grep -m1 '^entry_count=')"
printf '  %s\n  %s\n' "$RESERVES_BEFORE" "$ENTRY_BEFORE"

RESP="$(enter_dungeon "$TOKEN" "$CHAR_ID" main 1)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "True" ]; then
  ok "一个人进主线：允许，entry_id=$(json_field "$BODY" entry_id)"
else
  bad "一个人进不去（HTTP ${CODE}）：$(printf '%s' "$BODY" | head -c 120)"
fi

RESERVES_AFTER="$("$DOTNET" "$CHARACTER_DLL" --reserves-of "$CHAR_ID" --config "$TMP/character.json" 2>&1 | grep -m1 '^reserves=')"
printf '  %s\n' "$RESERVES_AFTER"

if printf '%s' "$RESERVES_AFTER" | grep -q 'fatigue=0 weekly_share=0'; then
  ok "进本之后疲劳与周份额都是 0：主线不扣不占"
else
  bad "进本之后账变了：${RESERVES_AFTER}"
fi

if [ "$RESERVES_BEFORE" = "$RESERVES_AFTER" ]; then
  ok "进本前后完全一致"
else
  bad "进本前后不一致：${RESERVES_BEFORE} → ${RESERVES_AFTER}"
fi

ENTRY_AFTER="$("$DOTNET" "$CHARACTER_DLL" --entry-count "$CHAR_ID" --config "$TMP/character.json" 2>&1 | grep -m1 '^entry_count=')"
if [ "$ENTRY_AFTER" = "entry_count=1 id=$CHAR_ID" ]; then
  ok "进本记录落了一行"
else
  bad "进本记录不对：${ENTRY_AFTER}"
fi

echo "== 5. 人数与参数校验 =="
RESP="$(enter_dungeon "$TOKEN" "$CHAR_ID" main 2)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" allowed)" = "False" ] \
   && [ "$(json_field "$BODY" reason)" = "party_too_large" ]; then
  ok "两个人进主线：被拒（party_too_large）"
else
  bad "两个人进主线没被拒：HTTP ${CODE} $(json_field "$BODY" reason)"
fi

ENTRY_AFTER2="$("$DOTNET" "$CHARACTER_DLL" --entry-count "$CHAR_ID" --config "$TMP/character.json" 2>&1 | grep -m1 '^entry_count=')"
if [ "$ENTRY_AFTER2" = "entry_count=1 id=$CHAR_ID" ]; then
  ok "被拒的那次没留下进本记录"
else
  bad "被拒的那次也记了一笔：${ENTRY_AFTER2}"
fi

# abyss 在 P1-2b1 起是已知副本，这里换一个真正不存在的编号。
RESP="$(enter_dungeon "$TOKEN" "$CHAR_ID" no_such_dungeon 1)"
split_response "$RESP"
if [ "$CODE" = "400" ] && [ "$(json_field "$BODY" error)" = "unknown_dungeon" ]; then
  ok "未知副本：400 unknown_dungeon"
else
  bad "未知副本没被拒：HTTP ${CODE}"
fi

RESP="$(enter_dungeon "$OTHER_TOKEN" "$CHAR_ID" main 1)"
split_response "$RESP"
if [ "$CODE" = "404" ] && [ "$(json_field "$BODY" error)" = "character_not_found" ]; then
  ok "别人的角色：404 character_not_found"
else
  bad "别人的角色也能进本：HTTP ${CODE}"
fi

RESP="$(enter_dungeon "" "$CHAR_ID" main 1)"
split_response "$RESP"
if [ "$CODE" = "401" ]; then
  ok "不带令牌：HTTP 401"
else
  bad "不带令牌也能进本：HTTP ${CODE}"
fi

echo "== 6. 战斗服：同一场的第二个人被拒 =="
# 走网关（P1-1a2 起长连接只从网关进），所以 URL 要带令牌。
GW_URL="ws://127.0.0.1:${GATEWAY_PORT}/battle?token=${TOKEN}&dungeon=main&party=${PARTY}"
# 第一个人开着连接不放：hold 6 秒，够第二个人撞上来。
"$DOTNET" "$PROBE_DLL" --url "${GW_URL}&class=warrior&char=1" --case p12a_join --expect open --hold-ms 6000 \
  > "$TMP/holder.txt" 2>&1 &
HOLDER_PID=$!

# 等第一个人真的进本（探针一进本就打 joined=ok），再让第二个人来。
HOLDER_OK=0
for _ in $(seq 1 60); do
  if grep -q 'joined=ok' "$TMP/holder.txt" 2>/dev/null; then
    HOLDER_OK=1
    break
  fi
  sleep 0.1
done

if [ "$HOLDER_OK" = "1" ]; then
  ok "第一个人进本成功：$(grep -m1 'joined=ok' "$TMP/holder.txt" | sed 's/^joined=ok //')"
else
  bad "第一个人没进去"
  head -6 "$TMP/holder.txt" 2>/dev/null | detail
fi

SECOND="$("$DOTNET" "$PROBE_DLL" --url "${GW_URL}&class=warrior&char=2" --case p12a_join --expect reject 2>&1)"
if printf '%s' "$SECOND" | grep -q '^verdict=ok' && printf '%s' "$SECOND" | grep -q 'reason=party_full'; then
  ok "第二个人被拒：收到 EnterRejected reason=party_full（$(printf '%s' "$SECOND" | grep -m1 'limit=' | sed 's/^case=p12a_join //')）"
else
  bad "第二个人没有被拒，或者拒绝没有回执"
  printf '%s\n' "$SECOND" | head -4 | detail
fi

if grep -q 'join_reject=party=p12a-party-1 dungeon=main reason=party_full' "$TMP/battle.log"; then
  ok "战斗服日志记了这次拒绝"
else
  bad "战斗服日志里没有这条拒绝"
fi

# 收掉第一个人，等它退出（cleanup 也会收，这里主动收一下让日志干净）。
stop_pid "$HOLDER_PID"
HOLDER_PID=""

# 第一个人走了以后，第二个人应该能进——上限是按当前人数算的。
sleep 0.5
THIRD="$("$DOTNET" "$PROBE_DLL" --url "${GW_URL}&class=warrior&char=3" --case p12a_join --expect open 2>&1)"
if printf '%s' "$THIRD" | grep -q '^verdict=ok'; then
  ok "第一个人走了之后，下一个人能进"
else
  bad "第一个人走了，下一个人还是进不去"
  printf '%s\n' "$THIRD" | head -4 | detail
fi

if grep -q "$TEST_PASSWORD" "$TMP/gateway.log" 2>/dev/null; then
  bad "网关日志里出现了口令"
else
  ok "网关日志里没有口令"
fi

echo
echo "P1-2a 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
