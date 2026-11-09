#!/usr/bin/env bash
# P1-1b2 核对：等级写入封顶 100。
#
# 完成标准（开发架构第 14 节第 21 行）：写入 101 被拒绝，等级停在 100。
#
# 做这几段：
#   1. 编译网关与角色服
#   2. 静态断言：上限只在角色服判、101 是拒绝不是夹、角色表有 CHECK、写入带账号
#   3. 建表、写两个测试账号，起角色服与网关
#   4. 建一个角色，升到 100 成功；写 101 被拒且库里仍是 100；写 0 与负数被拒
#   5. 拿另一个账号的令牌改这个角色，改不动（按不存在处理）
#   6. 绕过接口直写库 101，被库的 CHECK 拒掉，库里仍是 100
#   7. 不带令牌写等级 401，且库里不变
#
# 用法：bash tools/check-p1-1b2.sh
# 连库段要在有 gamedb 的机器上跑（笔记本）；缺凭据时明确失败。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
CHARACTER_DLL="$ROOT/src/Character/bin/Release/net10.0/Character.dll"

CHAR_PORT=${MMO_P11B2_CHAR_PORT:-62093}
GATEWAY_PORT=${MMO_P11B2_GATEWAY_PORT:-61094}
ACCOUNT="p11b2_user"
OTHER="p11b2_other"
TEST_PASSWORD="p11b2-Pw-33a7"
CHAR_NAME="p11b2_hero"

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

