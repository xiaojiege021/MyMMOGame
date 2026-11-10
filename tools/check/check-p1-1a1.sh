#!/usr/bin/env bash
# P1-1a1 核对：口令换成会话令牌。
#
# 完成标准（开发架构第 14 节第 18 行）：错误口令进不去。正确口令得到令牌。
#
# 做这几段：
#   1. 编译网关
#   2. 静态断言：口令不用明文比较、不进日志、库里不存明文；比较走固定时间；
#      令牌是随机串；配置里不放连接串
#   3. 环境：连 gamedb（MMO_GAMEDB 或 /srv/mmo/gamedb.env），缺了就明确失败
#   4. 建表并写两个用同一口令的账号：同口令的两个哈希必须不同（盐生效）
#   5. 起网关（临时配置，端口 61091，迭代次数调小只为跑得快）
#   6. HTTP 断言：正确口令 200 带令牌，错误口令 401，不存在账号同样 401 且
#      与错误口令的响应逐字相同，空账号/空口令 400，两次登录令牌不同
#   7. 清掉测试账号、停进程
#
# 用法：bash tools/check/check-p1-1a1.sh
# 退出码 0 表示全过，1 表示有不过的项。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"

GATEWAY_DLL="$ROOT/src/Gateway/bin/Release/net10.0/Gateway.dll"
PORT=${MMO_P11A1_PORT:-61091}
ACCOUNT_A="p11a1_alice"
ACCOUNT_B="p11a1_bob"
TEST_PASSWORD="p11a1-Pw-7f3a"

PASS=0
FAIL=0
TMP="$(mktemp -d)"
GATEWAY_PID=""

ok()  { PASS=$((PASS + 1)); printf '  ok   %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL %s\n' "$1"; }
detail() { sed 's/^/       /'; }

cleanup() {
  if [ -n "$GATEWAY_PID" ] && kill -0 "$GATEWAY_PID" 2>/dev/null; then
    kill -TERM "$GATEWAY_PID" 2>/dev/null || true
    for _ in $(seq 1 20); do
      kill -0 "$GATEWAY_PID" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "$GATEWAY_PID" 2>/dev/null || true
    wait "$GATEWAY_PID" 2>/dev/null || true
  fi

  # 测试账号不留痕，别把核对数据混进角色库。
  if [ -n "${HAVE_DB:-}" ]; then
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT_A" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
    printf '%s\n' "" | "$DOTNET" "$GATEWAY_DLL" --delete-account "$ACCOUNT_B" --config "$TMP/gateway.json" >/dev/null 2>&1 || true
  fi

  rm -rf "$TMP"
}
trap cleanup EXIT

echo "== 1. 编译 =="
if [ -z "$DOTNET" ]; then
  bad "找不到 dotnet"
  echo
  echo "P1-1a1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

if "$DOTNET" build "$ROOT/src/Gateway/Gateway.csproj" -c Release --nologo -v q >"$TMP/build.log" 2>&1; then
  ok "网关编译通过"
else
  bad "网关编译失败"
  grep -E "error " "$TMP/build.log" | head -5 | detail
  echo
  echo "P1-1a1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 2. 静态断言 =="
GW="$ROOT/src/Gateway"
SQL="$ROOT/db/character/account.sql"
CFG="$ROOT/config/gateway.json"

