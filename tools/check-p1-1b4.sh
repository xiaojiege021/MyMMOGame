#!/usr/bin/env bash
# P1-1b4 核对：金币、五档灵石、元宝的增减。
#
# 完成标准（开发架构第 14 节第 23 行）：三种货币都能加减。减到负数被拒绝。
#
# 做这几段：
#   1. 编译网关与角色服
#   2. 静态断言：币种表带七个币种；余额库层不许为负；加减是一条语句不是先读再写；
#      网关不写货币；扣负数不夹到 0
#   3. 建表、两个账号，起角色服与网关
#   4. 金币 +1000 / -300；元宝 +50；灵石五档各 +100
#   5. 减到负数被拒（insufficient），库里保持原值
#   6. 未知币种、别人的角色、无令牌 都被拒
#   7. 绕过接口直写负数，被库的 CHECK 拒掉
#
# 用法：bash tools/check-p1-1b4.sh
# 连库段要在有 gamedb 的机器上跑（笔记本）；缺凭据时明确失败。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"

CHAR_PORT=${MMO_P11B4_CHAR_PORT:-62097}
GATEWAY_PORT=${MMO_P11B4_GATEWAY_PORT:-61098}
ACCOUNT="p11b4_user"
OTHER="p11b4_other"
TEST_PASSWORD="p11b4-Pw-18e2"
CHAR_NAME="p11b4_hero"

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

add_currency() {
  # $1=令牌（空串表示不带） $2=角色 $3=币种 $4=增减量
  local token="$1" url="http://127.0.0.1:${GATEWAY_PORT}/currency/add"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 10 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' \
    -d "{\"character_id\":${2},\"kind\":\"${3}\",\"delta\":${4}}"
}

balance_of() {
  # $1=令牌 $2=角色 $3=币种
  curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/currency?token=$1&character_id=$2" \
    | python3 -c 'import json,sys
try:
    data = json.load(sys.stdin)
except Exception:
    print(""); raise SystemExit
for b in data.get("balances", []):
    if b.get("kind") == "'"$3"'":
        print(b.get("amount"))
        raise SystemExit
print("")'
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-1b4 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Gateway/Gateway.csproj" -c Release --nologo -v q >"$TMP/build-gw.log" 2>&1 \
  && "$DOTNET" build "$ROOT/src/Character/Character.csproj" -c Release --nologo -v q >"$TMP/build-char.log" 2>&1; then
  ok "网关与角色服编译通过"
else
  bad "编译失败"
  grep -E "error " "$TMP/build-gw.log" "$TMP/build-char.log" | head -5 | detail
  echo
  echo "P1-1b4 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
SQL="$ROOT/db/character/currency.sql"
CFG="$ROOT/config/currency.json"
SVC="$ROOT/src/Character/CharacterGrpcService.cs"
REPO="$ROOT/src/Character/CurrencyRepository.cs"

for kind in gold stone_1 stone_2 stone_3 stone_4 stone_5 yuanbao; do
  if ! grep -q "\"id\": \"${kind}\"" "$CFG"; then
    bad "币种表缺 ${kind}"
    CURRENCY_OK=0
  fi
done
if [ "${CURRENCY_OK:-1}" = "1" ]; then
  ok "币种表七个币种齐：金币、灵石五档、元宝"
fi

if grep -q 'amount[[:space:]]*bigint' "$SQL" && grep -q 'CHECK (amount >= 0)' "$SQL"; then
  ok "余额在库层不许为负"
else
  bad "库层没有禁止负数余额"
fi

# 加走 upsert，减走 update + 余额条件——不能合成一条 upsert：
# ON CONFLICT 会先校验拟插入行，扣钱传的负数会直接撞 CHECK。
if grep -q 'on conflict (character_id, kind) do update' "$REPO" \
   && grep -qF 'and amount + $3 >= 0' "$REPO"; then
  ok "加走 upsert、减走带余额条件的 update，各自一条语句原子完成"
else
  bad "加减不是原子语句，或者扣钱仍走 upsert（会被 CHECK 提前拦下）"
fi

# 只盯「余额」相关的夹值：服务里别的字段用 Math.Max(..., 0) 与余额无关，
# 早期这条断言扫整个文件，会把进本回包的 entry_id 转换误判成夹余额。
if grep -nE 'Math\.(Max|Clamp)\(' "$SVC" "$REPO" 2>/dev/null \
   | grep -E 'amount|balance' >/dev/null; then
  bad "扣到负数时被夹到 0 了，调用方会以为扣成功"
else
  ok "余额不足直接拒绝，不夹到 0"
fi

if grep -rq 'update "角色"\.currency' "$ROOT/src/Gateway" 2>/dev/null; then
  bad "网关直接写了货币，角色服的唯一写入被绕过"
else
  ok "网关不写货币，只转请求"
fi

echo "== 3. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env"
  echo
  echo "P1-1b4 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

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
  "character_upstream": "http://127.0.0.1:${CHAR_PORT}"
}
JSON