post_level() {
  # $1=令牌（空串表示不带） $2=JSON 体
  local token="$1" body="$2" url="http://127.0.0.1:${GATEWAY_PORT}/character/level"
  [ -n "$token" ] && url="${url}?token=${token}"
  curl -s -m 10 -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' -d "$body"
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

level_in_db() {
  "$DOTNET" "$CHARACTER_DLL" --level-of "$1" --config "$TMP/character.json" 2>&1 \
    | grep -m1 '^level=' | tr ' ' '\n' | sed -n 's/^level=//p' | tail -1
}

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-1b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Gateway/Gateway.csproj" -c Release --nologo -v q >"$TMP/build-gw.log" 2>&1 \
  && "$DOTNET" build "$ROOT/src/Character/Character.csproj" -c Release --nologo -v q >"$TMP/build-char.log" 2>&1; then
  ok "网关与角色服编译通过"
else
  bad "编译失败"
  grep -E "error " "$TMP/build-gw.log" "$TMP/build-char.log" | head -5 | detail
  echo
  echo "P1-1b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
SQL="$ROOT/db/character/character.sql"
SVC="$ROOT/src/Character/CharacterGrpcService.cs"
REPO="$ROOT/src/Character/CharacterRepository.cs"

if grep -qE 'level[[:space:]]+integer[[:space:]]+NOT NULL[[:space:]]+DEFAULT[[:space:]]+1[[:space:]]+CHECK[[:space:]]*\(level BETWEEN 1 AND 100\)' "$SQL"; then
  ok "角色表在库层就有 1–100 的 CHECK，绕过接口也写不进 101"
else
  bad "角色表没有等级 CHECK"
fi

if grep -q 'level_out_of_range' "$SVC"; then
  ok "越界等级按拒绝处理，错误码 level_out_of_range"
else
  bad "没有越界拒绝的错误码"
fi

# 拒绝不等于夹到 100：不能在服务端出现「超过就改成 100」的写法。
if grep -nE 'Math\.(Min|Clamp)\([^)]*100' "$SVC" "$REPO" 2>/dev/null >/dev/null; then
  bad "服务端把越界等级夹到了 100，调用方会以为写成功了"
else
  ok "越界直接拒绝，不静默夹到 100"
fi

if grep -q 'where id = \$2 and account = \$1' "$REPO"; then
  ok "写等级带账号条件，改不到别人的角色"
else
  bad "写等级没带账号条件"
fi

if grep -rq 'update "角色"\.character set level' "$ROOT/src/Gateway" 2>/dev/null; then
  bad "网关直接写了等级，角色的唯一写入被绕过"
else
  ok "网关不写等级，只转请求"
fi

echo "== 3. 环境与启动 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env"
  echo
  echo "P1-1b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

cat > "$TMP/character.json" <<JSON
{
  "schema": "character-config/1",
  "listen": "http://127.0.0.1:${CHAR_PORT}",
  "db_pool_max": 4
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
  echo "P1-1b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" "$CHARACTER_DLL" --migrate --config "$TMP/character.json" --sql "$SQL" >"$TMP/migrate-character.log" 2>&1; then
  ok "账号表与角色表都在位"
else
  bad "角色表建不出来"
  tail -5 "$TMP/migrate-character.log" | detail
  echo
  echo "P1-1b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

for acct in "$ACCOUNT" "$OTHER"; do
  printf '%s\n' "$TEST_PASSWORD" \
    | "$DOTNET" "$GATEWAY_DLL" --seed-account "$acct" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
done
ok "两个测试账号已写入：${ACCOUNT} / ${OTHER}"

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
  ok "角色服在 127.0.0.1:${CHAR_PORT} 上"
else
  bad "角色服没起来"
  tail -8 "$TMP/character.log" | detail
  echo
  echo "P1-1b2 核对：$PASS 项通过，$FAIL 项未通过"
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
  echo "P1-1b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

TOKEN="$(login_token "$ACCOUNT")"
OTHER_TOKEN="$(login_token "$OTHER")"
if [ -n "$TOKEN" ] && [ -n "$OTHER_TOKEN" ]; then
  ok "两个账号都能登录"
else
  bad "登录没拿到令牌"
  echo
  echo "P1-1b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 4. 建角色并写等级 =="
CREATE="$(curl -s -m 10 -w '\n%{http_code}' -X POST \
  "http://127.0.0.1:${GATEWAY_PORT}/character?token=${TOKEN}" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"${CHAR_NAME}\",\"race\":\"chen\",\"class_id\":\"warrior\"}")"
split_response "$CREATE"
CHAR_ID="$(json_field "$BODY" character_id)"

if [ "$CODE" = "200" ] && [ -n "$CHAR_ID" ]; then
  ok "测试角色建好：id=${CHAR_ID}"
else
  bad "测试角色没建出来（HTTP ${CODE}）"
  printf '%s\n' "$BODY" | head -3 | detail
  echo
  echo "P1-1b2 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

# 升到 100：应成功。
RESP="$(post_level "$TOKEN" "{\"character_id\":${CHAR_ID},\"level\":100}")"
split_response "$RESP"
LVL="$(json_field "$BODY" level)"
if [ "$CODE" = "200" ] && [ "$LVL" = "100" ]; then
  ok "写到 100 成功：level=100"
else
  bad "写到 100 失败（HTTP ${CODE}，level=${LVL}）"
fi

DB_LEVEL="$(level_in_db "$CHAR_ID")"
if [ "$DB_LEVEL" = "100" ]; then
  ok "库里是 100"
else
  bad "库里不是 100：${DB_LEVEL}"
fi

# 写 101：必须拒绝，而且库里还是 100。
RESP="$(post_level "$TOKEN" "{\"character_id\":${CHAR_ID},\"level\":101}")"
split_response "$RESP"
ERR="$(json_field "$BODY" error)"
if [ "$CODE" = "400" ] && [ "$ERR" = "level_out_of_range" ]; then
  ok "写 101 被拒：HTTP 400 level_out_of_range"
else
  bad "写 101 没被拒：HTTP ${CODE} ${ERR}"
fi

DB_LEVEL="$(level_in_db "$CHAR_ID")"
if [ "$DB_LEVEL" = "100" ]; then
  ok "被拒之后等级停在 100"
else
  bad "被拒之后等级变了：${DB_LEVEL}"
fi

# 0 与负数：同样拒绝。
for bad_level in 0 -5; do
  RESP="$(post_level "$TOKEN" "{\"character_id\":${CHAR_ID},\"level\":${bad_level}}")"
  split_response "$RESP"
  if [ "$CODE" = "400" ]; then
    ok "写 ${bad_level} 被拒：HTTP 400（$(json_field "$BODY" error)）"
  else
    bad "写 ${bad_level} 没被拒：HTTP ${CODE}"
  fi
done

echo "== 5. 别人的角色改不动 =="
RESP="$(post_level "$OTHER_TOKEN" "{\"character_id\":${CHAR_ID},\"level\":50}")"
split_response "$RESP"
ERR="$(json_field "$BODY" error)"
if [ "$CODE" = "404" ] && [ "$ERR" = "character_not_found" ]; then
  ok "别的账号改这个角色：404 character_not_found"
else
  bad "别的账号也能改：HTTP ${CODE} ${ERR}"
fi

DB_LEVEL="$(level_in_db "$CHAR_ID")"
if [ "$DB_LEVEL" = "100" ]; then
  ok "别人的请求没有改动等级：仍是 100"
else
  bad "别人的请求改动了等级：${DB_LEVEL}"
fi

echo "== 6. 绕过接口直写库 =="
RAW="$("$DOTNET" "$CHARACTER_DLL" --raw-level-write "$CHAR_ID" 101 --config "$TMP/character.json" 2>&1)"
if printf '%s' "$RAW" | grep -q 'raw_level=rejected'; then
  ok "直写库 101 被 CHECK 拒掉：$(printf '%s' "$RAW" | grep -m1 'raw_level=rejected' | sed 's/.*sqlstate=/sqlstate=/')"
else
  bad "直写库 101 竟然写进去了"
  printf '%s\n' "$RAW" | head -3 | detail
fi

DB_LEVEL="$(level_in_db "$CHAR_ID")"
if [ "$DB_LEVEL" = "100" ]; then
  ok "直写被拒之后库里仍是 100"
else
  bad "直写之后库里的等级是 ${DB_LEVEL}"
fi

echo "== 7. 不带令牌 =="
RESP="$(post_level "" "{\"character_id\":${CHAR_ID},\"level\":50}")"
split_response "$RESP"
if [ "$CODE" = "401" ]; then
  ok "不带令牌写等级：HTTP 401"
else
  bad "不带令牌也能写：HTTP ${CODE}"
fi

DB_LEVEL="$(level_in_db "$CHAR_ID")"
if [ "$DB_LEVEL" = "100" ]; then
  ok "不带令牌的请求没有改动等级"
else
  bad "不带令牌的请求改动了等级：${DB_LEVEL}"
fi

if grep -q "$TEST_PASSWORD" "$TMP/gateway.log" 2>/dev/null; then
  bad "网关日志里出现了口令"
else
  ok "网关日志里没有口令"
fi

echo
echo "P1-1b2 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