if grep -nE '(password|Password)[[:space:]]*==|==[[:space:]]*(password|Password)\b|String\.Equals\([^)]*[Pp]assword' \
     "$GW"/*.cs 2>/dev/null | grep -v 'password_hash' >/dev/null 2>&1; then
  bad "代码里在拿口令做普通相等比较"
else
  ok "口令不用普通相等比较"
fi

if grep -q 'FixedTimeEquals' "$GW/PasswordHasher.cs"; then
  ok "哈希比对走固定时间比较"
else
  bad "哈希比对没有用固定时间比较"
fi

# 日志里出现的应该是账号、结果、令牌长度，不是口令本身。
# seed_error=empty_password 这类错误码不算：它们说的是「口令为空」，没有值。
if grep -nE 'Console\.Write(Line)?\([^)]*[Pp]assword' "$GW"/*.cs 2>/dev/null \
   | grep -viE 'empty_password|password_missing|password_empty' >/dev/null; then
  bad "有把口令写进日志的地方"
else
  ok "口令不进日志"
fi

if grep -qiE '^\s*(password|passwd|pwd|plain)\s+(text|varchar|bytea)' "$SQL"; then
  bad "账号表里有存口令明文的列"
else
  ok "账号表只有哈希、盐和迭代次数"
fi

if grep -q 'password_hash' "$SQL" && grep -q 'password_salt' "$SQL" && grep -q 'iterations' "$SQL"; then
  ok "账号表结构齐：哈希、盐、迭代次数"
else
  bad "账号表缺列"
fi

if grep -q 'RandomNumberGenerator.GetBytes(SaltBytes)' "$GW/PasswordHasher.cs"; then
  ok "盐每行随机生成"
else
  bad "盐不是随机生成，两个同口令账号会撞同一个哈希"
fi

if grep -q 'RandomNumberGenerator.GetBytes(32)' "$GW/TokenStore.cs"; then
  ok "令牌是 32 字节随机串"
else
  bad "令牌不是随机生成"
fi

if grep -qiE 'password=|host=|database=|user id=' "$CFG"; then
  bad "配置文件里出现了连接串内容（密码不能进仓库）"
else
  ok "配置里没有连接串，库口令只从环境读"
fi

echo "== 3. 库环境 =="
if [ -z "${MMO_GAMEDB:-}" ] && [ ! -f /srv/mmo/gamedb.env ]; then
  bad "没有 MMO_GAMEDB，也没有 /srv/mmo/gamedb.env，连库的断言跑不了"
  detail <<'TXT'
       在笔记本上跑：环境文件已在位，直接重跑本脚本。
       在别的机器上跑：export MMO_GAMEDB='Host=...;Port=5432;Database=gamedb;Username=game_dev;Password=...'
TXT
  echo
  echo "P1-1a1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi
HAVE_DB=1
ok "库凭据在位：$( [ -n "${MMO_GAMEDB:-}" ] && echo "env:MMO_GAMEDB" || echo "file:/srv/mmo/gamedb.env" )"

cat > "$TMP/gateway.json" <<JSON
{
  "schema": "gateway-config/1",
  "listen": "http://127.0.0.1:${PORT}",
  "token_ttl_s": 7200,
  "pbkdf2_iterations": 10000
}
JSON

echo "== 4. 建表与种子账号 =="
if "$DOTNET" "$GATEWAY_DLL" --migrate --config "$TMP/gateway.json" --sql "$SQL" >"$TMP/migrate.log" 2>&1 \
   && grep -q '^migrate=ok' "$TMP/migrate.log"; then
  ok "账号表已就位（$(grep -m1 '^migrate=' "$TMP/migrate.log")）"
else
  bad "建表失败"
  tail -5 "$TMP/migrate.log" | detail
  echo
  echo "P1-1a1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

seed_account() {
  local account="$1"
  printf '%s\n' "$TEST_PASSWORD" \
    | "$DOTNET" "$GATEWAY_DLL" --seed-account "$account" --config "$TMP/gateway.json" 2>&1
}

SEED_A="$(seed_account "$ACCOUNT_A")"
SEED_B="$(seed_account "$ACCOUNT_B")"
if printf '%s' "$SEED_A" | grep -q '^seed=ok' && printf '%s' "$SEED_B" | grep -q '^seed=ok'; then
  ok "两个账号已写入（同一口令）：${ACCOUNT_A} / ${ACCOUNT_B}"
else
  bad "种子账号写入失败"
  printf '%s\n%s\n' "$SEED_A" "$SEED_B" | head -6 | detail
fi

STATS_OUT="$("$DOTNET" "$GATEWAY_DLL" --account-stats --config "$TMP/gateway.json" 2>&1)"
STATS_LINE="$(printf '%s\n' "$STATS_OUT" | grep -m1 '^accounts=' || true)"
if [ -n "$STATS_LINE" ]; then
  printf '  %s\n' "$STATS_LINE"
  ACCOUNTS="$(printf '%s' "$STATS_LINE" | tr ' ' '\n' | sed -n 's/^accounts=//p' | head -1)"
  HASHES="$(printf '%s' "$STATS_LINE" | tr ' ' '\n' | sed -n 's/^distinct_hashes=//p' | head -1)"
  SALTS="$(printf '%s' "$STATS_LINE" | tr ' ' '\n' | sed -n 's/^distinct_salts=//p' | head -1)"
  HASH_BYTES="$(printf '%s' "$STATS_LINE" | tr ' ' '\n' | sed -n 's/^hash_bytes=//p' | head -1)"

  if [ -n "$ACCOUNTS" ] && [ "$ACCOUNTS" -ge 2 ] && [ "$HASHES" = "$ACCOUNTS" ]; then
    ok "同一口令的哈希各不相同（盐生效）：${ACCOUNTS} 个账号，${HASHES} 个不同哈希"
  else
    bad "同口令的哈希有重复（盐没生效）：${ACCOUNTS} 个账号，${HASHES} 个不同哈希"
  fi

  if [ "$SALTS" = "$ACCOUNTS" ]; then
    ok "每行一个盐：${SALTS} 个不同盐"
  else
    bad "盐有重复：${ACCOUNTS} 个账号只有 ${SALTS} 个盐"
  fi

  if [ "$HASH_BYTES" = "32..32" ]; then
    ok "库里存的是 32 字节定长哈希，不是口令明文"
  else
    bad "哈希长度异常：${HASH_BYTES}"
  fi
else
  bad "拿不到账号统计"
  printf '%s\n' "$STATS_OUT" | head -4 | detail
fi

echo "== 5. 起网关 =="
: > "$TMP/gateway.log"
"$DOTNET" "$GATEWAY_DLL" --config "$TMP/gateway.json" >>"$TMP/gateway.log" 2>&1 &
GATEWAY_PID=$!

READY=0
for _ in $(seq 1 150); do
  if grep -q '^ready name=Gateway' "$TMP/gateway.log" 2>/dev/null; then
    if curl -s -m 2 "http://127.0.0.1:${PORT}/health" | grep -q '"status":"ok"'; then
      READY=1
      break
    fi
  fi
  if ! kill -0 "$GATEWAY_PID" 2>/dev/null; then
    break
  fi
  sleep 0.2
done

if [ "$READY" = "1" ]; then
  ok "网关在 127.0.0.1:${PORT} 上接客（$(grep -m1 '^db_env=' "$TMP/gateway.log")）"
else
  bad "网关没起来"
  tail -8 "$TMP/gateway.log" | detail
  echo
  echo "P1-1a1 核对：$PASS 项通过，$FAIL 项未通过"
  exit 1
fi

echo "== 6. 口令换令牌 =="
# 返回 "响应体\nHTTP码"，最后一行是状态码。
login() {
  curl -s -m 10 -w '\n%{http_code}' -X POST "http://127.0.0.1:${PORT}/login" \
    -H 'Content-Type: application/json' \
    -d "{\"account\":\"$1\",\"password\":\"$2\"}"
}

split_response() {
  local response="$1"
  BODY="${response%$'\n'*}"
  CODE="${response##*$'\n'}"
}

token_of() {
  printf '%s' "$1" | python3 -c 'import json,sys
try:
    print(json.load(sys.stdin).get("token", ""))
except Exception:
    print("")'
}

FAILED_BODY=""

# 正确口令。
RESP="$(login "$ACCOUNT_A" "$TEST_PASSWORD")"
split_response "$RESP"
TOKEN="$(token_of "$BODY")"
if [ "$CODE" = "200" ] && [ -n "$TOKEN" ]; then
  ok "正确口令得到令牌：长度 ${#TOKEN}"
else
  bad "正确口令没拿到令牌（HTTP ${CODE}）：$(printf '%s' "$BODY" | head -c 120)"
fi

if [ "${#TOKEN}" -ge 43 ]; then
  ok "令牌够长：${#TOKEN} 字符（32 字节随机数转 base64url）"
else
  bad "令牌太短：${#TOKEN}"
fi

# 错误口令。
RESP="$(login "$ACCOUNT_A" "wrong-${TEST_PASSWORD}")"
split_response "$RESP"
FAILED_BODY="$BODY"
if [ "$CODE" = "401" ]; then
  ok "错误口令被拒：HTTP 401"
else
  bad "错误口令没有被拒：HTTP ${CODE}：$(printf '%s' "$BODY" | head -c 120)"
fi

if [ -z "$(token_of "$BODY")" ]; then
  ok "被拒的响应里没有令牌"
else
  bad "错误口令也拿到了令牌"
fi

# 不存在的账号：与错误口令同一口径，不泄漏账号是否存在。
RESP="$(login "p11a1_nobody" "$TEST_PASSWORD")"
split_response "$RESP"
UNKNOWN_BODY="$BODY"
if [ "$CODE" = "401" ]; then
  ok "不存在的账号也是 401"
else
  bad "不存在的账号返回了 ${CODE}"
fi

if [ -n "$FAILED_BODY" ] && [ "$UNKNOWN_BODY" = "$FAILED_BODY" ]; then
  ok "不存在的账号与错误口令响应逐字相同，不泄漏账号是否存在"
else
  bad "两种失败的响应不同：$(printf '%s' "$FAILED_BODY" | head -c 60) vs $(printf '%s' "$UNKNOWN_BODY" | head -c 60)"
fi

# 空账号、空口令：不查库，直接 400。
RESP="$(login "" "$TEST_PASSWORD")"
split_response "$RESP"
if [ "$CODE" = "400" ]; then
  ok "空账号 400"
else
  bad "空账号返回 ${CODE}"
fi

RESP="$(login "$ACCOUNT_A" "")"
split_response "$RESP"
if [ "$CODE" = "400" ]; then
  ok "空口令 400"
else
  bad "空口令返回 ${CODE}"
fi

# 同一账号再登一次，令牌不能是同一个。
RESP="$(login "$ACCOUNT_A" "$TEST_PASSWORD")"
split_response "$RESP"
TOKEN2="$(token_of "$BODY")"
if [ -n "$TOKEN2" ] && [ "$TOKEN2" != "$TOKEN" ]; then
  ok "两次登录拿到不同令牌"
else
  bad "两次登录令牌相同或第二次没拿到"
fi

if grep -q 'login account=p11a1' "$TMP/gateway.log" && ! grep -q "$TEST_PASSWORD" "$TMP/gateway.log"; then
  ok "登录日志里有账号与结果，没有口令"
else
  bad "日志里缺账号记录，或把口令写进了日志"
fi

echo
echo "P1-1a1 核对：$PASS 项通过，$FAIL 项未通过"
[ "$FAIL" -eq 0 ] && exit 0 || exit 1