if ! "$DOTNET" "$GATEWAY_DLL" --migrate --config "$TMP/gateway.json" \
      --sql "$ROOT/db/character/account.sql" >"$TMP/migrate-account.log" 2>&1; then
  bad "账号表建不出来"
  tail -5 "$TMP/migrate-account.log" | detail
  echo
  echo "P1-1b4 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

"$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" --sql "$ROOT/db/character/character.sql" >/dev/null 2>&1 || true
if "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" --sql "$SQL" >"$TMP/migrate-currency.log" 2>&1; then
  ok "账号、角色、货币三张表都在位"
else
  bad "货币表建不出来"
  tail -5 "$TMP/migrate-currency.log" | detail
  echo
  echo "P1-1b4 核对：$PASS 项通过，$FAIL 项未通过"
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
  ok "角色服在 127.0.0.1:${CHAR_PORT} 上（$(grep -m1 '^currencies=' "$TMP/character.log")）"
else
  bad "角色服没起来"
  tail -8 "$TMP/character.log" | detail
  echo
  echo "P1-1b4 核对：$PASS 项通过，$FAIL 项未通过"
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
  echo "P1-1b4 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then
  ok "两个账号都能登录"
else
  bad "登录没拿到令牌"
  echo
  echo "P1-1b4 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

CREATE="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/character?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"${CHAR_NAME}\",\"race\":\"lan\",\"class_id\":\"mage\"}")"
split_response "$CREATE"
CHAR_ID="$(json_field "$BODY" character_id)"
if [ "$CODE" = "200" ] && [ -n "$CHAR_ID" ]; then
  ok "测试角色建好：id=${CHAR_ID}"
else
  bad "测试角色没建出来（HTTP ${CODE}）"
  echo
  echo "P1-1b4 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 4. 三种货币都能加能减 =="
RESP="$(add_currency "$TOKEN" "$CHAR_ID" gold 1000)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" amount)" = "1000" ]; then
  ok "金币 +1000 → 1000"
else
  bad "金币加不进去（HTTP ${CODE}）：$(printf '%s' "$BODY" | head -c 100)"
fi

RESP="$(add_currency "$TOKEN" "$CHAR_ID" gold -300)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" amount)" = "700" ]; then
  ok "金币 -300 → 700"
else
  bad "金币减不掉（HTTP ${CODE}）：$(printf '%s' "$BODY" | head -c 100)"
  # 502 是角色服内部异常，把它的日志带出来，不然只知道「抛了」不知道为什么。
  tail -12 "$TMP/character.log" 2>/dev/null | detail
fi

RESP="$(add_currency "$TOKEN" "$CHAR_ID" yuanbao 50)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" amount)" = "50" ]; then
  ok "元宝 +50 → 50"
else
  bad "元宝加不进去（HTTP ${CODE}）"
fi

RESP="$(add_currency "$TOKEN" "$CHAR_ID" yuanbao -20)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" amount)" = "30" ]; then
  ok "元宝 -20 → 30"
else
  bad "元宝减不掉（HTTP ${CODE}）"
fi

STONE_OK=1
for tier in stone_1 stone_2 stone_3 stone_4 stone_5; do
  RESP="$(add_currency "$TOKEN" "$CHAR_ID" "$tier" 100)"
  split_response "$RESP"
  [ "$CODE" = "200" ] && [ "$(json_field "$BODY" amount)" = "100" ] || STONE_OK=0
done
if [ "$STONE_OK" = "1" ]; then
  ok "灵石五档各记一笔 100"
else
  bad "灵石有一档记不进去"
fi

echo "== 5. 减到负数被拒绝 =="
RESP="$(add_currency "$TOKEN" "$CHAR_ID" gold -800)"
split_response "$RESP"
if [ "$CODE" = "412" ] && [ "$(json_field "$BODY" error)" = "insufficient" ]; then
  ok "金币只有 700 却扣 800：被拒（412 insufficient）"
else
  bad "扣成负数没被拒：HTTP ${CODE} $(printf '%s' "$BODY" | head -c 80)"
fi

GOLD_NOW="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
if [ "$GOLD_NOW" = "700" ]; then
  ok "被拒之后金币仍是 700"
else
  bad "被拒之后金币变成 ${GOLD_NOW}"
fi

RESP="$(add_currency "$TOKEN" "$CHAR_ID" yuanbao -31)"
split_response "$RESP"
if [ "$CODE" = "412" ]; then
  ok "元宝 30 扣 31：被拒"
else
  bad "元宝扣成负数没被拒：HTTP ${CODE}"
fi

# 恰好扣到 0 是允许的。
RESP="$(add_currency "$TOKEN" "$CHAR_ID" yuanbao -30)"
split_response "$RESP"
if [ "$CODE" = "200" ] && [ "$(json_field "$BODY" amount)" = "0" ]; then
  ok "恰好扣到 0：允许"
else
  bad "扣到 0 被拒了（HTTP ${CODE}）"
fi

echo "== 6. 该拒的都拒了 =="
RESP="$(add_currency "$TOKEN" "$CHAR_ID" crystal 100)"
split_response "$RESP"
if [ "$CODE" = "400" ] && [ "$(json_field "$BODY" error)" = "unknown_currency" ]; then
  ok "未知币种：HTTP 400 unknown_currency"
else
  bad "未知币种没被拒：HTTP ${CODE}"
fi

RESP="$(add_currency "$OTHER_TOKEN" "$CHAR_ID" gold 100)"
split_response "$RESP"
if [ "$CODE" = "404" ] && [ "$(json_field "$BODY" error)" = "character_not_found" ]; then
  ok "别人的角色：404 character_not_found"
else
  bad "别人的角色也能改钱：HTTP ${CODE}"
fi

GOLD_NOW="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
if [ "$GOLD_NOW" = "700" ]; then
  ok "别人的请求没有改动金币"
else
  bad "别人的请求改动了金币：${GOLD_NOW}"
fi

RESP="$(add_currency "" "$CHAR_ID" gold 100)"
split_response "$RESP"
if [ "$CODE" = "401" ]; then
  ok "不带令牌：HTTP 401"
else
  bad "不带令牌也能加钱：HTTP ${CODE}"
fi

echo "== 7. 绕过接口直写负数 =="
RAW="$("$DOTNET" "$CHARACTER_DLL" --raw-currency-write "$CHAR_ID" gold -1 --config "$TMP/character.json" 2>&1)"
if printf '%s' "$RAW" | grep -q 'raw_currency=rejected'; then
  ok "直写 -1 被库的 CHECK 拒掉：$(printf '%s' "$RAW" | grep -m1 'raw_currency=rejected' | sed 's/.*sqlstate=/sqlstate=/')"
else
  bad "直写负数竟然写进去了"
  printf '%s\n' "$RAW" | head -3 | detail
fi

GOLD_NOW="$(balance_of "$TOKEN" "$CHAR_ID" gold)"
if [ "$GOLD_NOW" = "700" ]; then
  ok "直写被拒之后金币仍是 700"
else
  bad "直写之后金币是 ${GOLD_NOW}"
fi

# 整包读回：七个币种都在，没记过账的按 0。
BALANCES="$(curl -s -m 10 "http://127.0.0.1:${GATEWAY_PORT}/currency?token=${TOKEN}&character_id=${CHAR_ID}")"
KINDS="$(printf '%s' "$BALANCES" | python3 -c 'import json,sys
try:
    print(len(json.load(sys.stdin).get("balances", [])))
except Exception:
    print("-1")')"
if [ "$KINDS" = "7" ]; then
  ok "整包读回七个币种"
else
  bad "整包读回的币种数不对：${KINDS}"
fi

if grep -q "$TEST_PASSWORD" "$TMP/gateway.log" 2>/dev/null; then
  bad "网关日志里出现了口令"
else
  ok "网关日志里没有口令"
fi

echo
echo "P1-1b4 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
